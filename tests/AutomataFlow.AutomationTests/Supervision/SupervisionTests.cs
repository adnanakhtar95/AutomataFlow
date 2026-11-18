using System.Net;
using System.Net.Sockets;
using AutomataFlow.AutomationTests.Platform;
using AutomataFlow.Supervisor;

namespace AutomataFlow.AutomationTests.Supervision;

/// <summary>
/// Phase 5 step 7 on REAL processes (ADR-075): the production launcher, supervisor and Worker in the supervision container
/// (production posture), real kernels, real sockets. Each test leaves the container empty for the next one (sequential).
/// The P-test mapping is in docs/PHASE5_PLAN.md.
/// </summary>
[Collection(SupervisionGroup.Name)]
public sealed class SupervisionTests(SupervisionFixture fixture)
{
    private SupervisionAgent Agent => fixture.Agent;

    private static readonly TimeSpan Exit = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task The_supervisor_and_the_worker_run_with_exactly_the_approved_privileges_and_stop_gracefully_with_a_fence()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        var before = await SupervisionQueries.IncarnationCountAsync(rig);
        var launch = await Agent.LaunchAsync(SupervisionFixture.Environment(rig, SupervisionPaths.Worker));
        try
        {
            var (incarnation, kind) = await SupervisionQueries.WaitForIncarnationAsync(rig, before, TimeSpan.FromSeconds(120), launch);
            Assert.Equal("LinuxNamespaces", kind);
            Assert.Contains("Exact-cookie destroy probe passed", launch.Log, StringComparison.Ordinal); // P11b on this kernel, at startup

            // Supervisor: its own uid, CAP_NET_ADMIN only (0x1000), bounding set included, no_new_privs.
            var supervisor = await Agent.StatusOfAsync(launch.SupervisorPid);
            Assert.Matches(@"Uid:\s+10002\s+10002\s+10002\s+10002", supervisor);
            Assert.Matches(@"CapEff:\s+0000000000001000", supervisor);
            Assert.Matches(@"CapPrm:\s+0000000000001000", supervisor);
            Assert.Matches(@"CapBnd:\s+0000000000001000", supervisor);
            Assert.Matches(@"NoNewPrivs:\s+1", supervisor);

            // Every unit process (init, Worker, ...): the Worker's uid, NO capability in any set, no_new_privs, nested PID namespace.
            var unit = await Agent.UnitProcessesAsync();
            Assert.NotEmpty(unit);
            foreach (var process in unit)
            {
                var status = await Agent.StatusOfAsync(process.Pid);
                Assert.Matches(@"CapEff:\s+0000000000000000", status);
                Assert.Matches(@"CapPrm:\s+0000000000000000", status);
                Assert.Matches(@"CapBnd:\s+0000000000000000", status);
                Assert.Matches(@"CapAmb:\s+0000000000000000", status);
                Assert.Matches(@"NoNewPrivs:\s+1", status);
                Assert.Matches(@"NSpid:\s+\d+\s+\d+", status);
            }

            // The Worker's uid cannot exercise the supervisor's privileges: it may not signal it, read its environment
            // (the database credentials) or its memory, even from outside the unit where it can name the supervisor's PID.
            Assert.NotEqual(0, (await Agent.SignalAsync(SupervisionAgent.WorkerUid, "0", launch.SupervisorPid)).ExitCode);
            Assert.NotEqual(0, (await Agent.ExecAsync(SupervisionAgent.WorkerUid, "cat", $"/proc/{launch.SupervisorPid}/environ")).ExitCode);
            Assert.NotEqual(0, (await Agent.ExecAsync(SupervisionAgent.WorkerUid, "head", "-c1", $"/proc/{launch.SupervisorPid}/mem")).ExitCode);

            // Graceful stop (docker stop -> tini -> SIGTERM): the Worker shuts down, then the unit is proven gone and fenced.
            await Agent.SignalAsync(SupervisionAgent.SupervisorUid, "TERM", launch.SupervisorPid);
            await launch.AssertExitAsync(SupervisorExitCodes.Fenced);
            Assert.Contains("graceful-stop", launch.Log, StringComparison.Ordinal);
            Assert.Contains("Worker stopped", launch.Log, StringComparison.Ordinal);
            Assert.Equal((true, "ProcessDeathConfirmed", "SupervisorUnitEmptyAndDrained", (string?)null), await SupervisionQueries.FenceConfirmationAsync(rig, incarnation));
            Assert.Empty(await Agent.UnitProcessesAsync());
        }
        finally
        {
            await launch.CleanUpAsync();
        }
    }

    [Fact]
    public async Task P9_without_CAP_NET_ADMIN_the_handshake_is_refused_and_the_worker_never_starts()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        var environment = SupervisionFixture.Environment(rig, SupervisionPaths.Worker);
        // The supervisor drops its ambient CAP_NET_ADMIN before it starts: it can no longer destroy sockets.
        environment["AF_SUPERVISOR_COMMAND"] = "setpriv --inh-caps=-net_admin --ambient-caps=-net_admin -- " + SupervisionPaths.Supervisor;
        var launch = await Agent.LaunchAsync(environment);
        try
        {
            await launch.AssertExitAsync(SupervisorExitCodes.StartupRefused);
            Assert.Contains("CAP_NET_ADMIN", launch.Log, StringComparison.Ordinal);
            Assert.Contains("Worker startup refused", launch.Log, StringComparison.Ordinal);
            Assert.Contains("supervisor-startup-refused", launch.Log, StringComparison.Ordinal);
            Assert.Equal(0, await SupervisionQueries.IncarnationCountAsync(rig)); // nothing registered, nothing executed
            Assert.Empty(await Agent.UnitProcessesAsync());
        }
        finally
        {
            await launch.CleanUpAsync();
        }
    }

    [Fact]
    public async Task P4_a_worker_outside_a_unit_refuses_to_start()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        await using var processes = new PlatformProcesses(rig);
        var worker = processes.StartProductionWorker(new() { ["Containment:SupervisorSocket"] = "/tmp/no-supervisor.sock" });
        Assert.True(await worker.WaitForExitAsync(TimeSpan.FromSeconds(90)), "the Worker kept running outside a unit");
        Assert.Contains(worker.Output, l => l.Contains("Worker startup refused", StringComparison.Ordinal));
        Assert.Contains(worker.Output, l => l.Contains("capabilities", StringComparison.Ordinal) || l.Contains("nested PID namespace", StringComparison.Ordinal));
        Assert.Equal(0, await SupervisionQueries.IncarnationCountAsync(rig));
    }

    [Fact]
    public async Task P7_P11f_a_lingering_socket_to_a_zero_window_peer_is_destroyed_by_cookie_before_the_fence_and_other_containers_are_untouched()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        using var server = new PeerServer(readBytesPerTick: 0);     // accepts, never reads: zero window
        using var bystander = new PeerServer(readBytesPerTick: 65536);
        using var bystanderClient = new TcpClient();
        await bystanderClient.ConnectAsync("tests", bystander.Port); // a connection in ANOTHER container's namespace

        var launch = await Agent.LaunchAsync(SupervisionFixture.Environment(rig, SupervisionPaths.SocketHolder("tests", server.Port, "normal")));
        try
        {
            var incarnation = Guid.Parse(UnitLaunch.HolderReady().Match(await launch.WaitForLogAsync("HOLDER-READY", TimeSpan.FromSeconds(120))).Groups["inc"].Value);
            await server.WaitForStallAsync();
            var buffered = server.Buffered;

            await KillUnitWorkerAsync();
            await launch.AssertExitAsync(SupervisorExitCodes.Fenced);
            Assert.Contains("unit-exited", launch.Log, StringComparison.Ordinal);
            Assert.Equal((true, "ProcessDeathConfirmed", "SupervisorUnitEmptyAndDrained", (string?)null), await SupervisionQueries.FenceConfirmationAsync(rig, incarnation));

            // The orphan (FIN_WAIT1 with a full send queue) was destroyed: the peer gets a reset, not the queued bytes and a FIN.
            var (reset, afterResume) = await server.ResumeAndReadToEndAsync();
            Assert.True(reset, "the peer saw a clean FIN: the queued data was still delivered (the socket was not destroyed)");
            Assert.True(afterResume <= buffered + 1_048_576, $"the peer kept receiving after the fence: {afterResume} bytes (buffered {buffered})");

            // The supervisor's capability is scoped to its own container: a connection in this container is unaffected.
            await bystanderClient.GetStream().WriteAsync(new byte[] { 1, 2, 3 });
            await Task.Delay(500);
            Assert.True(bystanderClient.Connected && bystander.Received >= 3);
        }
        finally
        {
            await launch.CleanUpAsync();
        }
    }

    [Fact]
    public async Task P8_a_trickling_peer_keeps_receiving_from_the_dead_orphan_until_the_drain_destroys_it()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        using var server = new PeerServer(readBytesPerTick: 512); // reads a little every 20 ms and keeps acknowledging

        var launch = await Agent.LaunchAsync(SupervisionFixture.Environment(rig, SupervisionPaths.SocketHolder("tests", server.Port, "normal")));
        try
        {
            await launch.WaitForLogAsync("HOLDER-READY", TimeSpan.FromSeconds(120));
            await Task.Delay(2_000);
            await KillUnitWorkerAsync();
            var atKill = server.Received;
            await launch.AssertExitAsync(SupervisorExitCodes.Fenced);

            Assert.True(await server.WaitForResetAsync(TimeSpan.FromSeconds(10)), "the trickling connection was never reset");
            var atReset = server.Received;
            Assert.True(atReset > atKill, "the dead Worker's orphan kept transmitting after its process died (TCP user timeout is no deadline)");
            await Task.Delay(1_000);
            Assert.Equal(atReset, server.Received); // nothing after the destroy
        }
        finally
        {
            await launch.CleanUpAsync();
        }
    }

    [Theory]
    [InlineData("corrupt-ledger", "ledger-corrupt")]     // P10
    [InlineData("wrong-cookie", "identity-mismatch")]     // P11c: same tuple, another cookie => ambiguity, nothing destroyed
    public async Task P10_P11c_ledger_damage_or_identity_ambiguity_gives_no_fence(string variant, string reason)
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        using var server = new PeerServer(readBytesPerTick: 0);

        var launch = await Agent.LaunchAsync(SupervisionFixture.Environment(rig, SupervisionPaths.SocketHolder("tests", server.Port, variant)));
        try
        {
            var incarnation = Guid.Parse(UnitLaunch.HolderReady().Match(await launch.WaitForLogAsync("HOLDER-READY", TimeSpan.FromSeconds(120))).Groups["inc"].Value);
            await server.WaitForStallAsync();
            await KillUnitWorkerAsync();

            await launch.AssertExitAsync(SupervisorExitCodes.NotFenced);
            Assert.Contains(reason, launch.Log, StringComparison.Ordinal);
            Assert.Null(await SupervisionQueries.FenceConfirmationAsync(rig, incarnation)); // never reported
            if (variant == "wrong-cookie")
            {
                Assert.False(await server.WaitForResetAsync(TimeSpan.FromSeconds(2)), "an ambiguous connection must never be destroyed");
            }
        }
        finally
        {
            await launch.CleanUpAsync();
        }
    }

    /// <summary>SIGKILL of the unit's Worker process alone, as the Worker's uid (orphans are the kernel's and the supervisor's job).</summary>
    private async Task KillUnitWorkerAsync()
    {
        var worker = (await Agent.UnitProcessesAsync()).Where(p => p.Name == "dotnet").ToList();
        Assert.NotEmpty(worker);
        Assert.Equal(0, (await Agent.SignalAsync(SupervisionAgent.WorkerUid, "KILL", [.. worker.Select(p => p.Pid)])).ExitCode);
    }
}

