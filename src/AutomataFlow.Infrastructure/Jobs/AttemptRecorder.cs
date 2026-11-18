using System.Text.Json;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutomataFlow.Infrastructure.Jobs;

/// <summary>Everything a Worker needs to run one claimed record, loaded inside the record's organization scope.</summary>
public sealed record RecordWork(JobRecord Record, Job Job, WorkflowVersion Version, IReadOnlyDictionary<string, string?> Values, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Loads a claimed record and persists what one attempt produced (Phase 5 step 6). Runs in a scope bound to the record's
/// organization (<see cref="TenantScope"/>); every query also names the organization explicitly.
/// </summary>
public sealed class AttemptRecorder(AutomataFlowDbContext db, TenantScope tenant, ValueProtection protection)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<RecordWork?> LoadAsync(ClaimedRecord claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        tenant.Enter(claim.OrganizationId);
        var org = claim.OrganizationId;
        var record = await db.JobRecords.AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == org && r.Id == claim.Id, cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.OrganizationId == org && j.Id == claim.JobId, cancellationToken);
        if (record is null || job is null)
        {
            return null;
        }

        var version = await db.WorkflowVersions.AsNoTracking().SingleAsync(v => v.OrganizationId == org && v.Id == job.WorkflowVersionId, cancellationToken);
        var json = record.InputProtected ? protection.UnprotectRecord(org, record.InputJson) : record.InputJson;
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(job.ParametersJson) ?? [];
        return new RecordWork(record, job, version, values, parameters);
    }

    /// <summary>
    /// Applies the transition and stores the attempt (status, steps, logs, artifacts, usage) in ONE transaction, only if
    /// this Worker still holds the claim. Returns false when the claim was lost (another Worker owns the record now).
    /// </summary>
    public async Task<bool> CompleteAsync(ClaimedRecord claim, Guid incarnation, WorkflowExecutionResult? result, RecordTransition transition,
        IReadOnlyList<ExecutionLogEntry> logs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(logs);
        tenant.Enter(claim.OrganizationId);
        var org = claim.OrganizationId;
        var attempted = result?.AttemptNumber is not null;
        var resultJson = transition.StoreResults && result is { Results.Count: > 0 } ? JsonSerializer.Serialize(result.Results) : null;
        var completedAt = transition.IsTerminal ? now : (DateTimeOffset?)null;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialize every transaction that can finish this job on the job row: otherwise two records completing at the
        // same moment each see the other still Running (read committed) and neither finalizes the job.
        await JobFinalizer.LockAsync(db, org, claim.JobId, cancellationToken);
        var updated = await db.JobRecords
            .Where(r => r.OrganizationId == org && r.Id == claim.Id && r.LockedBy == incarnation && r.Status == JobRecordStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, transition.Status)
                .SetProperty(r => r.NextIntent, transition.NextIntent)
                .SetProperty(r => r.NextAttemptAt, transition.NextAttemptAt)
                .SetProperty(r => r.AwaitingFenceSince, transition.AwaitingFenceSince)
                .SetProperty(r => r.ErrorCode, transition.ErrorCode)
                .SetProperty(r => r.ResultJson, r => resultJson ?? r.ResultJson)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + (attempted ? 1 : 0))
                .SetProperty(r => r.LockedBy, (Guid?)null)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(r => r.CompletedAt, completedAt), cancellationToken);
        if (updated != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        if (result is { AttemptNumber: { } number })
        {
            var attempt = JobAttempt.Start(org, claim.JobId, claim.Id, number, claim.NextIntent, incarnation, result.StartedAt);
            attempt.Complete(StatusOf(result.Outcome), result.Error?.Code, result.Error?.Category.ToString(), result.SideEffect != SideEffectState.NotStarted,
                result.ExecutionFenced, JsonSerializer.Serialize(result.Steps, Json), result.FinishedAt);
            db.JobAttempts.Add(attempt);
            var sequence = 0;
            foreach (var entry in logs)
            {
                entry.Properties.TryGetValue("stepId", out var stepId);
                db.ExecutionLogs.Add(ExecutionLog.Create(org, attempt.Id, sequence++, entry.Timestamp, entry.Level.ToString(), entry.EventType, stepId, entry.Message,
                    entry.Properties.Count == 0 ? null : JsonSerializer.Serialize(entry.Properties)));
            }

            foreach (var artifact in result.Artifacts)
            {
                db.ExecutionArtifacts.Add(ExecutionArtifact.Create(artifact.Id, org, attempt.Id, artifact.Kind.ToString(), artifact.StorageKey, artifact.FileName,
                    artifact.ContentType, artifact.SizeBytes, artifact.Sha256, artifact.CreatedAt));
            }

            db.UsageRecords.Add(UsageRecord.Create(org, claim.JobId, UsageMetrics.AttemptsStarted, 1, now));
            await db.SaveChangesAsync(cancellationToken);
        }

        await JobFinalizer.TryFinalizeAsync(db, org, claim.JobId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Whether the record's job has a cancellation request (read after an attempt ended).</summary>
    public Task<bool> IsCancelRequestedAsync(ClaimedRecord claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return db.Jobs.AnyAsync(j => j.OrganizationId == claim.OrganizationId && j.Id == claim.JobId && j.CancelRequested, cancellationToken);
    }

    public static JobAttemptStatus StatusOf(WorkflowOutcome outcome) => outcome switch
    {
        WorkflowOutcome.Succeeded => JobAttemptStatus.Succeeded,
        WorkflowOutcome.Failed => JobAttemptStatus.Failed,
        WorkflowOutcome.Indeterminate => JobAttemptStatus.Indeterminate,
        WorkflowOutcome.ReconciledFound => JobAttemptStatus.ReconciledFound,
        WorkflowOutcome.ReconciledNotFound => JobAttemptStatus.ReconciledNotFound,
        WorkflowOutcome.NeedsReview => JobAttemptStatus.NeedsReview,
        _ => JobAttemptStatus.Cancelled,
    };
}

/// <summary>
/// Completes a job once none of its records can still run (docs/ARCHITECTURE.md §5.5). Idempotent; race-safe only when
/// the calling transaction first took <see cref="LockAsync"/> (every writer that can finish a job does).
/// </summary>
public static class JobFinalizer
{
    /// <summary>Row lock on the job for the rest of the transaction (same lock order as claiming: job, then records).</summary>
    public static Task<int> LockAsync(AutomataFlowDbContext db, Guid organizationId, Guid jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Jobs" WHERE "OrganizationId" = {organizationId} AND "Id" = {jobId} FOR UPDATE""", cancellationToken);
    }

    /// <summary>
    /// The single finalization statement (parameters <c>@org</c>, <c>@job</c>). It must run in a statement AFTER the job-row
    /// lock: under read committed each statement takes a fresh snapshot, so it sees every commit made before the lock.
    /// </summary>
    internal const string FinalizeSql = """
        UPDATE "Jobs" AS j SET
          "Status" = CASE WHEN j."CancelRequested" THEN 'Cancelled'
                          WHEN s.ok = s.total THEN 'Completed'
                          WHEN s.ok = 0 THEN 'Failed'
                          ELSE 'CompletedWithErrors' END,
          "FinishedAt" = now(),
          "StartedAt" = COALESCE(j."StartedAt", now())
        FROM (SELECT count(*) AS total,
                     count(*) FILTER (WHERE "Status" IN ('Succeeded', 'Reconciled')) AS ok,
                     count(*) FILTER (WHERE "Status" IN ('Pending', 'Running', 'AwaitingFence')) AS open
              FROM "JobRecords" WHERE "OrganizationId" = @org AND "JobId" = @job) AS s
        WHERE j."OrganizationId" = @org AND j."Id" = @job AND j."Status" IN ('Pending', 'Running') AND s.open = 0
        """;

    public static Task<int> TryFinalizeAsync(AutomataFlowDbContext db, Guid organizationId, Guid jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlRawAsync(FinalizeSql, [new NpgsqlParameter("org", organizationId), new NpgsqlParameter("job", jobId)], cancellationToken);
    }

    /// <summary>The same statement on a raw connection (the Worker's idle-job sweep); call only after the job-row lock.</summary>
    public static async Task<int> TryFinalizeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organizationId, Guid jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var finalize = new NpgsqlCommand(FinalizeSql, connection, transaction);
        finalize.Parameters.AddWithValue("org", organizationId);
        finalize.Parameters.AddWithValue("job", jobId);
        return await finalize.ExecuteNonQueryAsync(cancellationToken);
    }
}
