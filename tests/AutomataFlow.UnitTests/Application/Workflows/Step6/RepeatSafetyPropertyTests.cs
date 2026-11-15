using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;

namespace AutomataFlow.UnitTests.Application.Workflows.Step6;

/// <summary>
/// E33: 10,000 random histories. The properties are checked INDEPENDENTLY of the rule implementation: an Allowed decision
/// must be backed by positive, attempt-bound evidence whose preconditions the test recomputes itself; nothing is admitted
/// while any attempt could still dispatch; ambiguous outcomes never become ordinary safe retries.
/// </summary>
public class RepeatSafetyPropertyTests
{
    private static readonly WorkflowOutcome?[] NormalOutcomes =
        [WorkflowOutcome.Succeeded, WorkflowOutcome.Failed, WorkflowOutcome.Indeterminate, WorkflowOutcome.Cancelled, null];

    private static readonly WorkflowOutcome?[] ReconcileOutcomes =
        [WorkflowOutcome.ReconciledFound, WorkflowOutcome.ReconciledNotFound, WorkflowOutcome.NeedsReview, WorkflowOutcome.Failed, WorkflowOutcome.Cancelled, null];

    [Fact]
    public void E33_random_histories_never_allow_without_positive_evidence()
    {
        var random = new Random(20261115);
        var counts = new Dictionary<RepeatDecisionKind, int>();
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var history = RandomHistory(random);
            var current = new CurrentWorkflow(random.Next(2) == 0, random.Next(2) == 0, random.Next(4) != 0, random.Next(2) == 0 ? Hist.Authoritative : Hist.Advisory);
            var now = Hist.T0.AddSeconds(random.Next(0, 3_600));

            var decision = RepeatSafetyEvaluator.Evaluate(current, Hist.Op, history, now, Hist.Policy);
            counts[decision.Kind] = counts.GetValueOrDefault(decision.Kind) + 1;

            // Deterministic and order-independent.
            Assert.Equal(decision, RepeatSafetyEvaluator.Evaluate(current, Hist.Op, [.. history.OrderBy(_ => random.Next())], now, Hist.Policy));

            var unfenced = history.Any(a => a.SideEffect != SideEffectState.NotStarted && !(a.ExecutionFenced && a.FinishedAt is not null) && a.FencedAt is null);
            var inFlight = history.Any(a => a.State is AttemptState.Reserved or AttemptState.Started);
            if (unfenced || inFlight)
            {
                Assert.NotEqual(RepeatDecisionKind.Allowed, decision.Kind);
                Assert.False(RepeatSafetyEvaluator.Admits(ExecutionIntent.Reconcile, decision, hasReconcileSection: true), $"iteration {iteration}: reconcile admitted while unfenced/in flight");
            }

            if (decision.Kind != RepeatDecisionKind.Allowed)
            {
                continue;
            }

            var ambiguous = history.Where(IsAmbiguous).OrderBy(a => a.AttemptNumber).ToList();
            switch (decision.Evidence)
            {
                case SafetyEvidence.NoPriorAttempt:
                    Assert.Empty(history);
                    break;
                case SafetyEvidence.NoSideEffectStarted:
                    Assert.All(history.Where(a => a.Intent == ExecutionIntent.Normal), a => Assert.Equal(SideEffectState.NotStarted, a.SideEffect));
                    break;
                case SafetyEvidence.TargetEnforcedIdempotency:
                    Assert.NotEmpty(ambiguous);
                    Assert.All(ambiguous, a => Assert.True(a.Snapshot is { TargetEnforced: true, OperationIdSent: true }));
                    Assert.True(current.TargetEnforced && current.OperationIdSent);
                    break;
                case SafetyEvidence.AuthoritativeNotFound:
                    var aStar = ambiguous[^1];
                    var horizon = RepeatSafetyEvaluator.FenceTime(aStar);
                    Assert.NotNull(horizon);
                    var lookup = history.Where(a => a.Intent == ExecutionIntent.Reconcile && a.AttemptNumber > aStar.AttemptNumber && a.Outcome == WorkflowOutcome.ReconciledNotFound)
                        .OrderBy(a => a.AttemptNumber).Last().Reconciliation!;
                    Assert.Equal(EvidenceLevel.Authoritative, lookup.Policy.Level);
                    Assert.True(lookup.ObservedAt >= horizon!.Value + TimeSpan.FromSeconds(lookup.Policy.MaxCommitLatencySeconds) + Hist.Policy.ClockSkewAllowance);
                    Assert.True(now - lookup.ObservedAt <= TimeSpan.FromSeconds(lookup.Policy.MaxEvidenceAgeSeconds));
                    Assert.True(lookup.Policy.MatchKey != MatchKeyKind.OperationId || aStar.Snapshot!.OperationIdSent);
                    Assert.DoesNotContain(history, a => a.Intent == ExecutionIntent.Reconcile && a.AttemptNumber > aStar.AttemptNumber && a.Outcome == WorkflowOutcome.ReconciledFound);
                    break;
                default:
                    Assert.Fail($"iteration {iteration}: Allowed with unexpected evidence {decision.Evidence}");
                    break;
            }

            // An open ambiguity (an unresolved checkpointed attempt) is never an ordinary "no side effect" retry.
            if (ambiguous.Count > 0 && !history.Any(a => a.AttemptNumber > ambiguous[^1].AttemptNumber && IsResolving(a)))
            {
                Assert.True(decision.Evidence is SafetyEvidence.TargetEnforcedIdempotency or SafetyEvidence.AuthoritativeNotFound, $"iteration {iteration}: {decision.Evidence}");
            }
        }

