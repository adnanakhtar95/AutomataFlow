using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Jobs;

/// <summary>Which kind of attempt a record's next attempt is (mirrors Application's ExecutionIntent).</summary>
public enum RecordIntent
{
    Normal = 0,
    Reconcile = 1,
}

/// <summary>
/// The unit of work claimed by Workers (docs/ARCHITECTURE.md §5.1). <see cref="OperationId"/> is generated once and
/// is identical across every attempt (ADR-015). Attempt history itself lives only in the attempt journal (ADR-049).
/// </summary>
public sealed class JobRecord : IOrganizationOwned
{
    public const int ErrorCodeMaxLength = 64;
    public const int ErrorMessageMaxLength = 500;

    private JobRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>1-based data row number in the uploaded file.</summary>
    public int RowNumber { get; private set; }

    public Guid OperationId { get; private set; }

    /// <summary>
    /// The record's raw values as JSON. When the workflow declares sensitive inputs this is a Data Protection payload,
    /// never plain text (decided at job creation).
    /// </summary>
    public string InputJson { get; private set; } = "{}";

    public bool InputProtected { get; private set; }

    public JobRecordStatus Status { get; private set; }

    public RecordIntent NextIntent { get; private set; }

    /// <summary>Attempts started for this record (journal attempts, both intents).</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Consecutive failed reconcile attempts (A4 budget is enforced by the evaluator; kept for display).</summary>
    public int ReconcileFailures { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>The Worker incarnation holding the claim.</summary>
    public Guid? LockedBy { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    /// <summary>Result fields (never sensitive) of the resolving attempt.</summary>
    public string? ResultJson { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Redacted message; never contains record values.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Codes of record validation failures (no values) for invalid rows.</summary>
    public string? ValidationErrorsJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static JobRecord CreatePending(Guid organizationId, Guid jobId, int rowNumber, string inputJson, bool inputProtected, DateTimeOffset createdAt) =>
        Create(organizationId, jobId, rowNumber, inputJson, inputProtected, JobRecordStatus.Pending, null, createdAt);

    /// <summary>A row that failed record validation: kept for reporting, never executed.</summary>
    public static JobRecord CreateInvalid(Guid organizationId, Guid jobId, int rowNumber, string validationErrorsJson, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationErrorsJson);
        return Create(organizationId, jobId, rowNumber, "{}", false, JobRecordStatus.Invalid, validationErrorsJson, createdAt);
    }

    private static JobRecord Create(Guid organizationId, Guid jobId, int rowNumber, string inputJson, bool inputProtected, JobRecordStatus status, string? validationErrors, DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty || jobId == Guid.Empty)
        {
            throw new ArgumentException("Organization and job are required.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(rowNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);
        return new JobRecord
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            JobId = jobId,
            RowNumber = rowNumber,
            OperationId = Guid.NewGuid(),
            InputJson = inputJson,
            InputProtected = inputProtected,
            Status = status,
            NextIntent = RecordIntent.Normal,
            NextAttemptAt = status == JobRecordStatus.Pending ? createdAt : null,
            ValidationErrorsJson = validationErrors,
            CreatedAt = createdAt,
            CompletedAt = status == JobRecordStatus.Invalid ? createdAt : null,
        };
    }

    public static bool IsTerminal(JobRecordStatus status) => status is JobRecordStatus.Succeeded or JobRecordStatus.Failed or JobRecordStatus.Cancelled
        or JobRecordStatus.Skipped or JobRecordStatus.NeedsReview or JobRecordStatus.Reconciled or JobRecordStatus.Invalid;
}
