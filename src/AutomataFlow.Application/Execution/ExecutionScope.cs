namespace AutomataFlow.Application.Execution;

/// <summary>
/// Identifies what a browser session is executing on behalf of. Every log entry and artifact carries it so that
/// executions are traceable and organization-scoped.
/// </summary>
public sealed record ExecutionScope(Guid OrganizationId, Guid? JobId, Guid? JobRecordId, int AttemptNumber)
{
    public static ExecutionScope ForOrganization(Guid organizationId) => new(organizationId, null, null, 0);
}
