using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>The facts about the CURRENT workflow version the evaluator needs.</summary>
public sealed record CurrentWorkflow(bool TargetEnforced, bool OperationIdSent, bool HasReconcileSection, ReconcilePolicySnapshot? Reconcile)
{
    public static CurrentWorkflow From(ValidatedWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return new CurrentWorkflow(
            workflow.Safety.TargetEnforced,
            workflow.Safety.OperationIdSent,
            workflow.Definition.HasReconcileSection,
            ReconcilePolicySnapshot.From(workflow.Definition.Reconciliation));
    }
}

/// <summary>
/// Decides whether another attempt for an operation is safe (docs/PHASE4_PLAN.md §10.5; ADR-045, ADR-050, ADR-056,
/// ADR-057, ADR-060). Pure and deterministic: rules S0–N4 in order, first match wins, mandatory default NeedsReview.
/// <list type="bullet">
/// <item>With an open ambiguity only A5 (target-enforced idempotency, judged on every ambiguous attempt's OWN snapshot) and
/// A6e (an authoritative, fresh <c>NotFound</c> observed at least the commit latency plus skew after the dispatch horizon)
/// can allow a repeat.</item>
/// <item>S1b precedes everything that can allow: while any attempt could still dispatch, nothing is admitted.</item>
/// <item>A fence is never evidence that a dispatched side effect failed; it only provides the dispatch horizon.</item>
/// </list>
/// </summary>
public static class RepeatSafetyEvaluator
{
    public static RepeatDecision Evaluate(ValidatedWorkflow current, Guid operationId, IReadOnlyList<AttemptSummary> history, DateTimeOffset now, RepeatSafetyPolicy policy) =>
        Evaluate(CurrentWorkflow.From(current), operationId, history, now, policy);

    public static RepeatDecision Evaluate(CurrentWorkflow current, Guid operationId, IReadOnlyList<AttemptSummary> history, DateTimeOffset now, RepeatSafetyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        // S0 Integrity.
        if (!IsValid(history, operationId))
        {
            return NeedsReview(RepeatReason.HistoryInvalid);
        }

        var attempts = history.OrderBy(a => a.AttemptNumber).ToList();

        // S1 In flight.
        if (attempts.Any(a => a.State is AttemptState.Reserved or AttemptState.Started))
        {
            return Decision(RepeatDecisionKind.InFlight, RepeatReason.AttemptInFlight);
        }

        // S1b Unfenced: an attempt that could still dispatch blocks everything that could allow.
        if (attempts.Any(a => !IsFenced(a)))
        {
            return Decision(RepeatDecisionKind.AwaitingFence, RepeatReason.UnfencedAttempt);
        }

        // S2 Empty.
        if (attempts.Count == 0)
        {
            return new RepeatDecision(RepeatDecisionKind.Allowed, SafetyEvidence.NoPriorAttempt, null, RepeatReason.NoPriorAttempt, null);
        }

        var normal = attempts.Where(a => a.Intent == ExecutionIntent.Normal).ToList();
        var ambiguous = normal.Where(IsAmbiguous).ToList();
        var aStar = ambiguous.LastOrDefault();

        // S3 Resolved: a resolving Normal attempt after A* (or anywhere without A*).
        var resolving = normal.LastOrDefault(a => IsResolving(a) && (aStar is null || a.AttemptNumber > aStar.AttemptNumber));
        if (resolving is not null)
        {
            return Resolved(resolving.Outcome == WorkflowOutcome.Succeeded ? Resolution.Succeeded : Resolution.BusinessFailure, RepeatReason.Resolved);
        }

        return aStar is null
            ? EvaluateWithoutAmbiguity(attempts, normal)
            : EvaluateAmbiguity(current, attempts, normal, ambiguous, aStar, now, policy);
    }

