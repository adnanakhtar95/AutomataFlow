using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Domain.Jobs;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>Scheduling policy of one job (docs/ARCHITECTURE.md §5.4; ADR-056 for the fence wait).</summary>
public sealed record SchedulerPolicy(int MaxAttempts, TimeSpan RetryBackoff, TimeSpan FenceWait, TimeSpan FenceRecheck, TimeSpan InFlightRetry, TimeSpan UnavailableRetry)
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Exponential backoff after the n-th Normal attempt (1-based), capped at one hour.</summary>
    public TimeSpan Backoff(int normalAttempts) =>
        normalAttempts <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, RetryBackoff.Ticks * (1L << Math.Min(normalAttempts - 1, 20))));
}

/// <summary>The next state of a record after an attempt (or a refusal). Codes only, never values.</summary>
public sealed record RecordTransition(JobRecordStatus Status, RecordIntent NextIntent, DateTimeOffset? NextAttemptAt, DateTimeOffset? AwaitingFenceSince, string? ErrorCode, bool StoreResults)
{
    public bool IsTerminal => JobRecord.IsTerminal(Status);
}

/// <summary>
/// Maps an attempt result and the <see cref="RepeatSafetyEvaluator"/>'s decision over the journal's fresh history to the
/// record's next state (Phase 5 step 6). Pure and deterministic. It never authorizes anything itself: whatever it schedules,
/// the executor re-evaluates the journal history at admission and refuses an unauthorized repeat (ADR-045). Its own
/// safety rules:
/// <list type="bullet">
/// <item>attempts exhausted with an open ambiguity → <c>NeedsReview</c>, never <c>Failed</c>;</item>
/// <item>a cancelled job never hides an ambiguity: anything but a clean "no side effect" becomes <c>NeedsReview</c>;</item>
/// <item><c>AwaitingFence</c> is re-checked and becomes <c>NeedsReview</c> after <see cref="SchedulerPolicy.FenceWait"/>;</item>
/// <item>an unreadable history never schedules a Normal repeat after a side effect: the record waits and is re-evaluated.</item>
/// </list>
/// </summary>
public static class RecordScheduler
{
    private static readonly HashSet<string> PermanentRefusals = new(StringComparer.Ordinal)
    {
        EngineErrorCodes.WorkflowTampered,
        EngineErrorCodes.RecordInvalid,
        EngineErrorCodes.PolicyViolation,
        EngineErrorCodes.WorkflowTimeoutExceedsServerLimit,
        ExecutionErrorCodes.ReconcileNotAvailable,
    };

    /// <param name="result">The attempt result, or null when no attempt was made (cancelled job, or a fence re-check).</param>
    /// <param name="decision">The evaluator's decision over the journal history read AFTER the attempt; null if unreadable.</param>
    /// <param name="normalAttempts">Normal attempts in the journal history (reconcile attempts do not count; A4 bounds them).</param>
    public static RecordTransition Next(
        WorkflowExecutionResult? result,
        RepeatDecision? decision,
        int normalAttempts,
        RecordIntent currentIntent,
        SchedulerPolicy policy,
        bool cancelRequested,
        DateTimeOffset? awaitingFenceSince,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (result is { Outcome: WorkflowOutcome.Refused, Error: { } refusal })
        {
            if (PermanentRefusals.Contains(refusal.Code))
            {
                return Terminal(JobRecordStatus.Failed, refusal.Code, currentIntent);
            }

            if (refusal.Code == ExecutionErrorCodes.JournalUnavailable)
            {
                return Retry(currentIntent, now + policy.UnavailableRetry, refusal.Code);
            }
        }

        decision ??= result?.Refusal;
        if (decision is null)
        {
            // History unreadable: wait and re-evaluate; never decide a repeat blind.
            return Retry(currentIntent, now + policy.UnavailableRetry, ExecutionErrorCodes.JournalUnavailable);
        }

        var lastCode = result?.Error?.Code;
        switch (decision.Kind)
        {
            case RepeatDecisionKind.NotNeeded:
                return decision.Resolution switch
                {
                    Resolution.Succeeded => new RecordTransition(JobRecordStatus.Succeeded, currentIntent, null, null, null, StoreResults: true),
                    Resolution.ReconciledFound => new RecordTransition(JobRecordStatus.Reconciled, currentIntent, null, null, null, StoreResults: true),
                    _ => Terminal(JobRecordStatus.Failed, lastCode ?? decision.Resolution?.ToString() ?? "Failed", currentIntent, storeResults: true),
                };

            case RepeatDecisionKind.Allowed:
                var clean = decision.Evidence is SafetyEvidence.NoPriorAttempt or SafetyEvidence.NoSideEffectStarted;
                if (cancelRequested)
                {
                    return clean ? Terminal(JobRecordStatus.Cancelled, "JobCancelled", currentIntent) : Review("CancelledWithOpenAmbiguity", currentIntent);
                }

                if (normalAttempts >= policy.MaxAttempts)
                {
                    return clean ? Terminal(JobRecordStatus.Failed, lastCode ?? "MaxAttemptsExceeded", currentIntent) : Review("AttemptsExhaustedWithOpenAmbiguity", currentIntent);
                }

                return Retry(RecordIntent.Normal, now + policy.Backoff(normalAttempts), lastCode);

            case RepeatDecisionKind.ReconcileRequired:
                return cancelRequested ? Review("CancelledWithOpenAmbiguity", currentIntent) : Retry(RecordIntent.Reconcile, now, lastCode);

            case RepeatDecisionKind.ReconcileTooEarly:
                return cancelRequested ? Review("CancelledWithOpenAmbiguity", currentIntent)
                    : Retry(RecordIntent.Reconcile, decision.RetryAfter is { } after && after > now ? after : now + policy.FenceRecheck, lastCode);

            case RepeatDecisionKind.InFlight:
                return Retry(currentIntent, now + policy.InFlightRetry, ExecutionErrorCodes.AttemptInFlight);

            case RepeatDecisionKind.AwaitingFence:
                if (cancelRequested)
                {
                    return Review("CancelledWithOpenAmbiguity", currentIntent);
                }

                var since = awaitingFenceSince ?? now;
                return now - since >= policy.FenceWait
                    ? Review("FenceNotConfirmed", currentIntent)
                    : new RecordTransition(JobRecordStatus.AwaitingFence, currentIntent, now + policy.FenceRecheck, since, ExecutionErrorCodes.AwaitingFence, StoreResults: false);

            default:
                return Review(decision.Reason.ToString(), currentIntent);
        }
    }

    private static RecordTransition Retry(RecordIntent intent, DateTimeOffset at, string? code) => new(JobRecordStatus.Pending, intent, at, null, code, StoreResults: false);

    private static RecordTransition Terminal(JobRecordStatus status, string? code, RecordIntent intent, bool storeResults = false) => new(status, intent, null, null, code, storeResults);

    private static RecordTransition Review(string reason, RecordIntent intent) => new(JobRecordStatus.NeedsReview, intent, null, null, reason, StoreResults: false);
}
