using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Workflows;

/// <summary>
/// A declared record input of a published version, denormalized from the definition for listing and CSV header
/// mapping (docs/ARCHITECTURE.md §7). The definition remains the source of truth.
/// </summary>
public sealed class WorkflowInput : IOrganizationOwned
{
    private WorkflowInput()
    {
    }

    public Guid WorkflowVersionId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The definition's input type name (string, email, integer, decimal, boolean, date, file).</summary>
    public string Type { get; private set; } = string.Empty;

    public bool Required { get; private set; }

    public bool Sensitive { get; private set; }

    public int Position { get; private set; }

    public static WorkflowInput Create(Guid organizationId, Guid workflowVersionId, string name, string type, bool required, bool sensitive, int position)
    {
        if (organizationId == Guid.Empty || workflowVersionId == Guid.Empty)
        {
            throw new ArgumentException("Organization and version are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return new WorkflowInput
        {
            OrganizationId = organizationId,
            WorkflowVersionId = workflowVersionId,
            Name = name,
            Type = type,
            Required = required,
            Sensitive = sensitive,
            Position = position,
        };
    }
}
