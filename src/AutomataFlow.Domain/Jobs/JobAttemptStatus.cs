namespace AutomataFlow.Domain.Jobs;

public enum JobAttemptStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,

    /// <summary>
    /// The attempt ended after a side-effecting step began, so the target system may or may not have committed.
    /// The next attempt must reconcile before repeating the action.
    /// </summary>
    Indeterminate = 5,

    ReconciledFound = 6,

    ReconciledNotFound = 7,

    NeedsReview = 8,

    /// <summary>The attempt lost its lease without a completion (judged by its checkpoint, ADR-049).</summary>
    Abandoned = 9,
}
