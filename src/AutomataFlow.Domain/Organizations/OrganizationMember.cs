using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Organizations;

/// <summary>
/// Links a user (identified by id only; the identity record lives in Infrastructure) to an organization.
/// </summary>
public sealed class OrganizationMember : IOrganizationOwned
{
    private OrganizationMember()
    {
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public OrganizationRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static OrganizationMember Create(Guid organizationId, Guid userId, OrganizationRole role, DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id is required.", nameof(organizationId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role.");
        }

        return new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = role,
            CreatedAt = createdAt,
        };
    }

    public void ChangeRole(OrganizationRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role.");
        }

        Role = role;
    }
}
