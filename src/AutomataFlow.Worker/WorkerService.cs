using System.Collections.Concurrent;
using System.Diagnostics;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Infrastructure.Persistence.Journal;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Worker;

/// <summary>Stable host identity for the incarnation record (§7.5.1).</summary>
public static class HostIdentity
{
    public static string HostId() =>
        OperatingSystem.IsLinux() && File.Exists("/etc/machine-id") && File.ReadAllText("/etc/machine-id").Trim() is { Length: > 0 } id ? id : Environment.MachineName;

    /// <summary>
    /// Linux: the kernel's boot id. Windows: the boot time (seconds precision), derived from the uptime counter; the
    /// supervisor compares it with a tolerance before ever claiming a reboot (step 7).
    /// </summary>
    public static string BootId() => OperatingSystem.IsLinux()
        ? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()
        : "boot-time:" + (DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Startup checks (ADR-058 rule 2, §7.4): a Worker executes nothing unless every check passes. There is no override.
/// </summary>
public sealed class WorkerStartup(IServiceProvider services, IAttemptJournal journal, IConfiguration configuration, IOptions<EgressProxyOptions> egress)
{
    /// <summary>The longest permitted side-effect step (WorkflowLimits.MaxStepTimeoutMs).</summary>
    public static readonly TimeSpan LongestSideEffectStep = TimeSpan.FromSeconds(120);

    public async Task<(WorkerIncarnation? Incarnation, IReadOnlyList<string> Problems)> CheckAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var capabilities = journal.Capabilities;
        if (!capabilities.IsDurable || !capabilities.SupportsFencing)
        {
            problems.Add("The attempt journal is not durable and fencing.");
        }

        if (capabilities.LeaseLength < LongestSideEffectStep + capabilities.LeaseSafetyMargin || capabilities.LeaseSafetyMargin <= TimeSpan.Zero)
        {
            problems.Add("The journal lease does not cover the longest side-effect step (120 s) plus the safety margin.");
        }

        if (journal is PostgresAttemptJournal postgres && !await postgres.VerifyServerDurabilityAsync(cancellationToken))
        {
            problems.Add("The PostgreSQL server does not flush its WAL (fsync is off).");
        }

        if (string.IsNullOrWhiteSpace(configuration["DataProtection:KeysDirectory"]))
        {
            problems.Add("DataProtection:KeysDirectory is not configured (the key ring must be shared with the Api).");
        }

        if (string.IsNullOrWhiteSpace(configuration["Artifacts:RootDirectory"]))
        {
            problems.Add("Artifacts:RootDirectory is not configured.");
        }

        ContainmentUnit? unit = null;
        if (services.GetService<IContainmentHandshake>() is not { } handshake)
        {
            problems.Add("No process-death supervisor containment handshake is configured.");
        }
        else
        {
            try
            {
                unit = await handshake.VerifyAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add("The containment handshake failed: " + ex.Message);
            }
        }

        if (problems.Count > 0 || unit is null)
        {
            return (null, problems);
        }

        using var process = Process.GetCurrentProcess();
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), HostIdentity.HostId(), HostIdentity.BootId(), Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero), unit, egress.Value.LedgerDirectory ?? string.Empty);
        try
        {
            await journal.RegisterIncarnationAsync(incarnation, cancellationToken);
        }
        catch (AttemptJournalException)
        {
            return (null, ["The Worker incarnation could not be registered in the journal."]);
        }

        return (incarnation, []);
    }
}

