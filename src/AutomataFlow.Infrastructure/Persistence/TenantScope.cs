namespace AutomataFlow.Infrastructure.Persistence;

/// <summary>
/// The organization a unit of work (an HTTP request, or one Worker record attempt) acts for (docs/ARCHITECTURE.md
/// §7.1, layers 3 and 4). It drives the EF query-filter backstop and the <c>SaveChanges</c> write guard. It is
/// <b>not</b> the primary control: application services pass the organization explicitly to every query.
/// It can be set once per scope; switching organizations inside one scope is refused.
/// </summary>
public sealed class TenantScope
{
    public Guid? OrganizationId { get; private set; }

    public void Enter(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (OrganizationId is { } current && current != organizationId)
        {
            throw new TenantIsolationException("The unit of work is already bound to another organization.");
        }

        OrganizationId = organizationId;
    }
}

/// <summary>A tenant-isolation rule was violated (write guard or scope switch). Never caught to continue.</summary>
public sealed class TenantIsolationException : Exception
{
    public TenantIsolationException()
    {
    }

    public TenantIsolationException(string message)
        : base(message)
    {
    }

    public TenantIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
