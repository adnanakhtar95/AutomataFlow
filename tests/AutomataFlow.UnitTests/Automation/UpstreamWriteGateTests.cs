using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Automation;

/// <summary>
/// The upstream write gate in isolation (docs/PHASE4_PLAN.md §7.6.1–7.6.3, WG5): admission and closing are totally
/// ordered under one lock; a closed gate admits nothing; the drained signal fires only after every admitted write exited.
/// </summary>
public class UpstreamWriteGateTests
{
    [Fact]
    public void A_closed_gate_admits_nothing()
    {
        var sessionLock = new Lock();
        var gate = new UpstreamWriteGate(sessionLock);
        Task drained;
        lock (sessionLock)
        {
            drained = gate.CloseWhileHoldingLock();
        }

        Assert.True(drained.IsCompleted);
        Assert.False(gate.TryEnter());
        Assert.Equal(0, gate.Admitted);
    }

    [Fact]
    public void Drained_fires_only_after_every_write_admitted_before_the_close_has_exited()
    {
        var sessionLock = new Lock();
        var gate = new UpstreamWriteGate(sessionLock);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        Task drained;
        lock (sessionLock)
        {
            drained = gate.CloseWhileHoldingLock();
        }

        Assert.False(gate.TryEnter());
        Assert.False(drained.IsCompleted);
        gate.Exit();
        Assert.False(drained.IsCompleted);
        gate.Exit();
        Assert.True(drained.IsCompleted);
    }

    /// <summary>
    /// WG5: concurrent writers race a close. Invariants for every iteration:
    /// (1) no write is admitted after the close linearized;
    /// (2) when "drained" completes, every admitted write has already finished its "send";
    /// (3) the gate never goes negative.
    /// </summary>
    [Fact]
    public async Task Stress_writers_racing_a_close_never_write_after_the_close_and_always_drain_first()
    {
        const int iterations = 10_000;
        const int writers = 4;
        var random = new Random(20261115);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var sessionLock = new Lock();
            var gate = new UpstreamWriteGate(sessionLock);
            var closed = 0;
            var admittedAfterClose = 0;
            var sendsInFlight = 0;
            var spins = Enumerable.Range(0, writers + 1).Select(_ => random.Next(0, 200)).ToArray();

            var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                Thread.SpinWait(spins[w]);

                // Sound check: if the close was ALREADY complete before this TryEnter started, admission must fail.
                var closedBeforeEnter = Volatile.Read(ref closed) == 1;
                if (!gate.TryEnter())
                {
                    return;
                }

                if (closedBeforeEnter)
                {
                    Interlocked.Increment(ref admittedAfterClose);
                }

                Interlocked.Increment(ref sendsInFlight);
                Thread.SpinWait(spins[w] / 2);   // the "send"
                Interlocked.Decrement(ref sendsInFlight);
                gate.Exit();
            })).ToArray();

            var closer = Task.Run(async () =>
            {
                Thread.SpinWait(spins[writers]);
                Task drained;
                lock (sessionLock)
                {
                    drained = gate.CloseWhileHoldingLock();
                    Volatile.Write(ref closed, 1);
                }

                // Read in-flight sends synchronously at the moment the drained signal fires.
                var atDrain = -1;
                await drained.ContinueWith(_ => atDrain = Volatile.Read(ref sendsInFlight), TaskContinuationOptions.ExecuteSynchronously);
                return atDrain;
            });

            await Task.WhenAll(writerTasks);
            var inFlightAtDrain = await closer;

            Assert.True(admittedAfterClose == 0, $"iteration {iteration}: {admittedAfterClose} write(s) admitted after the close completed");
            Assert.True(inFlightAtDrain == 0, $"iteration {iteration}: {inFlightAtDrain} send(s) still in flight when drained fired");
            Assert.True(gate.Admitted == 0, $"iteration {iteration}: admitted counter is {gate.Admitted}");
            Assert.False(gate.TryEnter());
        }
    }
}
