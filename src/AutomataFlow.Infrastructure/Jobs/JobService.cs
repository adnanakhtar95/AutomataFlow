using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.RecordFiles;
using AutomataFlow.Application.Workflows.Records;
using AutomataFlow.Application.Workflows.Urls;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AutomataFlow.Infrastructure.Jobs;

public enum ImportFormat
{
    Csv,
    Json,
}

public sealed class ImportOptions
{
    public const string SectionName = "Imports";

    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxRecords { get; set; } = 10_000;

    public int MaxColumns { get; set; } = 100;

    public int MaxCellChars { get; set; } = 10_000;

    public ImportLimits ToLimits() => new() { MaxBytes = MaxBytes, MaxRecords = MaxRecords, MaxColumns = MaxColumns, MaxCellChars = MaxCellChars };
}

/// <param name="Content">The uploaded file; must be seekable (it is hashed, then parsed).</param>
/// <param name="IgnoreUnknownColumns">Opt-in: drop columns that are not declared inputs (default: reject the file).</param>
public sealed record CreateJobCommand(Guid WorkflowVersionId, IReadOnlyDictionary<string, string> Parameters, JobOptions Options, string? IdempotencyKey, Stream Content, string? SourceName, ImportFormat Format, bool IgnoreUnknownColumns = false);

public sealed record JobCreated(Guid JobId, int TotalRecords, int InvalidRecords, bool Replayed);

public sealed record JobView(Guid Id, Guid WorkflowId, Guid WorkflowVersionId, JobStatus Status, JobOptions Options, bool CancelRequested, string? SourceName,
    int TotalRecords, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, IReadOnlyDictionary<string, int> RecordsByStatus);

/// <summary>A record as shown to operators: status, ids, result and error codes. Input values are never returned.</summary>
public sealed record RecordView(Guid Id, int RowNumber, Guid OperationId, JobRecordStatus Status, int AttemptCount, string? ErrorCode, string? ErrorMessage, JsonElement? Result, JsonElement? ValidationErrors, DateTimeOffset? CompletedAt);

/// <summary>Job creation and reads (Phase 5 step 4). The organization always comes from <see cref="ICurrentUser"/>.</summary>
public sealed class JobService(AutomataFlowDbContext db, ICurrentUser user, ValueProtection protection, IOptions<ImportOptions> imports, TimeProvider clock, ILogger<JobService> logger)
{
    private const int BatchSize = 500;