/// <summary>
/// The Worker loop (docs/ARCHITECTURE.md §5): fail-closed startup, then claim → process with bounded concurrency, a
/// heartbeat (incarnation liveness, claim renewal, cancellation polling) and recovery of expired claims. On shutdown the
/// running attempts are cancelled (Cancelled or Indeterminate per the engine's rules) and their records rescheduled.
/// </summary>
public sealed class WorkerService(
    WorkerStartup startup,
    WorkerIncarnationHolder holder,
    RecordDispatcher dispatcher,
    RecordProcessor processor,
    IAttemptJournal journal,
    IOptions<WorkerOptions> options,
    ILogger<WorkerService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Running> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (incarnation, problems) = await startup.CheckAsync(stoppingToken);
        if (incarnation is null)
        {
            foreach (var problem in problems)
            {
                logger.LogCritical("Worker startup refused {Problem}", problem);
            }

            throw new InvalidOperationException("Worker startup refused (fail closed): " + string.Join(" ", problems));
        }

        holder.Set(incarnation);
        logger.LogInformation("Worker started {IncarnationId} {HostId} {ContainmentUnit}", incarnation.Id.Value, incarnation.HostId, incarnation.Unit.Name);
        var o = options.Value;
        using var slots = new SemaphoreSlim(o.MaxConcurrentAttempts, o.MaxConcurrentAttempts);
        var heartbeat = Task.Run(() => HeartbeatAsync(incarnation, stoppingToken), CancellationToken.None);
        var recovery = Task.Run(() => RecoveryAsync(stoppingToken), CancellationToken.None);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (slots.CurrentCount > 0)
                {
                    IReadOnlyList<ClaimedRecord> claims = [];
                    try
                    {
                        claims = await dispatcher.ClaimAsync(incarnation.Id.Value, slots.CurrentCount, TimeSpan.FromSeconds(o.ClaimLeaseSeconds), stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning("Claiming failed {ExceptionType}", ex.GetType().Name);
                    }

                    foreach (var claim in claims)
                    {
                        await slots.WaitAsync(stoppingToken);
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await processor.ProcessAsync(claim, cts.Token);
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                logger.LogError("Record processing failed {JobRecordId} {ExceptionType}", claim.Id, ex.GetType().Name);
                            }
                            finally
                            {
                                _running.TryRemove(claim.Id, out _);
                                cts.Dispose();
                                slots.Release();
                            }
                        }, CancellationToken.None);
                        _running[claim.Id] = new Running(claim, cts, task);
                    }
                }

                await Task.Delay(o.PollIntervalMilliseconds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        foreach (var running in _running.Values)
        {
            await running.Cancellation.CancelAsync();
        }

        await Task.WhenAll(_running.Values.Select(r => r.Task).Append(heartbeat).Append(recovery));
        logger.LogInformation("Worker stopped {IncarnationId}", incarnation.Id.Value);
    }

    private async Task HeartbeatAsync(WorkerIncarnation incarnation, CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(o.HeartbeatSeconds), stoppingToken);
                await journal.HeartbeatAsync(incarnation.Id, stoppingToken);
                var snapshot = _running.Values.ToList();
                var lost = await dispatcher.RenewAsync(incarnation.Id.Value, [.. snapshot.Select(r => r.Claim.Id)], TimeSpan.FromSeconds(o.ClaimLeaseSeconds), stoppingToken);
                var cancelled = await dispatcher.CancelRequestedAsync([.. snapshot.Select(r => r.Claim.JobId).Distinct()], stoppingToken);
                foreach (var running in snapshot.Where(r => lost.Contains(r.Claim.Id) || cancelled.Contains(r.Claim.JobId)))
                {
                    logger.LogInformation("Stopping attempt {JobRecordId} {Reason}", running.Claim.Id, lost.Contains(running.Claim.Id) ? "ClaimLost" : "JobCancelled");
                    await running.Cancellation.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("Heartbeat failed {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private async Task RecoveryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.RecoveryIntervalSeconds), stoppingToken);
                if (await dispatcher.RecoverExpiredAsync(stoppingToken) is > 0 and var recovered)
                {
                    logger.LogInformation("Recovered expired record claims {Count}", recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("Claim recovery failed {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private sealed record Running(ClaimedRecord Claim, CancellationTokenSource Cancellation, Task Task);
}
