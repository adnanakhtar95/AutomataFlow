using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AutomataFlow.Api.Auth;

/// <summary>
/// The verified caller of one request (docs/ARCHITECTURE.md §7.1 layer 1). Populated only by
/// <see cref="MembershipVerificationMiddleware"/> after the membership was read from the database; the token's claims
/// alone are never trusted for the organization or the role.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private Guid _userId;
    private Guid _organizationId;
    private OrganizationRole _role;

    public bool IsAuthenticated { get; private set; }

    public Guid UserId => IsAuthenticated ? _userId : throw new InvalidOperationException("No authenticated caller.");

    public Guid OrganizationId => IsAuthenticated ? _organizationId : throw new InvalidOperationException("No authenticated caller.");

    public OrganizationRole Role => IsAuthenticated ? _role : throw new InvalidOperationException("No authenticated caller.");

    internal void Set(Guid userId, Guid organizationId, OrganizationRole role)
    {
        _userId = userId;
        _organizationId = organizationId;
        _role = role;
        IsAuthenticated = true;
    }
}

/// <summary>
/// After JWT validation: reads the caller's membership in the token's organization from the database, then binds the
/// request's <see cref="TenantScope"/> to that organization. A removed membership is effective immediately (403),
/// even while the access token is still valid.
/// </summary>
public sealed class MembershipVerificationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser current, TenantScope tenant, AccountService accounts)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
                || !Guid.TryParse(principal.FindFirst(PlatformClaims.Organization)?.Value, out var organizationId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var access = await accounts.AccessAsync(userId, organizationId, context.RequestAborted);
            if (access.Role is not { } role)
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not a member of this organization.").ExecuteAsync(context);
                return;
            }

            // A suspended workspace (Phase 8) can do nothing until a platform operator reactivates it; its data is kept.
            if (access.Suspended)
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The workspace is suspended.").ExecuteAsync(context);
                return;
            }

            current.Set(userId, organizationId, role);
            tenant.Enter(organizationId);
        }

        await next(context);
    }
}

public static class Policies
{
    public const string Viewer = "role:viewer";
    public const string Operator = "role:operator";
    public const string Admin = "role:admin";
    public const string Owner = "role:owner";
    public const string PlatformOperator = "platform-operator";
}

/// <summary>Requires at least <see cref="Minimum"/> in the verified membership.</summary>
public sealed record MinimumRoleRequirement(OrganizationRole Minimum) : IAuthorizationRequirement;

public sealed class MinimumRoleHandler(CurrentUser current) : AuthorizationHandler<MinimumRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumRoleRequirement requirement)
    {
        if (current.IsAuthenticated && current.Role >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Platform operators are infrastructure staff, never a tenant role (§7.5.6); checked in the database per request.</summary>
public sealed record PlatformOperatorRequirement : IAuthorizationRequirement;

public sealed class PlatformOperatorHandler(CurrentUser current, AccountService accounts) : AuthorizationHandler<PlatformOperatorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformOperatorRequirement requirement)
    {
        if (current.IsAuthenticated && await accounts.IsPlatformOperatorAsync(current.UserId))
        {
            context.Succeed(requirement);
        }
    }
}
