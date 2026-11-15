using System.Collections.Concurrent;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.Infrastructure.Security;

namespace AutomataFlow.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Worker-wide concurrent attempts (browser sessions); per-job limits are enforced at claim time.</summary>
    public int MaxConcurrentAttempts { get; set; } = 2;

    public int PollIntervalMilliseconds { get; set; } = 1_000;

    /// <summary>Record claim lease; renewed by the heartbeat. Distinct from the journal's attempt lease.</summary>
    public int ClaimLeaseSeconds { get; set; } = 60;

    public int HeartbeatSeconds { get; set; } = 10;

    public int RecoveryIntervalSeconds { get; set; } = 15;

    /// <summary>How long an unfenced earlier attempt may block a record before it becomes NeedsReview (ADR-056).</summary>
    public int FenceWaitSeconds { get; set; } = 900;

    public int FenceRecheckSeconds { get; set; } = 30;

    public static bool IsValid(WorkerOptions o) =>
        o.MaxConcurrentAttempts is >= 1 and <= 64 && o.PollIntervalMilliseconds is >= 50 and <= 60_000 && o.ClaimLeaseSeconds is >= 10 and <= 3_600
        && o.HeartbeatSeconds >= 1 && o.HeartbeatSeconds * 3 <= o.ClaimLeaseSeconds && o.RecoveryIntervalSeconds is >= 1 and <= 3_600
        && o.FenceWaitSeconds is >= 60 and <= 86_400 && o.FenceRecheckSeconds is >= 5 and <= 3_600;
}

/// <summary>
/// Collects the engine's execution events per attempt (organization, record, attempt number) until the attempt is
/// persisted. Entries are already redacted by the browser session. Bounded per attempt; overflow is counted, not stored.
/// </summary>
public sealed class ExecutionLogBuffer : IExecutionLogSink
{
    public const int MaxEntriesPerAttempt = 2_000;

    private readonly ConcurrentDictionary<(Guid Org, Guid Record, int Attempt), Bucket> _buckets = new();

    public ValueTask WriteAsync(ExecutionLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Scope.JobRecordId is { } record)
        {
            var bucket = _buckets.GetOrAdd((entry.Scope.OrganizationId, record, entry.Scope.AttemptNumber), _ => new Bucket());
            lock (bucket)
            {
                if (bucket.Entries.Count < MaxEntriesPerAttempt)
                {
                    bucket.Entries.Add(entry);
                }
                else
                {
                    bucket.Dropped++;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes and returns an attempt's events (plus a marker entry when some were dropped).</summary>
    public IReadOnlyList<ExecutionLogEntry> Drain(Guid organizationId, Guid recordId, int attemptNumber)
    {
        if (!_buckets.TryRemove((organizationId, recordId, attemptNumber), out var bucket))
        {
            return [];
        }

        lock (bucket)
        {
            if (bucket.Dropped > 0 && bucket.Entries.Count > 0)
            {
                var last = bucket.Entries[^1];
                bucket.Entries.Add(last with
                {
                    Level = ExecutionLogLevel.Warning,
                    EventType = "LogTruncated",
                    Message = $"{bucket.Dropped} further events were not stored.",
                    Properties = new Dictionary<string, string?>(),
                });
            }

            return [.. bucket.Entries];
        }
    }

    /// <summary>Discards everything of a record (e.g. a refused attempt) so nothing leaks into a later attempt.</summary>
    public void Discard(Guid organizationId, Guid recordId)
    {
        foreach (var key in _buckets.Keys.Where(k => k.Org == organizationId && k.Record == recordId))
        {
            _buckets.TryRemove(key, out _);
        }
    }

    private sealed class Bucket
    {
        public List<ExecutionLogEntry> Entries { get; } = [];

        public int Dropped { get; set; }
    }
}

/// <summary>The engine's (singleton) secret port, backed by a fresh scope bound to the attempt's own organization.</summary>
public sealed class ScopedSecretProvider(IServiceScopeFactory scopes) : ISecretProvider
{
    public async Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var services = scopes.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<TenantScope>().Enter(scope.OrganizationId);
        return await services.ServiceProvider.GetRequiredService<OrganizationSecretProvider>().GetAsync(scope, name, cancellationToken);
    }
}

/// <summary>
/// The Worker side of the process-death supervisor's containment handshake (§7.5.2): proves that this process runs inside
/// a containment unit the supervisor controls, and returns that unit. Without a successful handshake the Worker never
/// starts (ADR-058). Platform implementations: step 7.
/// </summary>
public interface IContainmentHandshake
{
    Task<ContainmentUnit> VerifyAsync(CancellationToken cancellationToken);
}

/// <summary>Holds the incarnation registered at startup; resolving it earlier fails (no execution before startup checks).</summary>
public sealed class WorkerIncarnationHolder
{
    private WorkerIncarnation? _incarnation;

    public WorkerIncarnation Incarnation => _incarnation ?? throw new InvalidOperationException("The Worker incarnation is not registered yet (startup checks have not passed).");

    public bool IsSet => _incarnation is not null;

    internal void Set(WorkerIncarnation incarnation) => _incarnation = _incarnation is null ? incarnation : throw new InvalidOperationException("The incarnation is already set.");
}
