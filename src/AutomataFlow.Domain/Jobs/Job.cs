using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Jobs;

/// <summary>Execution policy of a job (docs/ARCHITECTURE.md §5.2, §5.4). Validated on creation.</summary>
public sealed record JobOptions(int MaxConcurrency, int MaxAttempts, int RetryBackoffSeconds)
{
    public const int MaxConcurrencyLimit = 16;
    public const int MaxAttemptsLimit = 10;
    public const int MaxBackoffSeconds = 3_600;

    public static JobOptions Default { get; } = new(2, 3, 30);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrency, MaxConcurrencyLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAttempts, MaxAttemptsLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryBackoffSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetryBackoffSeconds, MaxBackoffSeconds);
    }
}

/// <summary>A batch of records executed with one pinned workflow version.</summary>
public sealed class Job : IOrganizationOwned
{
    public const int IdempotencyKeyMaxLength = 100;
    public const int SourceNameMaxLength = 255;

    private Job()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid WorkflowId { get; private set; }

    /// <summary>The pinned, published version; its hash is re-verified before every attempt.</summary>
    public Guid WorkflowVersionId { get; private set; }

    public JobStatus Status { get; private set; }

    public JobOptions Options { get; private set; } = JobOptions.Default;

    /// <summary>Workflow parameter values from the job configuration (never from records).</summary>
    public string ParametersJson { get; private set; } = "{}";

    public bool CancelRequested { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Client-supplied key making job creation idempotent (unique per organization).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Sanitized name of the uploaded file, for display only.</summary>
    public string? SourceName { get; private set; }

    public int TotalRecords { get; private set; }

    public int InvalidRecords { get; private set; }

    public static Job Create(
        Guid organizationId,
        Guid workflowId,
        Guid workflowVersionId,
        JobOptions options,
        string parametersJson,
        Guid createdBy,
        string? idempotencyKey,
        string? sourceName,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty || workflowId == Guid.Empty || workflowVersionId == Guid.Empty || createdBy == Guid.Empty)
        {
            throw new ArgumentException("Organization, workflow, version and creator are required.");
        }

        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        if (idempotencyKey is { Length: 0 or > IdempotencyKeyMaxLength })
        {
            throw new ArgumentException($"An idempotency key must be 1-{IdempotencyKeyMaxLength} characters.", nameof(idempotencyKey));
        }

        if (sourceName is { Length: > SourceNameMaxLength })
        {
            throw new ArgumentException("The source name is too long.", nameof(sourceName));
        }

        return new Job
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            WorkflowId = workflowId,
            WorkflowVersionId = workflowVersionId,
            Status = JobStatus.Pending,
            Options = options,
            ParametersJson = parametersJson,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            IdempotencyKey = idempotencyKey,
            SourceName = sourceName,
        };
    }

    /// <summary>Called once, after all records were persisted, in the creating transaction.</summary>
    public void SetRecordCounts(int total, int invalid)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegative(invalid);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(invalid, total);
        TotalRecords = total;
        InvalidRecords = invalid;
    }

    public void RequestCancellation() => CancelRequested = true;

    public void MarkStarted(DateTimeOffset at)
    {
        if (Status == JobStatus.Pending)
        {
            Status = JobStatus.Running;
            StartedAt = at;
        }
    }

    public void Finish(JobStatus status, DateTimeOffset at)
    {
        if (status is JobStatus.Pending or JobStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A job can only finish in a terminal status.");
        }

        Status = status;
        FinishedAt = at;
        StartedAt ??= at;
    }
}