    /// <summary>
    /// The executor guard (§10.5): Normal requires Allowed; Reconcile is admitted unless the decision is InFlight,
    /// AwaitingFence or HistoryInvalid, or the workflow has no reconcile section.
    /// </summary>
    public static bool Admits(ExecutionIntent intent, RepeatDecision decision, bool hasReconcileSection)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return intent == ExecutionIntent.Normal
            ? decision.Kind == RepeatDecisionKind.Allowed
            : hasReconcileSection
              && decision.Kind is not (RepeatDecisionKind.InFlight or RepeatDecisionKind.AwaitingFence)
              && decision.Reason != RepeatReason.HistoryInvalid;
    }

    // ----- Derived definitions -----

    /// <summary>SE(a): Started for Started or Unknown.</summary>
    public static bool SideEffectStarted(AttemptSummary attempt) => attempt.SideEffect is SideEffectState.Started or SideEffectState.Unknown;

    /// <summary>Fenced(a) per the §7.2 table; an attempt that started no side effect has nothing to dispatch.</summary>
    public static bool IsFenced(AttemptSummary attempt) => !SideEffectStarted(attempt) || FenceTime(attempt) is not null;

    /// <summary>
    /// FenceTime(a), the earliest applicable: <c>FinishedAt</c> of a (late) completion with ExecutionFenced; <c>AbandonedAt</c>
    /// of an abandoned entry without checkpoint; an external <c>FencedAt</c>. Never derived from an assumed timeout.
    /// This is also the dispatch horizon H (§7.6.6).
    /// </summary>
    public static DateTimeOffset? FenceTime(AttemptSummary attempt)
    {
        DateTimeOffset? earliest = null;
        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is { } value && (earliest is null || value < earliest))
            {
                earliest = value;
            }
        }

        if (attempt.ExecutionFenced)
        {
            Consider(attempt.FinishedAt);
        }

        if (attempt.State == AttemptState.Abandoned && attempt.SideEffectCheckpointAt is null && attempt.SideEffect == SideEffectState.NotStarted)
        {
            Consider(attempt.AbandonedAt);
        }

        Consider(attempt.FencedAt);
        return earliest;
    }

    private static bool IsResolving(AttemptSummary a) =>
        a.Intent == ExecutionIntent.Normal
        && (a.Outcome == WorkflowOutcome.Succeeded
            || (SideEffectStarted(a) && a.Outcome == WorkflowOutcome.Failed && a.Error is { Category: ErrorCategory.Business, IsTransient: false }));

    private static bool IsAmbiguous(AttemptSummary a) => a.Intent == ExecutionIntent.Normal && SideEffectStarted(a) && !IsResolving(a);

    private static bool IsPermanent(AttemptSummary a) => a.Error is { IsTransient: false } error && error.Category != ErrorCategory.Cancelled;

    private static bool IsValid(IReadOnlyList<AttemptSummary> history, Guid operationId)
    {
        var numbers = new HashSet<int>();
        foreach (var a in history)
        {
            if (a.OperationId != operationId || a.AttemptNumber <= 0 || !numbers.Add(a.AttemptNumber))
            {
                return false;
            }

            if (a.State == AttemptState.Completed && a.Outcome is null)
            {
                return false;
            }

            if (a.State != AttemptState.Reserved && (a.Snapshot is null || a.StartedAt is null))
            {
                return false;
            }

            if (a.Intent == ExecutionIntent.Reconcile && a.Outcome is WorkflowOutcome.ReconciledFound or WorkflowOutcome.ReconciledNotFound && a.Reconciliation is null)
            {
                return false;
            }
        }

        return true;
    }

    // ----- No open ambiguity: N1–N4 -----

    private static RepeatDecision EvaluateWithoutAmbiguity(List<AttemptSummary> attempts, List<AttemptSummary> normal)
    {
        var latestNormal = normal.LastOrDefault();

        // N1 Permanent failure before any side effect.
        if (latestNormal is { Outcome: WorkflowOutcome.Failed } && !SideEffectStarted(latestNormal) && IsPermanent(latestNormal))
        {
            return Resolved(Resolution.PermanentFailureBeforeSideEffect, RepeatReason.PermanentFailureBeforeSideEffect);
        }

        // N2 Reconciled first.
        if (normal.Count == 0 && attempts.Any(a => a.Intent == ExecutionIntent.Reconcile && a.Outcome == WorkflowOutcome.ReconciledFound))
        {
            return Resolved(Resolution.ReconciledFound, RepeatReason.ReconciledFound);
        }

        // N3 No side effect started anywhere, and the latest Normal attempt (if any) ended retryably.
        var latestRetryable = latestNormal is null
            || (latestNormal.Outcome == WorkflowOutcome.Failed && latestNormal.Error is { IsTransient: true })
            || latestNormal.Outcome == WorkflowOutcome.Cancelled
            || latestNormal.State == AttemptState.Abandoned;
        if (normal.All(a => !SideEffectStarted(a)) && latestRetryable)
        {
            return new RepeatDecision(RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted, null, RepeatReason.NoSideEffectStarted, null);
        }

        // N4 Default.
        return NeedsReview(RepeatReason.NoMatchingRule);
    }

    // ----- Open ambiguity: A1–A8 -----

    private static RepeatDecision EvaluateAmbiguity(
        CurrentWorkflow current,
        List<AttemptSummary> attempts,
        List<AttemptSummary> normal,
        List<AttemptSummary> ambiguous,
        AttemptSummary aStar,
        DateTimeOffset now,
        RepeatSafetyPolicy policy)
    {
        var recs = attempts.Where(a => a.Intent == ExecutionIntent.Reconcile && a.AttemptNumber > aStar.AttemptNumber).ToList();

        // A1 Found.
        if (recs.Any(r => r.Outcome == WorkflowOutcome.ReconciledFound))
        {
            return Resolved(Resolution.ReconciledFound, RepeatReason.ReconciledFound);
        }

        // A2 A reconcile asked for a human, or failed permanently.
        if (recs.Any(r => r.Outcome == WorkflowOutcome.NeedsReview || (r.Outcome == WorkflowOutcome.Failed && IsPermanent(r))))
        {
            return NeedsReview(RepeatReason.ReconcileNeedsReview);
        }

        // A3 The latest Normal attempt after A* failed permanently before any side effect: the workflow is broken.
        var latestAfter = normal.LastOrDefault(a => a.AttemptNumber > aStar.AttemptNumber);
        if (latestAfter is { Outcome: WorkflowOutcome.Failed } && !SideEffectStarted(latestAfter) && IsPermanent(latestAfter))
        {
            return NeedsReview(RepeatReason.PermanentFailureWithOpenAmbiguity);
        }

        // A4 Reconcile budget: transient failures, cancellations and abandoned reconciles.
        var failedRecs = recs.Count(r => (r.Outcome == WorkflowOutcome.Failed && !IsPermanent(r)) || r.Outcome == WorkflowOutcome.Cancelled || (r.State == AttemptState.Abandoned && r.Outcome is null));
        if (failedRecs >= policy.MaxReconcileFailures)
        {
            return NeedsReview(RepeatReason.ReconcileBudgetExhausted);
        }

        // A4b Ambiguity budget.
        if (ambiguous.Count >= policy.MaxAmbiguousAttempts)
        {
            return NeedsReview(RepeatReason.AmbiguityBudgetExhausted);
        }

        // A5 Target-enforced idempotency: every ambiguous attempt's OWN snapshot, and the current version.
        if (ambiguous.All(a => a.Snapshot is { TargetEnforced: true, OperationIdSent: true }) && current.TargetEnforced && current.OperationIdSent)
        {
            return new RepeatDecision(RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency, null, RepeatReason.TargetEnforcedIdempotency, null);
        }

        // S1b guarantees every attempt is fenced here, so A* has a dispatch horizon.
        var horizon = FenceTime(aStar)!.Value;

        // A6 The latest NotFound after A*.
        var latestNotFound = recs.LastOrDefault(r => r.Outcome == WorkflowOutcome.ReconciledNotFound);
        if (latestNotFound?.Reconciliation is { } evidence)
        {
            if (evidence.Policy.Level == EvidenceLevel.Advisory)
            {
                return NeedsReview(RepeatReason.AdvisoryNotFound);
            }

            if (evidence.Policy.MatchKey == MatchKeyKind.OperationId && aStar.Snapshot?.OperationIdSent != true)
            {
                return NeedsReview(RepeatReason.KeyNotSent);
            }

            var earliest = horizon + TimeSpan.FromSeconds(evidence.Policy.MaxCommitLatencySeconds) + policy.ClockSkewAllowance;
            if (evidence.ObservedAt < earliest)
            {
                return new RepeatDecision(RepeatDecisionKind.ReconcileTooEarly, SafetyEvidence.None, null, RepeatReason.EvidenceTooEarly, earliest > now ? earliest : now);
            }

            if (now - evidence.ObservedAt > TimeSpan.FromSeconds(evidence.Policy.MaxEvidenceAgeSeconds))
            {
                return current.HasReconcileSection
                    ? Decision(RepeatDecisionKind.ReconcileRequired, RepeatReason.EvidenceExpired)
                    : NeedsReview(RepeatReason.EvidenceExpired);
            }

            return new RepeatDecision(RepeatDecisionKind.Allowed, SafetyEvidence.AuthoritativeNotFound, null, RepeatReason.AuthoritativeNotFound, null);
        }

        // A7 Reconcile next.
        if (current.HasReconcileSection)
        {
            if (current.Reconcile is { Level: EvidenceLevel.Authoritative } policyNow)
            {
                var earliest = horizon + TimeSpan.FromSeconds(policyNow.MaxCommitLatencySeconds) + policy.ClockSkewAllowance;
                if (now < earliest)
                {
                    return new RepeatDecision(RepeatDecisionKind.ReconcileTooEarly, SafetyEvidence.None, null, RepeatReason.EvidenceTooEarly, earliest);
                }
            }

            return Decision(RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
        }

        // A8 No safe path.
        return NeedsReview(RepeatReason.NoSafePath);
    }

    private static RepeatDecision NeedsReview(RepeatReason reason) => Decision(RepeatDecisionKind.NeedsReview, reason);

    private static RepeatDecision Decision(RepeatDecisionKind kind, RepeatReason reason) => new(kind, SafetyEvidence.None, null, reason, null);

    private static RepeatDecision Resolved(Resolution resolution, RepeatReason reason) => new(RepeatDecisionKind.NotNeeded, SafetyEvidence.Resolved, resolution, reason, null);
}