    public async Task<ServiceResult<JobCreated>> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var org = user.OrganizationId;
        try
        {
            command.Options.Validate();
        }
        catch (ArgumentException)
        {
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Invalid, new ServiceError("options", "options-invalid", "Job options are out of range."));
        }

        if (command.IdempotencyKey is { Length: 0 or > Job.IdempotencyKeyMaxLength })
        {
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Invalid, new ServiceError("Idempotency-Key", "idempotency-key-invalid", "The idempotency key must be 1-100 characters."));
        }

        if (command.Content.Length > imports.Value.MaxBytes)
        {
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Invalid, new ServiceError("file", "file-too-large", $"The file is larger than {imports.Value.MaxBytes} bytes."));
        }

        var fingerprint = await FingerprintAsync(command, cancellationToken);
        if (command.IdempotencyKey is { } key && await ReplayAsync(org, key, fingerprint, cancellationToken) is { } replay)
        {
            return replay;
        }

        var version = await db.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(v => v.OrganizationId == org && v.Id == command.WorkflowVersionId, cancellationToken);
        if (version is null)
        {
            return ServiceResult<JobCreated>.NotFound();
        }

        if (version.Status != WorkflowVersionStatus.Published)
        {
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Conflict, new ServiceError("workflowVersionId", "version-not-published", "Jobs can only run published versions."));
        }

        if (ValidatedWorkflow.Load(version.DefinitionJson, version.DefinitionHash).Workflow is not { } workflow)
        {
            logger.LogWarning("Published version failed hash verification {WorkflowVersionId}", version.Id);
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Conflict, new ServiceError("workflowVersionId", "WorkflowTampered", "The stored definition does not match its hash."));
        }

        var definition = workflow.Definition;
        if (ValidateParameters(definition, command.Parameters) is { } parameterError)
        {
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Invalid, parameterError);
        }

        var now = clock.GetUtcNow();
        var job = Job.Create(org, version.WorkflowId, version.Id, command.Options, JsonSerializer.Serialize(command.Parameters), user.UserId, command.IdempotencyKey, Sanitize(command.SourceName), now, fingerprint);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && command.IdempotencyKey is { } raced)
        {
            // A concurrent request with the same key won: answer as a replay (or a conflict if it was different).
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await ReplayAsync(org, raced, fingerprint, cancellationToken) ?? ServiceResult<JobCreated>.Fail(ServiceStatus.Conflict, new ServiceError("Idempotency-Key", "idempotency-key-conflict", "The key was used concurrently."));
        }

        var protect = definition.Inputs.Any(i => i.Sensitive);
        var (total, invalid) = (0, 0);
        try
        {
            command.Content.Position = 0;
            var rows = command.Format == ImportFormat.Csv
                ? RecordImport.ReadCsvAsync(command.Content, imports.Value.ToLimits(), cancellationToken)
                : RecordImport.ReadJsonAsync(command.Content, imports.Value.ToLimits(), cancellationToken);
            var pending = 0;
            var declared = definition.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            await foreach (var row in rows)
            {
                if (!command.IgnoreUnknownColumns && command.Format == ImportFormat.Csv && row.RowNumber == 1 && row.Values.Keys.FirstOrDefault(k => !declared.Contains(k)) is { } unknown)
                {
                    throw new ImportException("column-unknown", $"Column '{unknown}' is not a declared input of the workflow.");
                }

                total++;
                var values = command.IgnoreUnknownColumns ? row.Values.Where(v => declared.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal) : row.Values;
                var (record, errors) = RecordValidator.Validate(definition, values);
                if (record is null)
                {
                    invalid++;
                    var codes = JsonSerializer.Serialize(errors.Select(e => new { path = e.Path, code = e.Code }));
                    db.JobRecords.Add(JobRecord.CreateInvalid(org, job.Id, row.RowNumber, codes, now));
                }
                else
                {
                    var json = JsonSerializer.Serialize(record.Values);
                    db.JobRecords.Add(JobRecord.CreatePending(org, job.Id, row.RowNumber, protect ? protection.ProtectRecord(org, json) : json, protect, now));
                }

                if (++pending == BatchSize)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    db.ChangeTracker.Clear(); // bounded memory: nothing of earlier batches stays tracked
                    pending = 0;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        catch (ImportException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JobCreated>.Fail(ServiceStatus.Invalid, new ServiceError(ex.Row is { } r ? $"file:row {r}" : "file", ex.Code, ex.Message));
        }

        await db.Jobs.Where(j => j.OrganizationId == org && j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TotalRecords, total).SetProperty(j => j.InvalidRecords, invalid), cancellationToken);
        db.UsageRecords.Add(UsageRecord.Create(org, job.Id, UsageMetrics.RecordsImported, total, now));
        await db.SaveChangesAsync(cancellationToken);
        await JobFinalizer.TryFinalizeAsync(db, org, job.Id, cancellationToken); // nothing runnable (all invalid / empty) => finished now
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Job created {JobId} {WorkflowVersionId} {TotalRecords} {InvalidRecords}", job.Id, version.Id, total, invalid);
        return ServiceResult<JobCreated>.Ok(new JobCreated(job.Id, total, invalid, false));
    }

    public async Task<IReadOnlyList<JobView>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.OrganizationId == org).OrderByDescending(j => j.CreatedAt)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(cancellationToken);
        var result = new List<JobView>(jobs.Count);
        foreach (var job in jobs)
        {
            result.Add(await ViewAsync(job, cancellationToken));
        }

        return result;
    }

    public async Task<ServiceResult<JobView>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.OrganizationId == org && j.Id == jobId, cancellationToken);
        return job is null ? ServiceResult<JobView>.NotFound() : ServiceResult<JobView>.Ok(await ViewAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<IReadOnlyList<RecordView>>> RecordsAsync(Guid jobId, JobRecordStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        if (!await db.Jobs.AnyAsync(j => j.OrganizationId == org && j.Id == jobId, cancellationToken))
        {
            return ServiceResult<IReadOnlyList<RecordView>>.NotFound();
        }

        var query = db.JobRecords.AsNoTracking().Where(r => r.OrganizationId == org && r.JobId == jobId);
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var rows = await query.OrderBy(r => r.RowNumber).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500))
            .Select(r => new { r.Id, r.RowNumber, r.OperationId, r.Status, r.AttemptCount, r.ErrorCode, r.ErrorMessage, r.ResultJson, r.ValidationErrorsJson, r.CompletedAt })
            .ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<RecordView>>.Ok([.. rows.Select(r => new RecordView(r.Id, r.RowNumber, r.OperationId, r.Status, r.AttemptCount, r.ErrorCode, r.ErrorMessage,
            Parse(r.ResultJson), Parse(r.ValidationErrorsJson), r.CompletedAt))]);
    }

    private async Task<JobView> ViewAsync(Job job, CancellationToken cancellationToken)
    {
        var counts = await db.JobRecords.AsNoTracking().Where(r => r.OrganizationId == job.OrganizationId && r.JobId == job.Id)
            .GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return new JobView(job.Id, job.WorkflowId, job.WorkflowVersionId, job.Status, job.Options, job.CancelRequested, job.SourceName, job.TotalRecords,
            job.CreatedAt, job.StartedAt, job.FinishedAt, counts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal));
    }

    private async Task<ServiceResult<JobCreated>?> ReplayAsync(Guid org, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var existing = await db.Jobs.AsNoTracking().Where(j => j.OrganizationId == org && j.IdempotencyKey == key)
            .Select(j => new { j.Id, j.RequestFingerprint, j.TotalRecords, j.InvalidRecords }).SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return null;
        }

        return existing.RequestFingerprint == fingerprint
            ? ServiceResult<JobCreated>.Ok(new JobCreated(existing.Id, existing.TotalRecords, existing.InvalidRecords, true))
            : ServiceResult<JobCreated>.Fail(ServiceStatus.Conflict, new ServiceError("Idempotency-Key", "idempotency-key-reused", "The key was already used for a different request."));
    }

    private static async Task<string> FingerprintAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        command.Content.Position = 0;
        var file = await SHA256.HashDataAsync(command.Content, cancellationToken);
        command.Content.Position = 0;
        var header = string.Join("\n", command.WorkflowVersionId.ToString("D"), command.Format.ToString(),
            JsonSerializer.Serialize(command.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)), JsonSerializer.Serialize(command.Options), command.IgnoreUnknownColumns ? "ignore" : "strict", Convert.ToHexStringLower(file));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(header)));
    }

    /// <summary>Every declared parameter, nothing else; url parameters must be canonicalizable origins (bound again by the Worker).</summary>
    private static ServiceError? ValidateParameters(WorkflowDefinition definition, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var declared in definition.Parameters)
        {
            if (!parameters.TryGetValue(declared.Name, out var value) || string.IsNullOrWhiteSpace(value) || value.Length > WorkflowLimits.MaxGenericStringChars)
            {
                return new ServiceError("parameters." + declared.Name, "parameter-missing", "A declared parameter is missing or empty.");
            }

            if (declared.Type == WorkflowParameterType.Url && CanonicalOrigin.TryCanonicalize(value, allowWildcard: false, out _) is null)
            {
                return new ServiceError("parameters." + declared.Name, "parameter-invalid-url", "A url parameter must be an origin such as https://crm.example.com.");
            }
        }

        var unknown = parameters.Keys.FirstOrDefault(k => definition.Parameters.All(p => p.Name != k));
        return unknown is null ? null : new ServiceError("parameters." + unknown, "parameter-unknown", "The workflow does not declare this parameter.");
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var file = Path.GetFileName(name.Replace('\\', '/'));
        var clean = new string([.. file.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, Job.SourceNameMaxLength)];
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
