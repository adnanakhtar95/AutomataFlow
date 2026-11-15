using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;

namespace AutomataFlow.UnitTests.Application.Workflows.Step6;

/// <summary>Builders for evaluator histories (§10.5). "T" = targetEnforced + operationIdSent snapshot.</summary>
internal static class Hist
{
    public static readonly Guid Op = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    public static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Default dispatch horizon of the ambiguous attempt: T0 + 60 s.</summary>
    public static readonly DateTimeOffset H = T0.AddSeconds(60);

    public static readonly ReconcilePolicySnapshot Authoritative = new(EvidenceLevel.Authoritative, MatchKeyKind.OperationId, 10, 300);
    public static readonly ReconcilePolicySnapshot Advisory = new(EvidenceLevel.Advisory, MatchKeyKind.BusinessKey, 0, 0);

    public static readonly CurrentWorkflow CurrentT = new(true, true, true, Authoritative);
    public static readonly CurrentWorkflow CurrentNotT = new(false, false, true, Authoritative);
    public static readonly CurrentWorkflow CurrentNoReconcile = new(false, false, false, null);

    public static readonly RepeatSafetyPolicy Policy = RepeatSafetyPolicy.Create(1, 3, 3);

    public static AttemptSnapshot Snap(bool targetEnforced = true, bool operationIdSent = true, int version = 1) =>
        new("sha256:" + new string('b', 64), version, 120, targetEnforced, operationIdSent, Authoritative);

    public static ErrorSummary Transient(string code = "Timeout") => new(code, ErrorCategory.Transient, true);

    public static ErrorSummary Permanent(string code = "ElementNotUnique") => new(code, ErrorCategory.Permanent, false);

    public static ErrorSummary Business(string code = "DuplicateCustomer", bool transient = false) => new(code, ErrorCategory.Business, transient);

    public static ErrorSummary Cancelled() => new("Cancelled", ErrorCategory.Cancelled, false);

    public static AttemptSummary Normal(
        int number,
        WorkflowOutcome? outcome,
        SideEffectState sideEffect,
        ErrorSummary? error = null,
        AttemptState state = AttemptState.Completed,
        bool executionFenced = true,
        DateTimeOffset? finishedAt = null,
        AttemptSnapshot? snapshot = null,
        DateTimeOffset? abandonedAt = null,
        DateTimeOffset? fencedAt = null,
        FenceSource? fenceSource = null,
        Guid? operation = null) =>
        new(operation ?? Op, number, ExecutionIntent.Normal, state, outcome, error, sideEffect, snapshot ?? Snap(),
            T0, sideEffect == SideEffectState.NotStarted ? null : T0.AddSeconds(5),
            state == AttemptState.Completed || outcome is not null ? finishedAt ?? H : null,
            abandonedAt, executionFenced, fencedAt, fenceSource, null);

    public static AttemptSummary Rec(int number, WorkflowOutcome? outcome, ReconciliationEvidence? evidence = null, ErrorSummary? error = null, AttemptState state = AttemptState.Completed) =>
        new(Op, number, ExecutionIntent.Reconcile, state, outcome, error, SideEffectState.NotStarted, Snap(), T0, null,
            outcome is null ? null : H.AddSeconds(30), state == AttemptState.Abandoned ? H.AddSeconds(40) : null, outcome is not null, null, null, evidence);

    public static ReconciliationEvidence NotFoundAt(DateTimeOffset observedAt, ReconcilePolicySnapshot? policy = null) =>
        new(ReconciliationFinding.NotFound, observedAt, policy ?? Authoritative);

    public static ReconciliationEvidence FoundAt(DateTimeOffset observedAt) => new(ReconciliationFinding.Found, observedAt, Authoritative);

    /// <summary>An ambiguous Normal attempt (Indeterminate after a checkpoint), fenced at its completion H.</summary>
    public static AttemptSummary Ambiguous(int number, bool targetEnforced = true, bool operationIdSent = true, DateTimeOffset? horizon = null) =>
        Normal(number, WorkflowOutcome.Indeterminate, SideEffectState.Started, Transient(), finishedAt: horizon ?? H, snapshot: Snap(targetEnforced, operationIdSent));

    public static RepeatDecision Eval(CurrentWorkflow current, DateTimeOffset now, params AttemptSummary[] history) =>
        RepeatSafetyEvaluator.Evaluate(current, Op, history, now, Policy);
}

