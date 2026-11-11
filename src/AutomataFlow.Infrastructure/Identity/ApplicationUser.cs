using Microsoft.AspNetCore.Identity;

namespace AutomataFlow.Infrastructure.Identity;

/// <summary>Platform user (ASP.NET Core Identity). Global, not organization-owned; membership links it to organizations.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
}
