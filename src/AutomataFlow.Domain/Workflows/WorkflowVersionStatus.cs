namespace AutomataFlow.Domain.Workflows;

public enum WorkflowVersionStatus
{
    Draft = 0,
    /// <summary>Immutable once published; jobs pin a published version.</summary>
    Published = 1,
    Archived = 2,
}
