using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Fencing;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.Infrastructure.Persistence.Journal;
using AutomataFlow.Supervisor.Fencing;
using AutomataFlow.Supervisor.Linux;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Supervisor;

/// <summary>Process exit codes (the container's restart policy starts a new incarnation in every case).</summary>
public static class SupervisorExitCodes
{
    public const int Fenced = 0;
    public const int NothingToFence = 0;
    public const int StartupRefused = 2;
    public const int NotFenced = 3;
}

/// <summary>
/// The process-death supervisor (ADR-059 as amended by ADR-075). Lifecycle of one container: fail-closed startup checks,
/// the containment handshake, monitoring (unit exit, stale heartbeat by the DATABASE clock, startup timeout, graceful stop
/// on SIGTERM), then kill → prove unit empty → drain → ProcessDeathConfirmed, and exit. A fence never restarts anything:
/// the container exits and its restart policy starts a new incarnation; records are re-evaluated from the journal.
/// </summary>
public sealed class SupervisorService(
    IOptions<SupervisorOptions> options,
    IConfiguration configuration,
    IAttemptJournal journal,
    SupervisedIncarnations incarnations,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<SupervisorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Environment.ExitCode = await RunAsync(stoppingToken);
        lifetime.StopApplication();
    }

    private async Task<int> RunAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!OperatingSystem.IsLinux())
        {
            logger.LogCritical("Supervisor startup refused {Problem}", "The process-death supervisor runs on Linux only (no Windows supervisor is delivered).");
            return SupervisorExitCodes.StartupRefused;
        }

        var socketPath = configuration["Containment:SupervisorSocket"];
        var ledgerDirectory = configuration["Automation:EgressProxy:LedgerDirectory"];
        var problems = new List<string>();
        if (SupervisorStartup.MakeNonDumpable() is { } dumpable)
        {
            problems.Add(dumpable);
        }

        problems.AddRange(SupervisorStartup.CheckCredentials(ProcFs.SelfStatus(), o.WorkerUid));
        if (SupervisorStartup.ProbeSeccompProfile() is { } seccomp)
        {
            problems.Add(seccomp);
        }
        if (string.IsNullOrWhiteSpace(socketPath) || string.IsNullOrWhiteSpace(ledgerDirectory))
        {
            problems.Add("Containment:SupervisorSocket and Automation:EgressProxy:LedgerDirectory must be configured.");
        }

        if (problems.Count == 0)
        {
            if (SupervisorStartup.ProbeExactCookieDestroy() is { } probe)
            {
                problems.Add("The exact-cookie destroy probe failed: " + probe);
            }
            else
            {
                logger.LogInformation("Exact-cookie destroy probe passed (a stale cookie was refused, the exact cookie destroyed)");
            }
        }

        var (unit, unitProblems) = await LinuxUnit.AttachAsync(o.UnitWrapperPid, o.WorkerUid, TimeSpan.FromSeconds(30), CancellationToken.None);
        problems.AddRange(unitProblems);
        if (unit?.Scan() is { } initial && (!initial.Complete || initial.ForeignTasks.Count > 0))
        {
            problems.Add("The container holds processes other than the unit and the supervisor (it must be dedicated to one incarnation).");
        }

        UnitIdentity? identity = unit is null ? null : new UnitIdentity(o.WorkerUid, unit.UserNamespace, unit.PidNamespace,
            new ContainmentUnit(ContainmentKind.LinuxNamespaces, $"{Environment.MachineName}/{unit.UserNamespace}/{unit.PidNamespace}/{Guid.NewGuid():N}"),
            Path.GetFullPath(ledgerDirectory ?? "/"));
        foreach (var problem in problems)
        {
            logger.LogCritical("Supervisor startup refused {Problem}", problem);
        }

        using var serverStop = new CancellationTokenSource();
        using var server = string.IsNullOrWhiteSpace(socketPath) ? null
            : new HandshakeServer(socketPath, problems.Count == 0 ? identity : null, problems.Count == 0 ? null : "supervisor-startup-refused", logger);
        server?.Start(serverStop.Token);

        if (unit is null)
        {
            _ = Native.kill(o.UnitWrapperPid, Native.SIGKILL); // cannot attach: never leave an unsupervised unit running
            return SupervisorExitCodes.StartupRefused;
        }

        // A refused start keeps answering the handshake with the refusal until the Worker gave up (it then exits) or the
        // startup timeout passed; the unit is killed and proven gone either way.
        var trigger = problems.Count > 0 ? await AwaitRefusedUnitAsync(unit, stoppingToken) : await MonitorAsync(unit, identity!, stoppingToken);
        logger.LogWarning("Unit termination {Trigger} {ContainmentUnit}", trigger, identity!.Unit.Name);
        await serverStop.CancelAsync();
        return await FenceAsync(unit, identity, ledgerDirectory ?? string.Empty, problems.Count > 0);
    }

    private async Task<string> AwaitRefusedUnitAsync(LinuxUnit unit, CancellationToken stoppingToken)
    {
        var deadline = clock.GetUtcNow().AddSeconds(options.Value.StartupTimeoutSeconds);
        while (unit.WrapperRunning() && clock.GetUtcNow() < deadline && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(200, CancellationToken.None);
        }

        return "supervisor-startup-refused";
    }

    private async Task<string> MonitorAsync(LinuxUnit unit, UnitIdentity identity, CancellationToken stoppingToken)
    {
        var o = options.Value;
        var started = clock.GetUtcNow();
        while (true)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                // Graceful stop: the Worker cancels its attempts (barrier-fenced completions), then the unit is proven gone.
                unit.Terminate();
                var deadline = clock.GetUtcNow().AddSeconds(o.GracefulStopSeconds);
                while (unit.WrapperRunning() && clock.GetUtcNow() < deadline)
                {
                    await Task.Delay(100, CancellationToken.None);
                }

                return "graceful-stop";
            }

            if (!unit.WrapperRunning())
            {
                return "unit-exited";
            }

            try
            {
                var registered = await incarnations.FindAsync(identity.Unit, stoppingToken);
                if (registered is null && clock.GetUtcNow() - started > TimeSpan.FromSeconds(o.StartupTimeoutSeconds))
                {
                    return "worker-never-registered";
                }

                if (registered is not null && registered.DatabaseNow - registered.LastHeartbeatAt > TimeSpan.FromSeconds(o.HeartbeatStaleSeconds))
                {
                    return "heartbeat-stale"; // frozen or partitioned: it may still act, so it is killed and proven gone
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("Incarnation liveness read failed {ExceptionType}", ex.GetType().Name); // no decision without data
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(o.MonitorIntervalMilliseconds), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<int> FenceAsync(LinuxUnit unit, UnitIdentity identity, string ledgerDirectory, bool startupRefused)
    {
        var o = options.Value;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(o.FenceConfirmTimeoutSeconds + (o.DrainPolls * o.DrainPollMilliseconds / 1000) + 60));
        WorkerIncarnation? incarnation = null;
        var lookupFailed = false;
        try
        {
            incarnation = (await incarnations.FindAsync(identity.Unit, budget.Token))?.Incarnation;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lookupFailed = true;
            logger.LogError("Incarnation lookup failed before the fence {ExceptionType}", ex.GetType().Name);
        }

        var table = NamespaceSocketTable.System();
        var policy = new SupervisorPolicy(TimeSpan.FromSeconds(o.FenceConfirmTimeoutSeconds), TimeSpan.FromMilliseconds(100),
            new DrainPolicy(o.DrainPolls, TimeSpan.FromMilliseconds(o.DrainPollMilliseconds), 3));
        var outcome = await new FenceProcedure(journal, unit, ConnectionLedgerReader.Read, table, table, policy, clock, logger)
            .RunAsync(incarnation, ledgerDirectory, budget.Token);
        if (lookupFailed && outcome.Kind == FenceResultKind.NothingToFence)
        {
            outcome = new FenceOutcome(FenceResultKind.NotFenced, "incarnation-lookup-failed");
        }

        logger.Log(outcome.Kind == FenceResultKind.NotFenced ? LogLevel.Critical : LogLevel.Information,
            "Process-death fence {Outcome} {Reason} {IncarnationId} {LedgerEntries} {SocketsDestroyed} {FencedAttempts}",
            outcome.Kind, outcome.Reason, incarnation?.Id.Value, outcome.Drain?.LedgerEntries, outcome.Drain?.SocketsDestroyed, outcome.Confirmation?.FencedAttempts.Length);
        foreach (var foreign in table.LastForeign)
        {
            logger.LogWarning("Socket held by a live process other than the supervisor {Local} {Remote} {State} {Inode}", foreign.Local, foreign.Remote, foreign.State, foreign.Inode);
        }

        return outcome.Kind switch
        {
            _ when startupRefused => SupervisorExitCodes.StartupRefused,
            FenceResultKind.Fenced => SupervisorExitCodes.Fenced,
            FenceResultKind.NothingToFence => SupervisorExitCodes.NothingToFence,
            _ => SupervisorExitCodes.NotFenced,
        };
    }
}
