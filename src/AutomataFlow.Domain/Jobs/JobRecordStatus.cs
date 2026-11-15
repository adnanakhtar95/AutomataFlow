namespace AutomataFlow.Domain.Jobs;

public enum JobRecordStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5,

    /// <summary>
    /// The outcome of a side-effecting step could not be determined and reconciliation was inconclusive;
    /// a human must decide (docs/ARCHITECTURE.md section 5.6).
    /// </summary>
    NeedsReview = 6,

    /// <summary>Resolved by a reconcile attempt that found the operation's effect (ReconciledFound).</summary>
    Reconciled = 7,

    /// <summary>The row failed record validation at upload and is never executed.</summary>
    Invalid = 8,

    /// <summary>An earlier attempt is unfenced: nothing may run until a fence is proven or FenceWaitSeconds elapse.</summary>
    AwaitingFence = 9,
}
