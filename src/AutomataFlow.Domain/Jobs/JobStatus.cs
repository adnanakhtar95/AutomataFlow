namespace AutomataFlow.Domain.Jobs;

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Failed = 4,
    Cancelled = 5,
}