/// <summary>A TCP peer in this (the tests) container: reads <c>readBytesPerTick</c> bytes every 20 ms (0 = never).</summary>
internal sealed class PeerServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Socket> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reset = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _received;
    private volatile bool _resume;

    public PeerServer(int readBytesPerTick)
    {
        _listener.Server.ReceiveBufferSize = 65536;
        _listener.Start();
        _ = Task.Run(async () =>
        {
            var socket = await _listener.AcceptSocketAsync(_stop.Token);
            _accepted.TrySetResult(socket);
            var buffer = new byte[65536];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var take = _resume ? buffer.Length : readBytesPerTick;
                    if (take == 0)
                    {
                        await Task.Delay(20, _stop.Token);
                        continue;
                    }

                    var read = await socket.ReceiveAsync(buffer.AsMemory(0, take), SocketFlags.None, _stop.Token);
                    if (read == 0)
                    {
                        return; // clean FIN
                    }

                    Interlocked.Add(ref _received, read);
                    if (!_resume)
                    {
                        await Task.Delay(20, _stop.Token);
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                _reset.TrySetResult();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public long Received => Interlocked.Read(ref _received);

    /// <summary>Bytes the kernel holds for us unread (what the dead sender had already delivered).</summary>
    public int Buffered => _accepted.Task.IsCompletedSuccessfully ? _accepted.Task.Result.Available : 0;

    public async Task WaitForStallAsync()
    {
        var socket = await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var last = -1;
        for (var i = 0; i < 100 && socket.Available != last; i++)
        {
            last = socket.Available;
            await Task.Delay(200); // the receive buffer stops growing once the window is closed
        }
    }

    public async Task<bool> WaitForResetAsync(TimeSpan timeout)
    {
        try
        {
            await _reset.Task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Starts reading everything: returns whether the connection ended in a reset, and how much arrived.</summary>
    public async Task<(bool Reset, long Received)> ResumeAndReadToEndAsync()
    {
        _resume = true;
        var reset = await WaitForResetAsync(TimeSpan.FromSeconds(20));
        return (reset, Received);
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_accepted.Task.IsCompletedSuccessfully)
        {
            _accepted.Task.Result.Dispose();
        }

        _listener.Stop();
        _stop.Dispose();
    }
}
