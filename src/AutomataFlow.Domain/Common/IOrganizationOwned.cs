namespace AutomataFlow.Domain.Common;

/// <summary>
/// Marks an entity as customer-owned data that belongs to exactly one organization.
/// Every such entity must be accessed through organization-scoped application services;
/// EF Core query filters are only a backstop (see docs/ARCHITECTURE.md section 7.1).
/// </summary>
public interface IOrganizationOwned
{
    Guid OrganizationId { get; }
}
