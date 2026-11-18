using System.Text;
using System.Text.Json;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.Infrastructure.Jobs;

public sealed record AttemptView(Guid Id, int AttemptNumber, RecordIntent Intent, JobAttemptStatus Status, string? ErrorCode, string? ErrorCategory, bool SideEffectStarted,
    bool ExecutionFenced, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, JsonElement? Steps, IReadOnlyList<ArtifactView> Artifacts);

public sealed record ArtifactView(Guid Id, string Kind, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

public sealed record LogView(int Sequence, DateTimeOffset Timestamp, string Level, string EventType, string? StepId, string Message);

/// <summary>
/// Job control and execution reads (Phase 5 step 6): cancellation, result export, attempts, logs and artifacts. The
/// organization comes from <see cref="ICurrentUser"/> and is in every query; artifacts are served only after the row was
/// found in the caller's organization.
/// </summary>
public sealed class ExecutionQueryService(AutomataFlowDbContext db, ICurrentUser user, FileSystemArtifactStore artifacts, TimeProvider clock)
{
    private static readonly string[] FixedColumns = ["rowNumber", "operationId", "status", "errorCode"];

    /// <summary>
    /// Requests cancellation. Pending records whose operation has NO journal history are cancelled at once; any record with
    /// history (possibly an unresolved side effect of a crashed Worker) is left to the Worker, which resolves it through the
    /// repeat-safety evaluator (Cancelled only without an open ambiguity, otherwise NeedsReview).
    /// </summary>
    public async Task<ServiceResult<bool>> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.OrganizationId == org && j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return ServiceResult<bool>.NotFound();
        }

        if (job.Status is not (JobStatus.Pending or JobStatus.Running))
        {
            return ServiceResult<bool>.Fail(ServiceStatus.Conflict, new ServiceError(null, "job-finished", "The job has already finished."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await JobFinalizer.LockAsync(db, org, jobId, cancellationToken);
        job.RequestCancellation();
        await db.SaveChangesAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JobRecords" AS r SET "Status" = 'Cancelled', "ErrorCode" = 'JobCancelled', "CompletedAt" = {now}, "NextAttemptAt" = NULL
            WHERE r."OrganizationId" = {org} AND r."JobId" = {jobId} AND r."Status" = 'Pending'
              AND NOT EXISTS (SELECT 1 FROM "AttemptJournal" a WHERE a."OrganizationId" = r."OrganizationId" AND a."OperationId" = r."OperationId")
            """, cancellationToken);
        await JobFinalizer.TryFinalizeAsync(db, org, jobId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Explicit operator requeue of a NeedsReview record (§7.5.7): a fence or new evidence never restarts automation by
    /// itself. The record returns to the queue; the Worker re-evaluates the journal and the executor's admission guard
    /// still refuses any repeat that is not authorized (so a requeue can lead to reconciliation, never to a blind repeat).
    /// </summary>
    public async Task<ServiceResult<bool>> RequeueAsync(Guid jobId, Guid recordId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var record = await db.JobRecords.AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == org && r.JobId == jobId && r.Id == recordId, cancellationToken);
        if (record is null)
        {
            return ServiceResult<bool>.NotFound();
        }

        if (record.Status != JobRecordStatus.NeedsReview)
        {
            return ServiceResult<bool>.Fail(ServiceStatus.Conflict, new ServiceError(null, "not-needs-review", "Only a record that needs review can be requeued."));
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await JobFinalizer.LockAsync(db, org, jobId, cancellationToken);
        await db.JobRecords.Where(r => r.OrganizationId == org && r.Id == recordId && r.Status == JobRecordStatus.NeedsReview)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.Pending).SetProperty(r => r.NextAttemptAt, now)
                .SetProperty(r => r.AwaitingFenceSince, (DateTimeOffset?)null).SetProperty(r => r.CompletedAt, (DateTimeOffset?)null).SetProperty(r => r.ErrorCode, "Requeued"), cancellationToken);
        await db.Jobs.Where(j => j.OrganizationId == org && j.Id == jobId && !j.CancelRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running).SetProperty(j => j.FinishedAt, (DateTimeOffset?)null), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Writes the job's records as CSV: row, operation id, status, error code and every result field (never inputs).</summary>
    public async Task<bool> WriteResultsCsvAsync(Guid jobId, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var org = user.OrganizationId;
        if (!await db.Jobs.AnyAsync(j => j.OrganizationId == org && j.Id == jobId, cancellationToken))
        {
            return false;
        }

        var rows = await db.JobRecords.AsNoTracking().Where(r => r.OrganizationId == org && r.JobId == jobId).OrderBy(r => r.RowNumber)
            .Select(r => new { r.RowNumber, r.OperationId, r.Status, r.ErrorCode, r.ResultJson }).ToListAsync(cancellationToken);
        var results = rows.Select(r => r.ResultJson is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(r.ResultJson) ?? []).ToList();
        var fields = results.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        await output.WriteLineAsync(string.Join(',', FixedColumns.Concat(fields).Select(Cell)));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cells = new[] { row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), row.OperationId.ToString("D"), row.Status.ToString(), row.ErrorCode ?? string.Empty }
                .Concat(fields.Select(f => results[i].GetValueOrDefault(f) ?? string.Empty));
            await output.WriteLineAsync(string.Join(',', cells.Select(Cell)));
        }

        return true;
    }

    public async Task<ServiceResult<IReadOnlyList<AttemptView>>> AttemptsAsync(Guid jobId, Guid recordId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        if (!await db.JobRecords.AnyAsync(r => r.OrganizationId == org && r.JobId == jobId && r.Id == recordId, cancellationToken))
        {
            return ServiceResult<IReadOnlyList<AttemptView>>.NotFound();
        }

        var attempts = await db.JobAttempts.AsNoTracking().Where(a => a.OrganizationId == org && a.JobRecordId == recordId).OrderBy(a => a.AttemptNumber).ToListAsync(cancellationToken);
        var ids = attempts.Select(a => a.Id).ToList();
        var stored = await db.ExecutionArtifacts.AsNoTracking().Where(a => a.OrganizationId == org && ids.Contains(a.JobAttemptId)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<AttemptView>>.Ok([.. attempts.Select(a => new AttemptView(a.Id, a.AttemptNumber, a.Intent, a.Status, a.ErrorCode, a.ErrorCategory,
            a.SideEffectStarted, a.ExecutionFenced, a.StartedAt, a.FinishedAt, Parse(a.StepsJson),
            [.. stored.Where(s => s.JobAttemptId == a.Id).Select(s => new ArtifactView(s.Id, s.Kind, s.FileName, s.ContentType, s.SizeBytes, s.Sha256, s.CreatedAt))]))]);
    }

    public async Task<ServiceResult<IReadOnlyList<LogView>>> LogsAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        if (!await db.JobAttempts.AnyAsync(a => a.OrganizationId == org && a.Id == attemptId, cancellationToken))
        {
            return ServiceResult<IReadOnlyList<LogView>>.NotFound();
        }

        return ServiceResult<IReadOnlyList<LogView>>.Ok(await db.ExecutionLogs.AsNoTracking().Where(l => l.OrganizationId == org && l.JobAttemptId == attemptId)
            .OrderBy(l => l.Sequence).Select(l => new LogView(l.Sequence, l.Timestamp, l.Level, l.EventType, l.StepId, l.Message)).ToListAsync(cancellationToken));
    }

    /// <summary>Opens an artifact's content after verifying it belongs to the caller's organization (404 otherwise).</summary>
    public async Task<(ArtifactView Artifact, Stream Content)?> OpenArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var row = await db.ExecutionArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == org && a.Id == artifactId, cancellationToken);
        if (row is null || !row.StorageKey.StartsWith(org.ToString("N") + "/", StringComparison.Ordinal))
        {
            return null;
        }

        return (new ArtifactView(row.Id, row.Kind, row.FileName, row.ContentType, row.SizeBytes, row.Sha256, row.CreatedAt), artifacts.OpenRead(row.StorageKey));
    }

    /// <summary>CSV cell with quoting, and spreadsheet-formula neutralization (=, +, -, @, tab, CR at the start).</summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