        // The generator must actually exercise the interesting decisions.
        foreach (var kind in Enum.GetValues<RepeatDecisionKind>())
        {
            Assert.True(counts.GetValueOrDefault(kind) > 0, $"no history produced {kind}");
        }
    }

    private static bool IsResolving(AttemptSummary a) =>
        a.Intent == ExecutionIntent.Normal
        && (a.Outcome == WorkflowOutcome.Succeeded || (a.SideEffect != SideEffectState.NotStarted && a.Outcome == WorkflowOutcome.Failed && a.Error is { Category: ErrorCategory.Business, IsTransient: false }));

    private static bool IsAmbiguous(AttemptSummary a) => a.Intent == ExecutionIntent.Normal && a.SideEffect != SideEffectState.NotStarted && !IsResolving(a);

    private static List<AttemptSummary> RandomHistory(Random random)
    {
        var history = new List<AttemptSummary>();
        var count = random.Next(0, 6);
        var time = Hist.T0;
        for (var n = 1; n <= count; n++)
        {
            time = time.AddSeconds(random.Next(1, 300));
            var reconcile = random.Next(3) == 0;
            var state = random.Next(10) switch
            {
                0 => AttemptState.Started,
                1 or 2 => AttemptState.Abandoned,
                _ => AttemptState.Completed,
            };

            var outcome = reconcile ? ReconcileOutcomes[random.Next(ReconcileOutcomes.Length)] : NormalOutcomes[random.Next(NormalOutcomes.Length)];
            if (state == AttemptState.Completed && outcome is null)
            {
                outcome = reconcile ? WorkflowOutcome.ReconciledNotFound : WorkflowOutcome.Indeterminate;
            }

            if (state == AttemptState.Started)
            {
                outcome = null;
            }

            var sideEffect = reconcile ? SideEffectState.NotStarted : (SideEffectState)random.Next(3);
            var error = outcome is WorkflowOutcome.Failed or WorkflowOutcome.Cancelled or WorkflowOutcome.Indeterminate or WorkflowOutcome.NeedsReview
                ? new ErrorSummary("E", (ErrorCategory)random.Next(5), random.Next(2) == 0)
                : null;
            var finished = outcome is null ? (DateTimeOffset?)null : time.AddSeconds(random.Next(1, 60));
            var fenced = finished is not null && random.Next(3) != 0;
            DateTimeOffset? fencedAt = random.Next(4) == 0 ? time.AddSeconds(random.Next(1, 600)) : null;
            ReconciliationEvidence? evidence = reconcile && outcome is WorkflowOutcome.ReconciledFound or WorkflowOutcome.ReconciledNotFound
                ? new ReconciliationEvidence(outcome == WorkflowOutcome.ReconciledFound ? ReconciliationFinding.Found : ReconciliationFinding.NotFound,
                    time.AddSeconds(random.Next(0, 120)), random.Next(2) == 0 ? Hist.Authoritative : Hist.Advisory)
                : null;

            history.Add(new AttemptSummary(
                Hist.Op, n, reconcile ? ExecutionIntent.Reconcile : ExecutionIntent.Normal, state, outcome, error, sideEffect,
                Hist.Snap(random.Next(2) == 0, random.Next(2) == 0), time,
                sideEffect == SideEffectState.Started ? time.AddSeconds(1) : null,
                finished, state == AttemptState.Abandoned ? time.AddSeconds(90) : null,
                fenced, fencedAt, fencedAt is null ? null : FenceSource.ProcessDeathConfirmed, evidence));
        }

        return history;
    }
}
