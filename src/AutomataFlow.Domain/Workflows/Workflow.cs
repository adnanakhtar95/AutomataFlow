using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Workflows;

/// <summary>A logical workflow; its content lives in immutable <see cref="WorkflowVersion"/>s (docs/ARCHITECTURE.md §7).</summary>
public sealed class Workflow : IOrganizationOwned
{
    public const int NameMaxLength = 64;
    public const int DescriptionMaxLength = 500;

    private Workflow()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>The highest version number created so far (0 before the first version).</summary>
    public int LatestVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Workflow Create(Guid organizationId, string name, string? description, DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"A workflow name cannot exceed {NameMaxLength} characters.", nameof(name));
        }

        if (description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"A description cannot exceed {DescriptionMaxLength} characters.", nameof(description));
        }

        return new Workflow
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Name = trimmed,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = createdAt,
        };
    }

    /// <summary>Reserves the next version number; the caller creates the version in the same transaction.</summary>
    public int NextVersionNumber() => ++LatestVersion;
}
