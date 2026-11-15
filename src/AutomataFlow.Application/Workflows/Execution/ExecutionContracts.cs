using System.Collections.Immutable;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>
/// One attempt request (docs/PHASE4_PLAN.md §7.1). Phase 5 builds one per attempt. Deliberately absent: a
/// private-network flag (§16 R3) and caller-supplied attempt history (history comes only from the journal, ADR-049).
/// </summary>
/// <param name="RecordedDefinitionHash">The hash stored for the pinned workflow version; verified at execution start (§11).</param>
/// <param name="Record">Raw record values; validated by <c>RecordValidator</c>.</param>
/// <param name="Parameters">From the job configuration only.</param>
public sealed record WorkflowExecutionRequest(
    ExecutionScope Scope,
    Guid OperationId,
    ValidatedWorkflow Workflow,
    int WorkflowVersion,
    string RecordedDefinitionHash,
    IReadOnlyDictionary<string, string?> Record,
    IReadOnlyDictionary<string, string> Parameters,
    ExecutionIntent Intent);

/// <summary>A classified error (§9). <see cref="Message"/> is redacted and never contains values.</summary>
public sealed record ExecutionError(string Code, ErrorCategory Category, bool IsTransient, string? StepId, string Message)
{
    public ErrorSummary ToSummary() => new(Code, Category, IsTransient);
}

public enum StepStatus
{
    Succeeded,
    Failed,
    FailedContinued,
    Skipped,
}

/// <summary>A step record: ids, type, status, duration and error code only (no values).</summary>
public sealed record StepRecord(string StepId, ActionType Type, StepStatus Status, long DurationMs, string? ErrorCode);

/// <summary>The result of one attempt or a refusal (§7.1). Exactly one outcome, set once.</summary>
public sealed record WorkflowExecutionResult(
    WorkflowOutcome Outcome,
    int? AttemptNumber,
    ExecutionError? Error,
    SideEffectState SideEffect,
    bool ExecutionFenced,
    RepeatDecision? Refusal,
    ReconciliationEvidence? Reconciliation,
    ImmutableDictionary<string, string> Results,
    ImmutableArray<StepRecord> Steps,
    ImmutableArray<ArtifactDescriptor> Artifacts,
    ImmutableArray<string> Diagnostics,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>Secret values (Application port, §5). Phase 5: encrypted, organization-scoped storage.</summary>
public interface ISecretProvider
{
    /// <summary>Returns the secret or throws; values are held in memory for the attempt only and never logged.</summary>
    Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken);
}

/// <summary>An upload's content as provided by the organization-scoped file provider (§5.4). Never a filesystem path.</summary>
public sealed record ExecutionFile(string FileName, string ContentType, long SizeBytes, Stream Content);

/// <summary>Resolves a record's file reference within the scope's organization only (Phase 5 tests cross-tenant denial).</summary>
public interface IExecutionFileProvider
{
    Task<ExecutionFile> OpenAsync(ExecutionScope scope, string fileReference, CancellationToken cancellationToken);
}

/// <summary>Stable engine error codes of the executor (§9) in addition to <c>EngineErrorCodes</c>.</summary>
public static class ExecutionErrorCodes
{
    public const string RepeatNotAuthorized = "RepeatNotAuthorized";
    public const string AttemptInFlight = "AttemptInFlight";
    public const string AwaitingFence = "AwaitingFence";
    public const string HistoryInvalid = "HistoryInvalid";
    public const string JournalUnavailable = "JournalUnavailable";
    public const string CheckpointFailed = "CheckpointFailed";
    public const string SecretUnavailable = "SecretUnavailable";
    public const string UploadRejected = "UploadRejected";
    public const string ExtractTooLong = "ExtractTooLong";
    public const string ResultTooLarge = "ResultTooLarge";
    public const string ResultOverwrite = "ResultOverwrite";
    public const string LookupInconclusive = "LookupInconclusive";
    public const string AmbiguousMatch = "AmbiguousMatch";
    public const string TooManyRows = "TooManyRows";
    public const string Cancelled = "Cancelled";
    public const string ReconcileNotAvailable = "ReconcileNotAvailable";
}
