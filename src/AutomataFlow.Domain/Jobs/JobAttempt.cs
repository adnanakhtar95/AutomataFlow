using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Jobs;

/// <summary>
/// The execution record of one journal attempt: what the operator sees (status, timing, error code, logs and
/// artifacts). The attempt journal stays the only source of attempt history for repeat decisions (ADR-049).
/// </summary>
public sealed class JobAttempt : IOrganizationOwned
{
    private JobAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobId { get; private set; }

    public Guid JobRecordId { get; private set; }

    /// <summary>The journal's attempt number for the record's operation.</summary>
    public int AttemptNumber { get; private set; }

    public RecordIntent Intent { get; private set; }

    public JobAttemptStatus Status { get; private set; }

    public Guid WorkerIncarnationId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorCategory { get; private set; }

    public bool SideEffectStarted { get; private set; }

    public bool ExecutionFenced { get; private set; }

    /// <summary>Step records (ids, types, statuses, durations, error codes; never values) as JSON.</summary>
    public string? StepsJson { get; private set; }

    public static JobAttempt Start(Guid organizationId, Guid jobId, Guid jobRecordId, int attemptNumber, RecordIntent intent, Guid incarnation, DateTimeOffset startedAt)
    {
        if (organizationId == Guid.Empty || jobId == Guid.Empty || jobRecordId == Guid.Empty || incarnation == Guid.Empty)
        {
            throw new ArgumentException("Organization, job, record and incarnation are required.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        return new JobAttempt
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            JobId = jobId,
            JobRecordId = jobRecordId,
            AttemptNumber = attemptNumber,
            Intent = intent,
            Status = JobAttemptStatus.Running,
            WorkerIncarnationId = incarnation,
            StartedAt = startedAt,
        };
    }

    public void Complete(JobAttemptStatus status, string? errorCode, string? errorCategory, bool sideEffectStarted, bool executionFenced, string? stepsJson, DateTimeOffset finishedAt)
    {
        if (Status != JobAttemptStatus.Running)
        {
            throw new InvalidOperationException("The attempt is already completed.");
        }

        if (status == JobAttemptStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A completion needs a terminal status.");
        }

        Status = status;
        ErrorCode = errorCode;
        ErrorCategory = errorCategory;
        SideEffectStarted = sideEffectStarted;
        ExecutionFenced = executionFenced;
        StepsJson = stepsJson;
        FinishedAt = finishedAt < StartedAt ? StartedAt : finishedAt;
    }
}
