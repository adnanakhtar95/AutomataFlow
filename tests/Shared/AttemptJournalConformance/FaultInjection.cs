using AutomataFlow.Application.Workflows.Execution;

namespace AutomataFlow.Tests.Journal;

/// <summary>J2: the checkpoint write fails before anything is stored.</summary>
public sealed class CheckpointThrowsJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    public override Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken) =>
        throw new AttemptJournalException("Simulated journal outage before the checkpoint was stored.");
}

/// <summary>J3: the checkpoint is stored durably, then the acknowledgement is lost (the caller sees an exception).</summary>
public sealed class CheckpointAckLostJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    public override async Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken)
    {
        await base.SideEffectStartingAsync(reservation, stepId, stepTimeout, cancellationToken);
        throw new AttemptJournalException("Simulated lost acknowledgement after the checkpoint was stored.");
    }
}

/// <summary>J5: completion fails (diagnostic only for the executor; the entry later becomes Abandoned).</summary>
public sealed class CompleteThrowsJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    public override Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken) =>
        throw new AttemptJournalException("Simulated completion failure.");
}

/// <summary>The first completion is durably stored but its acknowledgement is lost; later calls behave normally.</summary>
public sealed class CompleteAckLostOnceJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    private int _calls;

    public override async Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken)
    {
        var result = await base.CompleteAsync(reservation, completion, cancellationToken);
        return Interlocked.Increment(ref _calls) == 1 ? throw new AttemptJournalException("Simulated lost acknowledgement after the completion was stored.") : result;
    }
}

/// <summary>The first completion fails before anything is stored; the retry succeeds.</summary>
public sealed class CompleteFailsOnceJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    private int _calls;

    public override Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _calls) == 1 ? throw new AttemptJournalException("Simulated transient outage.") : base.CompleteAsync(reservation, completion, cancellationToken);
}

/// <summary>Every completion is refused with InvalidState (nothing is stored); counts the calls.</summary>
public sealed class CompleteInvalidStateJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    private int _calls;

    public int CompleteCalls => Volatile.Read(ref _calls);

    public override Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        throw new AttemptJournalException(JournalErrorCodes.InvalidState, "Simulated refusal.");
    }
}

/// <summary>A transient failure first (nothing stored), then InvalidState refusals: the retry must NOT be read as "recorded".</summary>
public sealed class CompleteFailsThenInvalidStateJournal(TimeProvider clock, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    private int _calls;

    public override Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _calls) == 1
            ? throw new AttemptJournalException("Simulated transient outage.")
            : throw new AttemptJournalException(JournalErrorCodes.InvalidState, "Simulated refusal without a stored completion.");
}

/// <summary>The observable state of a fake containment unit (processes and ledgered upstream sockets).</summary>
public sealed class FakeContainmentUnit
{
    /// <summary>Whether killing the unit makes it report empty (false: a task stuck in uninterruptible sleep).</summary>
    public bool EmptiesWhenKilled { get; init; } = true;

    public int LedgeredConnections { get; init; } = 2;

    /// <summary>How many ledgered sockets the drain can bring to a safe state.</summary>
    public int DrainableConnections { get; init; } = 2;

    public bool LedgerReadable { get; init; } = true;

    public bool Killed { get; private set; }

    public bool Empty => Killed && EmptiesWhenKilled;

    public void Kill() => Killed = true;
}

/// <summary>
/// A fake process-death supervisor (§7.5.3) driving the real journal contract: record the kill request, kill the unit,
/// require positive proof (unit empty AND a complete socket drain) and only then report evidence. Any missing proof
/// means no confirmation at all (fail closed).
/// </summary>
public sealed class FakeSupervisor(IAttemptJournal journal, TimeProvider supervisorClock)
{
    public async Task<FenceConfirmationResult?> TryConfirmProcessDeathAsync(WorkerIncarnation incarnation, FakeContainmentUnit unit, CancellationToken cancellationToken = default)
    {
        await journal.RecordKillRequestAsync(incarnation.Id, cancellationToken);
        unit.Kill();
        if (!unit.Empty || !unit.LedgerReadable)
        {
            return null; // no positive proof → no fence; the attempts stay AwaitingFence
        }

        var drain = new DrainReport(EnumerationComplete: true, unit.LedgeredConnections, Math.Min(unit.DrainableConnections, unit.LedgeredConnections), OwnershipAmbiguous: false);
        if (!drain.ProvesNoFurtherTransmission)
        {
            return null;
        }

        var evidence = new FenceEvidence(FenceSource.ProcessDeathConfirmed, incarnation.Id, incarnation.Unit, FenceMethod.SupervisorUnitEmptyAndDrained,
            supervisorClock.GetUtcNow(), drain, null, null);
        return await journal.ConfirmFenceAsync(evidence, cancellationToken);
    }
}
