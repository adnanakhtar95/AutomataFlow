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
}
