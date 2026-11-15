using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Domain.Jobs;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 5 step 6: record scheduling from attempt results and repeat-safety decisions.</summary>
public class RecordSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly SchedulerPolicy Policy = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    private static WorkflowExecutionResult Result(WorkflowOutcome outcome, string? code = null, RepeatDecision? refusal = null) =>
        new(outcome, outcome == WorkflowOutcome.Refused ? null : 1, code is null ? null : new ExecutionError(code, ErrorCategory.Transient, true, null, "m"),
            SideEffectState.NotStarted, true, refusal, null, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, [], [], [], Now, Now);

    private static RepeatDecision Decision(RepeatDecisionKind kind, SafetyEvidence evidence = SafetyEvidence.None, Resolution? resolution = null, RepeatReason reason = RepeatReason.NoPriorAttempt, DateTimeOffset? after = null) =>
        new(kind, evidence, resolution, reason, after);

    private static RecordTransition Next(WorkflowExecutionResult? result, RepeatDecision? decision, int attempts = 1, bool cancel = false, DateTimeOffset? since = null, RecordIntent intent = RecordIntent.Normal) =>
        RecordScheduler.Next(result, decision, attempts, intent, Policy, cancel, since, Now);

    [Theory]
    [InlineData("WorkflowTampered")]
    [InlineData("RecordInvalid")]
    [InlineData("PolicyViolation")]
    [InlineData("WorkflowTimeoutExceedsServerLimit")]
    [InlineData("ReconcileNotAvailable")]
    public void Permanent_refusals_fail_the_record(string code)
    {
        var t = Next(Result(WorkflowOutcome.Refused, code), null);
        Assert.Equal((JobRecordStatus.Failed, code), (t.Status, t.ErrorCode));
    }

    [Fact]
    public void An_unavailable_journal_or_unreadable_history_waits_and_never_decides_blind()
    {
        var refused = Next(Result(WorkflowOutcome.Refused, ExecutionErrorCodes.JournalUnavailable), null, intent: RecordIntent.Reconcile);
        Assert.Equal((JobRecordStatus.Pending, RecordIntent.Reconcile, Now.AddSeconds(10)), (refused.Status, refused.NextIntent, refused.NextAttemptAt!.Value));

        var unreadable = Next(Result(WorkflowOutcome.Indeterminate, "Timeout"), decision: null);
        Assert.Equal((JobRecordStatus.Pending, RecordIntent.Normal, ExecutionErrorCodes.JournalUnavailable), (unreadable.Status, unreadable.NextIntent, unreadable.ErrorCode));
    }

    [Fact]
    public void Resolutions_end_the_record()
    {
        Assert.Equal((JobRecordStatus.Succeeded, true), Pair(Next(Result(WorkflowOutcome.Succeeded), Decision(RepeatDecisionKind.NotNeeded, SafetyEvidence.Resolved, Resolution.Succeeded, RepeatReason.Resolved))));
        Assert.Equal((JobRecordStatus.Reconciled, true), Pair(Next(Result(WorkflowOutcome.ReconciledFound), Decision(RepeatDecisionKind.NotNeeded, SafetyEvidence.Resolved, Resolution.ReconciledFound, RepeatReason.ReconciledFound))));
        var business = Next(Result(WorkflowOutcome.Failed, "DuplicateCustomer"), Decision(RepeatDecisionKind.NotNeeded, SafetyEvidence.Resolved, Resolution.BusinessFailure, RepeatReason.Resolved));
        Assert.Equal((JobRecordStatus.Failed, "DuplicateCustomer"), (business.Status, business.ErrorCode));

        static (JobRecordStatus, bool) Pair(RecordTransition t) => (t.Status, t.StoreResults);
    }

    [Fact]
    public void Allowed_repeats_use_exponential_backoff_until_the_attempt_limit()
    {
        var first = Next(Result(WorkflowOutcome.Failed, "Timeout"), Decision(RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), attempts: 1);
        var second = Next(Result(WorkflowOutcome.Failed, "Timeout"), Decision(RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), attempts: 2);
        Assert.Equal((JobRecordStatus.Pending, RecordIntent.Normal, Now.AddSeconds(30)), (first.Status, first.NextIntent, first.NextAttemptAt!.Value));
        Assert.Equal(Now.AddSeconds(60), second.NextAttemptAt);
        Assert.Equal(SchedulerPolicy.MaxBackoff, Policy.Backoff(40));

        var exhausted = Next(Result(WorkflowOutcome.Failed, "Timeout"), Decision(RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), attempts: 3);
        Assert.Equal((JobRecordStatus.Failed, "Timeout"), (exhausted.Status, exhausted.ErrorCode));
    }

    [Fact]
    public void Exhausted_attempts_with_an_open_ambiguity_need_review_never_fail()
    {
        var t = Next(Result(WorkflowOutcome.Indeterminate, "Timeout"), Decision(RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency), attempts: 3);
        Assert.Equal((JobRecordStatus.NeedsReview, "AttemptsExhaustedWithOpenAmbiguity"), (t.Status, t.ErrorCode));
    }

    [Fact]
    public void Reconciliation_is_scheduled_now_or_after_the_evidence_wait()
    {
        var now = Next(Result(WorkflowOutcome.Indeterminate, "Timeout"), Decision(RepeatDecisionKind.ReconcileRequired));
        Assert.Equal((JobRecordStatus.Pending, RecordIntent.Reconcile, Now), (now.Status, now.NextIntent, now.NextAttemptAt!.Value));

        var later = Next(Result(WorkflowOutcome.ReconciledNotFound), Decision(RepeatDecisionKind.ReconcileTooEarly, after: Now.AddSeconds(47)));
        Assert.Equal((RecordIntent.Reconcile, Now.AddSeconds(47)), (later.NextIntent, later.NextAttemptAt!.Value));
    }

    [Fact]
    public void An_unfenced_attempt_is_rechecked_and_needs_review_after_the_fence_wait()
    {
        var first = Next(Result(WorkflowOutcome.Refused, ExecutionErrorCodes.AwaitingFence), Decision(RepeatDecisionKind.AwaitingFence, reason: RepeatReason.UnfencedAttempt));
        Assert.Equal((JobRecordStatus.AwaitingFence, Now, Now.AddSeconds(30)), (first.Status, first.AwaitingFenceSince!.Value, first.NextAttemptAt!.Value));

        var stillWaiting = Next(null, Decision(RepeatDecisionKind.AwaitingFence), since: Now.AddMinutes(-14));
        Assert.Equal((JobRecordStatus.AwaitingFence, Now.AddMinutes(-14)), (stillWaiting.Status, stillWaiting.AwaitingFenceSince!.Value));

        var expired = Next(null, Decision(RepeatDecisionKind.AwaitingFence), since: Now.AddMinutes(-15));
        Assert.Equal((JobRecordStatus.NeedsReview, "FenceNotConfirmed"), (expired.Status, expired.ErrorCode));
    }

    [Fact]
    public void A_cancelled_job_never_hides_an_ambiguity()
    {
        Assert.Equal(JobRecordStatus.Cancelled, Next(null, Decision(RepeatDecisionKind.Allowed, SafetyEvidence.NoPriorAttempt), attempts: 0, cancel: true).Status);
        Assert.Equal(JobRecordStatus.Cancelled, Next(null, Decision(RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), cancel: true).Status);
        Assert.Equal(JobRecordStatus.NeedsReview, Next(null, Decision(RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency), cancel: true).Status);
        Assert.Equal(JobRecordStatus.NeedsReview, Next(null, Decision(RepeatDecisionKind.ReconcileRequired), cancel: true).Status);
        Assert.Equal(JobRecordStatus.NeedsReview, Next(null, Decision(RepeatDecisionKind.AwaitingFence), cancel: true).Status);
        Assert.Equal(JobRecordStatus.Succeeded, Next(null, Decision(RepeatDecisionKind.NotNeeded, SafetyEvidence.Resolved, Resolution.Succeeded), cancel: true).Status);
    }

    [Fact]
    public void In_flight_and_review_decisions()
    {
        var inFlight = Next(Result(WorkflowOutcome.Refused, ExecutionErrorCodes.AttemptInFlight), Decision(RepeatDecisionKind.InFlight, reason: RepeatReason.AttemptInFlight));
        Assert.Equal((JobRecordStatus.Pending, Now.AddSeconds(5)), (inFlight.Status, inFlight.NextAttemptAt!.Value));

        var review = Next(Result(WorkflowOutcome.ReconciledNotFound), Decision(RepeatDecisionKind.NeedsReview, reason: RepeatReason.ReconcileNeedsReview));
        Assert.Equal((JobRecordStatus.NeedsReview, "ReconcileNeedsReview"), (review.Status, review.ErrorCode));
        Assert.True(review.IsTerminal);
        Assert.False(inFlight.IsTerminal);
        Assert.Equal(EngineErrorCodes.WorkflowTampered, Next(Result(WorkflowOutcome.Refused, EngineErrorCodes.WorkflowTampered), null).ErrorCode);
    }
}
