using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Execution;

public enum ExecutionIntent
{
    Normal,
    Reconcile,
}

/// <summary>The terminal outcome of ONE attempt, or a refusal (§7.1, §16 D3).</summary>
public enum WorkflowOutcome
{
    Succeeded,
    Failed,
    Indeterminate,
    ReconciledFound,
    ReconciledNotFound,
    NeedsReview,
    Cancelled,

    /// <summary>Admission refused: not an attempt, never journaled.</summary>
    Refused,
}

public enum AttemptState
{
    Reserved,
    Started,
    Completed,
    Abandoned,
}

public enum SideEffectState
{
    NotStarted,
    Started,

    /// <summary>The entry could not be read completely; treated as <see cref="Started"/>.</summary>
    Unknown,
}

public enum ErrorCategory
{
    Transient,
    Permanent,
    Policy,
    Business,
    Cancelled,
}

/// <summary>Code, category and transience of an attempt's error (no message, no values).</summary>
public sealed record ErrorSummary(string Code, ErrorCategory Category, bool IsTransient);

/// <summary>Where a fence came from (§7.2 fencing table; ADR-056).</summary>
public enum FenceSource
{
    ExecutorEgressRevoked,
    NoCheckpoint,
    ProcessDeathConfirmed,
    IsolationTerminated,
    EgressLeaseExpired,
    HumanConfirmed,
}

/// <summary>The reconcile policy of the version that ran (advisory: latency and age are 0 and never used).</summary>
public sealed record ReconcilePolicySnapshot(EvidenceLevel Level, MatchKeyKind MatchKey, int MaxCommitLatencySeconds, int MaxEvidenceAgeSeconds)
{
    public static ReconcilePolicySnapshot? From(ReconciliationPolicy? policy) => policy is null
        ? null
        : new ReconcilePolicySnapshot(policy.Evidence, policy.MatchKey, policy.Authority?.MaxCommitLatencySeconds ?? 0, policy.Authority?.MaxEvidenceAgeSeconds ?? 0);
}

/// <summary>Recorded at <c>StartAsync</c> from the version that actually ran (R7): evidence is bound to it.</summary>
public sealed record AttemptSnapshot(string DefinitionHash, int WorkflowVersion, int TimeoutSeconds, bool TargetEnforced, bool OperationIdSent, ReconcilePolicySnapshot? Reconcile)
{
    public static AttemptSnapshot From(ValidatedWorkflow workflow, int workflowVersion)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return new AttemptSnapshot(
            workflow.DefinitionHash,
            workflowVersion,
            workflow.Definition.TimeoutSeconds,
            workflow.Safety.TargetEnforced,
            workflow.Safety.OperationIdSent,
            ReconcilePolicySnapshot.From(workflow.Definition.Reconciliation));
    }
}

public enum ReconciliationFinding
{
    Found,
    NotFound,
    Ambiguous,
}

/// <summary>A reconcile finding, observed at <see cref="ObservedAt"/> (journal clock, before the first reconcile step).</summary>
public sealed record ReconciliationEvidence(ReconciliationFinding Finding, DateTimeOffset ObservedAt, ReconcilePolicySnapshot Policy);

/// <summary>One journal entry as the evaluator sees it (§10.5). Ordered by <see cref="AttemptNumber"/>, never by timestamps.</summary>
/// <param name="Snapshot">Null only for a <see cref="AttemptState.Reserved"/> entry (not yet started).</param>
/// <param name="StartedAt">Null only for a <see cref="AttemptState.Reserved"/> entry.</param>
public sealed record AttemptSummary(
    Guid OperationId,
    int AttemptNumber,
    ExecutionIntent Intent,
    AttemptState State,
    WorkflowOutcome? Outcome,
    ErrorSummary? Error,
    SideEffectState SideEffect,
    AttemptSnapshot? Snapshot,
    DateTimeOffset? StartedAt,
    DateTimeOffset? SideEffectCheckpointAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? AbandonedAt,
    bool ExecutionFenced,
    DateTimeOffset? FencedAt,
    FenceSource? FenceSource,
    ReconciliationEvidence? Reconciliation);

/// <summary>Server options of the evaluator (§1.6); construct with <see cref="Create"/> to enforce the documented ranges.</summary>
public sealed record RepeatSafetyPolicy
{
    private RepeatSafetyPolicy(TimeSpan clockSkewAllowance, int maxReconcileFailures, int maxAmbiguousAttempts)
    {
        ClockSkewAllowance = clockSkewAllowance;
        MaxReconcileFailures = maxReconcileFailures;
        MaxAmbiguousAttempts = maxAmbiguousAttempts;
    }

    public TimeSpan ClockSkewAllowance { get; }

    public int MaxReconcileFailures { get; }

    public int MaxAmbiguousAttempts { get; }

    /// <summary>Defaults: skew 5 s, 3 reconcile failures, 3 ambiguous attempts.</summary>
    public static RepeatSafetyPolicy Default { get; } = Create(5, 3, 3);

    /// <summary>Ranges: skew 0..60 s; reconcile failures 1..10; ambiguous attempts 1..10.</summary>
    public static RepeatSafetyPolicy Create(int clockSkewSeconds, int maxReconcileFailures, int maxAmbiguousAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clockSkewSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clockSkewSeconds, 60);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReconcileFailures, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxReconcileFailures, 10);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAmbiguousAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAmbiguousAttempts, 10);
        return new RepeatSafetyPolicy(TimeSpan.FromSeconds(clockSkewSeconds), maxReconcileFailures, maxAmbiguousAttempts);
    }
}

public enum RepeatDecisionKind
{
    Allowed,
    NotNeeded,
    ReconcileRequired,
    ReconcileTooEarly,
    InFlight,
    AwaitingFence,
    NeedsReview,
}

/// <summary>The positive evidence behind an <see cref="RepeatDecisionKind.Allowed"/> (or <c>Resolved</c> for NotNeeded).</summary>
public enum SafetyEvidence
{
    None,
    NoPriorAttempt,
    NoSideEffectStarted,
    TargetEnforcedIdempotency,
    AuthoritativeNotFound,
    Resolved,
}

public enum Resolution
{
    Succeeded,
    BusinessFailure,
    PermanentFailureBeforeSideEffect,
    ReconciledFound,
}

/// <summary>Which rule decided (§10.5).</summary>
public enum RepeatReason
{
    HistoryInvalid,
    AttemptInFlight,
    UnfencedAttempt,
    NoPriorAttempt,
    Resolved,
    ReconciledFound,
    ReconcileNeedsReview,
    PermanentFailureWithOpenAmbiguity,
    ReconcileBudgetExhausted,
    AmbiguityBudgetExhausted,
    TargetEnforcedIdempotency,
    AdvisoryNotFound,
    KeyNotSent,
    EvidenceTooEarly,
    EvidenceExpired,
    AuthoritativeNotFound,
    ReconcileRequired,
    NoSafePath,
    PermanentFailureBeforeSideEffect,
    NoSideEffectStarted,
    NoMatchingRule,
}

/// <summary>Exactly one decision per evaluation.</summary>
public sealed record RepeatDecision(RepeatDecisionKind Kind, SafetyEvidence Evidence, Resolution? Resolution, RepeatReason Reason, DateTimeOffset? RetryAfter);