/// <summary>Phase 4 step 6: <see cref="RepeatSafetyEvaluator"/> rules S0–N4 (§10.5; E1–E42 of §13.1).</summary>
public class RepeatSafetyEvaluatorTests
{
    private static readonly DateTimeOffset Now = Hist.T0.AddMinutes(30);

    private static void AssertDecision(RepeatDecision decision, RepeatDecisionKind kind, RepeatReason reason)
    {
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(reason, decision.Reason);
    }

    // ----- S0–S3 -----

    [Fact]
    public void E1_an_entry_for_another_operation_invalidates_the_history()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, WorkflowOutcome.Succeeded, SideEffectState.Started, operation: Guid.NewGuid())),
            RepeatDecisionKind.NeedsReview, RepeatReason.HistoryInvalid);
    }

    [Fact]
    public void E2_duplicate_attempt_numbers_or_a_completed_entry_without_outcome_invalidate_the_history()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Ambiguous(1)), RepeatDecisionKind.NeedsReview, RepeatReason.HistoryInvalid);
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, null, SideEffectState.NotStarted)), RepeatDecisionKind.NeedsReview, RepeatReason.HistoryInvalid);
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(0, WorkflowOutcome.Succeeded, SideEffectState.Started)), RepeatDecisionKind.NeedsReview, RepeatReason.HistoryInvalid);
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Rec(1, WorkflowOutcome.ReconciledNotFound)), RepeatDecisionKind.NeedsReview, RepeatReason.HistoryInvalid);
    }

    [Fact]
    public void E3_a_started_entry_is_in_flight()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, null, SideEffectState.NotStarted, state: AttemptState.Started)),
            RepeatDecisionKind.InFlight, RepeatReason.AttemptInFlight);
    }

    [Fact]
    public void E4_an_empty_history_allows_the_first_attempt()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now);

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.NoPriorAttempt), (decision.Kind, decision.Evidence));
    }

    [Fact]
    public void E5_a_succeeded_attempt_resolves_the_record()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, WorkflowOutcome.Succeeded, SideEffectState.Started));

        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.Succeeded), (decision.Kind, decision.Resolution));
    }

    [Fact]
    public void E6_a_declared_permanent_business_failure_after_the_side_effect_resolves_the_record()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, WorkflowOutcome.Failed, SideEffectState.Started, Hist.Business()));

        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.BusinessFailure), (decision.Kind, decision.Resolution));
    }

    [Fact]
    public void E7_a_technical_failure_after_the_side_effect_is_ambiguous_never_resolved()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Now, Hist.Normal(1, WorkflowOutcome.Failed, SideEffectState.Started, Hist.Permanent()));

        Assert.NotEqual(RepeatDecisionKind.NotNeeded, decision.Kind);
        Assert.NotEqual(RepeatDecisionKind.Allowed, decision.Kind);
    }

    [Fact]
    public void E8_a_transient_failure_before_any_side_effect_allows_a_retry()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Now, Hist.Normal(1, WorkflowOutcome.Failed, SideEffectState.NotStarted, Hist.Transient()));

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), (decision.Kind, decision.Evidence));
    }

    [Fact]
    public void E9_a_permanent_failure_before_any_side_effect_needs_no_repeat()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Now, Hist.Normal(1, WorkflowOutcome.Failed, SideEffectState.NotStarted, Hist.Permanent("InvalidSelector")));

        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.PermanentFailureBeforeSideEffect), (decision.Kind, decision.Resolution));
    }

    // ----- Open ambiguity: A1–A8 -----

    [Fact]
    public void E10_target_enforced_idempotency_allows_a_repeat()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1));

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency), (decision.Kind, decision.Evidence));
    }

    [Fact]
    public void E11_a_Found_reconcile_wins_over_idempotency()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Rec(2, WorkflowOutcome.ReconciledFound, Hist.FoundAt(Hist.H.AddSeconds(20))));

        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.ReconciledFound), (decision.Kind, decision.Resolution));
    }

    [Fact]
    public void E12_a_reconcile_that_needs_review_wins_over_idempotency()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Rec(2, WorkflowOutcome.NeedsReview, error: Hist.Business("AmbiguousMatch"))),
            RepeatDecisionKind.NeedsReview, RepeatReason.ReconcileNeedsReview);
    }

    [Fact]
    public void E13_a_permanent_pre_side_effect_failure_never_hides_an_open_ambiguity()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Normal(2, WorkflowOutcome.Failed, SideEffectState.NotStarted, Hist.Permanent("InvalidSelector"))),
            RepeatDecisionKind.NeedsReview, RepeatReason.PermanentFailureWithOpenAmbiguity);
    }

    [Fact]
    public void E14_a_newer_version_cannot_vouch_for_an_older_ambiguous_attempt()
    {
        var decision = Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1, targetEnforced: false, operationIdSent: false));

        Assert.NotEqual(RepeatDecisionKind.Allowed, decision.Kind);
        AssertDecision(decision, RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
        AssertDecision(Hist.Eval(Hist.CurrentNoReconcile with { TargetEnforced = true, OperationIdSent = true }, Now, Hist.Ambiguous(1, false, false)),
            RepeatDecisionKind.NeedsReview, RepeatReason.NoSafePath);
    }

    [Fact]
    public void E15_every_ambiguous_attempt_must_be_target_enforced()
    {
        Assert.NotEqual(RepeatDecisionKind.Allowed, Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Ambiguous(2, targetEnforced: false)).Kind);
    }

    [Fact]
    public void E16_an_advisory_NotFound_never_authorizes_a_repeat()
    {
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, Hist.Ambiguous(1, false, false), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddMinutes(5), Hist.Advisory))),
            RepeatDecisionKind.NeedsReview, RepeatReason.AdvisoryNotFound);
    }

    [Fact]
    public void E17_an_operation_id_lookup_proves_nothing_when_the_key_was_not_sent()
    {
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, Hist.Ambiguous(1, targetEnforced: false, operationIdSent: false), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddMinutes(5)))),
            RepeatDecisionKind.NeedsReview, RepeatReason.KeyNotSent);
    }

    [Fact]
    public void E18_E19_an_authoritative_NotFound_counts_only_from_horizon_plus_latency_plus_skew()
    {
        var threshold = Hist.H.AddSeconds(10 + 1); // latency 10 s + skew 1 s
        var early = Hist.Eval(Hist.CurrentNotT, threshold, Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(threshold.AddTicks(-1))));
        AssertDecision(early, RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
        Assert.Equal(threshold, early.RetryAfter);

        var exact = Hist.Eval(Hist.CurrentNotT, threshold, Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(threshold)));
        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.AuthoritativeNotFound), (exact.Kind, exact.Evidence));
    }

    [Fact]
    public void E20_expired_evidence_requires_a_new_reconcile()
    {
        var observed = Hist.H.AddMinutes(1);
        AssertDecision(Hist.Eval(Hist.CurrentNotT, observed.AddSeconds(301), Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(observed))),
            RepeatDecisionKind.ReconcileRequired, RepeatReason.EvidenceExpired);
        Assert.Equal(RepeatDecisionKind.Allowed, Hist.Eval(Hist.CurrentNotT, observed.AddSeconds(300), Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(observed))).Kind);
        AssertDecision(Hist.Eval(Hist.CurrentNoReconcile, observed.AddSeconds(301), Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(observed))),
            RepeatDecisionKind.NeedsReview, RepeatReason.EvidenceExpired);
    }

    [Fact]
    public void E21_a_later_failed_login_keeps_fresh_evidence_valid_until_it_expires()
    {
        var observed = Hist.H.AddMinutes(1);
        AttemptSummary[] history =
        [
            Hist.Ambiguous(1, false, true),
            Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(observed)),
            Hist.Normal(3, WorkflowOutcome.Failed, SideEffectState.NotStarted, Hist.Transient("SessionTimeout")),
        ];

        Assert.Equal(RepeatDecisionKind.Allowed, Hist.Eval(Hist.CurrentNotT, observed.AddSeconds(100), history).Kind);
        Assert.Equal(RepeatDecisionKind.ReconcileRequired, Hist.Eval(Hist.CurrentNotT, observed.AddSeconds(400), history).Kind);
    }

    [Fact]
    public void E22_evidence_from_before_the_latest_ambiguous_attempt_is_ignored()
    {
        var secondHorizon = Hist.H.AddMinutes(10);
        AttemptSummary[] history =
        [
            Hist.Ambiguous(1, false, true),
            Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddMinutes(5))),
            Hist.Ambiguous(3, false, true, horizon: secondHorizon),
        ];

        AssertDecision(Hist.Eval(Hist.CurrentNotT, secondHorizon.AddSeconds(5), history), RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
        AssertDecision(Hist.Eval(Hist.CurrentNotT, secondHorizon.AddSeconds(20), history), RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
    }

    [Fact]
    public void E23_a_permanently_failed_reconcile_needs_review()
    {
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.Failed, error: Hist.Permanent("InvalidSelector"))),
            RepeatDecisionKind.NeedsReview, RepeatReason.ReconcileNeedsReview);
    }

    [Fact]
    public void E24_failed_cancelled_and_abandoned_reconciles_are_budgeted()
    {
        AttemptSummary[] three =
        [
            Hist.Ambiguous(1, false, true),
            Hist.Rec(2, WorkflowOutcome.Failed, error: Hist.Transient("LookupInconclusive")),
            Hist.Rec(3, WorkflowOutcome.Cancelled, error: Hist.Cancelled()),
            Hist.Rec(4, null, state: AttemptState.Abandoned),
        ];
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, three), RepeatDecisionKind.NeedsReview, RepeatReason.ReconcileBudgetExhausted);
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, three[..3]), RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
    }

    [Fact]
    public void E25_a_fenced_abandoned_attempt_without_any_safe_path_needs_review()
    {
        var abandoned = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false,
            abandonedAt: Hist.H, fencedAt: Hist.H.AddMinutes(1), fenceSource: FenceSource.ProcessDeathConfirmed, snapshot: Hist.Snap(false, false));

        AssertDecision(Hist.Eval(Hist.CurrentNoReconcile, Now, abandoned), RepeatDecisionKind.NeedsReview, RepeatReason.NoSafePath);
    }

    [Fact]
    public void E26_an_abandoned_attempt_without_checkpoint_started_no_side_effect()
    {
        var abandoned = Hist.Normal(1, null, SideEffectState.NotStarted, state: AttemptState.Abandoned, executionFenced: false, abandonedAt: Hist.H);

        var decision = Hist.Eval(Hist.CurrentNoReconcile, Now, abandoned);

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), (decision.Kind, decision.Evidence));
    }

    [Fact]
    public void E27_an_unknown_side_effect_state_is_treated_as_started_and_unfenced()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, null, SideEffectState.Unknown, state: AttemptState.Abandoned, executionFenced: false, abandonedAt: Hist.H)),
            RepeatDecisionKind.AwaitingFence, RepeatReason.UnfencedAttempt);
    }

    [Fact]
    public void E28_E41_latency_counts_from_the_process_death_fence_time_never_from_start_plus_timeout()
    {
        var p = Hist.H.AddMinutes(7); // FencedAt, committed after unit-empty AND socket drain
        var abandoned = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false,
            abandonedAt: Hist.H, fencedAt: p, fenceSource: FenceSource.ProcessDeathConfirmed, snapshot: Hist.Snap(false, true));

        Assert.Equal(p, RepeatSafetyEvaluator.FenceTime(abandoned));
        AssertDecision(Hist.Eval(Hist.CurrentNotT, p.AddSeconds(20), abandoned, Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(p.AddSeconds(10)))),
            RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
        Assert.Equal(RepeatDecisionKind.Allowed,
            Hist.Eval(Hist.CurrentNotT, p.AddSeconds(20), abandoned, Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(p.AddSeconds(11)))).Kind);

        var executorFenced = Hist.Ambiguous(1, false, true, horizon: p);
        AssertDecision(Hist.Eval(Hist.CurrentNotT, p.AddSeconds(20), executorFenced, Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(p.AddSeconds(10)))),
            RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
    }

    [Fact]
    public void E29_a_reconcile_Found_without_any_normal_attempt_resolves_the_record()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Now, Hist.Rec(1, WorkflowOutcome.ReconciledFound, Hist.FoundAt(Hist.H)));

        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.ReconciledFound), (decision.Kind, decision.Resolution));
    }

    [Fact]
    public void E30_a_reconcile_NotFound_without_any_normal_attempt_allows_the_first_attempt()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Now, Hist.Rec(1, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H)));

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), (decision.Kind, decision.Evidence));
    }

    [Fact]
    public void E31_history_order_does_not_matter()
    {
        AttemptSummary[] ordered = [Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddMinutes(1))), Hist.Normal(3, WorkflowOutcome.Failed, SideEffectState.NotStarted, Hist.Transient())];

        Assert.Equal(Hist.Eval(Hist.CurrentNotT, Now, ordered), Hist.Eval(Hist.CurrentNotT, Now, [.. ordered.Reverse()]));
    }

    [Fact]
    public void E32_an_observation_inside_the_clock_skew_allowance_is_too_early()
    {
        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, Hist.Ambiguous(1, false, true), Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddSeconds(10.5)))),
            RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
    }

    [Fact]
    public void E34_the_ambiguity_budget_beats_idempotency()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Ambiguous(2), Hist.Ambiguous(3)), RepeatDecisionKind.NeedsReview, RepeatReason.AmbiguityBudgetExhausted);
        Assert.Equal(RepeatDecisionKind.Allowed, Hist.Eval(Hist.CurrentT, Now, Hist.Ambiguous(1), Hist.Ambiguous(2)).Kind);
    }

    [Fact]
    public void E35_an_unfenced_checkpointed_attempt_blocks_even_target_enforced_idempotency()
    {
        var frozen = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false, abandonedAt: Hist.H);

        AssertDecision(Hist.Eval(Hist.CurrentT, Now, frozen), RepeatDecisionKind.AwaitingFence, RepeatReason.UnfencedAttempt);
    }

    [Fact]
    public void E36_a_completion_whose_revocation_was_not_confirmed_is_unfenced()
    {
        AssertDecision(Hist.Eval(Hist.CurrentT, Now, Hist.Normal(1, WorkflowOutcome.Indeterminate, SideEffectState.Started, Hist.Transient(), executionFenced: false)),
            RepeatDecisionKind.AwaitingFence, RepeatReason.UnfencedAttempt);
    }

    [Fact]
    public void E37_a_late_fenced_completion_fences_at_its_own_time()
    {
        var late = Hist.H.AddMinutes(3);
        var completed = Hist.Normal(1, WorkflowOutcome.Indeterminate, SideEffectState.Started, Hist.Transient(), state: AttemptState.Abandoned,
            executionFenced: true, finishedAt: late, abandonedAt: Hist.H, snapshot: Hist.Snap(false, true));

        Assert.Equal(late, RepeatSafetyEvaluator.FenceTime(completed));
        AssertDecision(Hist.Eval(Hist.CurrentNotT, late.AddSeconds(5), completed), RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
    }

    /// <summary>E38 (a late completion normally carries the barrier result; with ExecutionFenced it is resolved).</summary>
    [Fact]
    public void E38_a_late_succeeded_completion_resolves_the_record()
    {
        var completed = Hist.Normal(1, WorkflowOutcome.Succeeded, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: true, abandonedAt: Hist.H);

        Assert.Equal(RepeatDecisionKind.NotNeeded, Hist.Eval(Hist.CurrentT, Now, completed).Kind);
    }

    [Fact]
    public void E39_a_lookup_observed_while_the_attempt_was_unfenced_is_too_early()
    {
        var fencedLater = Hist.H.AddMinutes(10);
        var attempt = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false,
            abandonedAt: Hist.H, fencedAt: fencedLater, fenceSource: FenceSource.HumanConfirmed, snapshot: Hist.Snap(false, true));

        AssertDecision(Hist.Eval(Hist.CurrentNotT, fencedLater.AddMinutes(1), attempt, Hist.Rec(2, WorkflowOutcome.ReconciledNotFound, Hist.NotFoundAt(Hist.H.AddMinutes(5)))),
            RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
    }

    [Fact]
    public void E42_a_fence_never_resolves_an_ambiguity()
    {
        var fenced = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false,
            abandonedAt: Hist.H, fencedAt: Hist.H.AddMinutes(1), fenceSource: FenceSource.ProcessDeathConfirmed, snapshot: Hist.Snap(false, true));

        var decision = Hist.Eval(Hist.CurrentNotT, Now, fenced);

        AssertDecision(decision, RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
        Assert.Equal(SideEffectState.Started, fenced.SideEffect);
        Assert.Null(fenced.Outcome);
    }

    [Fact]
    public void A7_an_authoritative_current_version_waits_for_horizon_plus_latency_before_reconciling()
    {
        var decision = Hist.Eval(Hist.CurrentNotT, Hist.H.AddSeconds(3), Hist.Ambiguous(1, false, true));

        AssertDecision(decision, RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
        Assert.Equal(Hist.H.AddSeconds(11), decision.RetryAfter);
    }

    [Fact]
    public void N4_anything_unmatched_needs_review()
    {
        // A legacy/defective combination: Failed before any side effect with a non-transient Cancelled error. It is neither
        // permanent (N1) nor retryable (N3), so no rule claims it and the mandatory default applies.
        var legacy = Hist.Normal(1, WorkflowOutcome.Failed, SideEffectState.NotStarted, new ErrorSummary("Odd", ErrorCategory.Cancelled, false));

        AssertDecision(Hist.Eval(Hist.CurrentNotT, Now, legacy), RepeatDecisionKind.NeedsReview, RepeatReason.NoMatchingRule);
    }

    // ----- Executor guard -----

    [Fact]
    public void The_executor_guard_admits_normal_only_when_allowed_and_reconcile_unless_in_flight_unfenced_or_invalid()
    {
        var allowed = new RepeatDecision(RepeatDecisionKind.Allowed, SafetyEvidence.NoPriorAttempt, null, RepeatReason.NoPriorAttempt, null);
        var required = new RepeatDecision(RepeatDecisionKind.ReconcileRequired, SafetyEvidence.None, null, RepeatReason.ReconcileRequired, null);
        var tooEarly = new RepeatDecision(RepeatDecisionKind.ReconcileTooEarly, SafetyEvidence.None, null, RepeatReason.EvidenceTooEarly, Now);
        var awaiting = new RepeatDecision(RepeatDecisionKind.AwaitingFence, SafetyEvidence.None, null, RepeatReason.UnfencedAttempt, null);
        var invalid = new RepeatDecision(RepeatDecisionKind.NeedsReview, SafetyEvidence.None, null, RepeatReason.HistoryInvalid, null);

        Assert.True(RepeatSafetyEvaluator.Admits(ExecutionIntent.Normal, allowed, true));
        Assert.False(RepeatSafetyEvaluator.Admits(ExecutionIntent.Normal, required, true));
        Assert.True(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, required, true));
        Assert.True(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, tooEarly, true)); // harmless; its evidence is ignored (A6c)
        Assert.False(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, required, false));
        Assert.False(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, awaiting, true));
        Assert.False(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, invalid, true));
    }

    [Theory]
    [InlineData(-1, 3, 3)]
    [InlineData(61, 3, 3)]
    [InlineData(5, 0, 3)]
    [InlineData(5, 11, 3)]
    [InlineData(5, 3, 0)]
    [InlineData(5, 3, 11)]
    public void Policy_options_are_validated_with_the_documented_ranges(int skew, int reconcileFailures, int ambiguousAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RepeatSafetyPolicy.Create(skew, reconcileFailures, ambiguousAttempts));
    }

    /// <summary>F13: after FenceWaitSeconds the record went to NeedsReview; once fenced and requeued, normal rules apply from FencedAt.</summary>
    [Fact]
    public void F13_after_an_operator_requeue_the_rules_apply_from_the_fence_time()
    {
        var fencedAt = Hist.H.AddHours(2);
        var attempt = Hist.Normal(1, null, SideEffectState.Started, state: AttemptState.Abandoned, executionFenced: false,
            abandonedAt: Hist.H, fencedAt: fencedAt, fenceSource: FenceSource.HumanConfirmed, snapshot: Hist.Snap(false, true));

        AssertDecision(Hist.Eval(Hist.CurrentNotT, fencedAt.AddSeconds(5), attempt), RepeatDecisionKind.ReconcileTooEarly, RepeatReason.EvidenceTooEarly);
        AssertDecision(Hist.Eval(Hist.CurrentNotT, fencedAt.AddSeconds(12), attempt), RepeatDecisionKind.ReconcileRequired, RepeatReason.ReconcileRequired);
    }

    [Fact]
    public void The_snapshot_records_the_safety_profile_of_the_version_that_ran()
    {
        var workflow = WorkflowJson.Valid(WorkflowJson.Full);

        var snapshot = AttemptSnapshot.From(workflow, 7);

        Assert.Equal((workflow.DefinitionHash, 7, 90, true, true), (snapshot.DefinitionHash, snapshot.WorkflowVersion, snapshot.TimeoutSeconds, snapshot.TargetEnforced, snapshot.OperationIdSent));
        Assert.Equal(new ReconcilePolicySnapshot(EvidenceLevel.Authoritative, MatchKeyKind.OperationId, 30, 300), snapshot.Reconcile);
        Assert.Equal(new CurrentWorkflow(true, true, true, snapshot.Reconcile), CurrentWorkflow.From(workflow));
    }
}
