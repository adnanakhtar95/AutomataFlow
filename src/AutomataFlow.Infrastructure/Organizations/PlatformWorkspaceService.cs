using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AutomataFlow.Infrastructure.Organizations;

public sealed record PlatformWorkspaceView(Guid Id, string Name, string Slug, OrganizationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? SuspendedAt,
    int MemberCount, int ActiveJobs, int TotalJobs);

public sealed record PlatformWorkspaceCreated(PlatformWorkspaceView Workspace, InvitationCreated OwnerInvitation);

/// <summary>
/// Platform-operator workspace administration (Phase 8): list, create with an Owner invitation, suspend, reactivate.
/// Reviewed cross-organization access by design (§7.1 layer 3, §7.5.6): restricted to the platform-operator policy, and
/// it returns workspace identity and counts only, never customer data. Writes into a workspace run in a new scope whose
/// tenant is that workspace (a request scope can never switch organizations).
/// </summary>
public sealed class PlatformWorkspaceService(AutomataFlowDbContext db, ICurrentUser user, IServiceScopeFactory scopes, TimeProvider clock, ILogger<PlatformWorkspaceService> logger)
{
    public async Task<IReadOnlyList<PlatformWorkspaceView>> ListAsync(CancellationToken cancellationToken)
    {
        var organizations = await db.Organizations.AsNoTracking().OrderBy(o => o.Name).ToListAsync(cancellationToken);
        var members = await db.OrganizationMembers.IgnoreQueryFilters().AsNoTracking().GroupBy(m => m.OrganizationId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var jobs = await db.Jobs.IgnoreQueryFilters().AsNoTracking().GroupBy(j => j.OrganizationId)
            .Select(g => new { g.Key, Total = g.Count(), Active = g.Count(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running) })
            .ToDictionaryAsync(g => g.Key, cancellationToken);
        return [.. organizations.Select(o => new PlatformWorkspaceView(o.Id, o.Name, o.Slug, o.Status, o.CreatedAt, o.SuspendedAt,
            members.GetValueOrDefault(o.Id), jobs.TryGetValue(o.Id, out var j) ? j.Active : 0, j?.Total ?? 0))];
    }

    /// <summary>Creates a workspace and an Owner invitation for <paramref name="ownerEmail"/> (the token is returned once).</summary>
    public async Task<ServiceResult<PlatformWorkspaceCreated>> CreateAsync(string name, string slug, string ownerEmail, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        Organization organization;
        try
        {
            organization = Organization.Create(name, slug, now);
        }
        catch (ArgumentException)
        {
            return ServiceResult<PlatformWorkspaceCreated>.Fail(ServiceStatus.Invalid, new ServiceError("slug", "invalid", "Name or slug is invalid (slug: lowercase letters, digits and single hyphens)."));
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(organization.Id);
        var workspaceDb = scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>();
        await using var transaction = await workspaceDb.Database.BeginTransactionAsync(cancellationToken);
        workspaceDb.Organizations.Add(organization);
        try
        {
            await workspaceDb.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ServiceResult<PlatformWorkspaceCreated>.Fail(ServiceStatus.Conflict, new ServiceError("slug", "duplicate", "A workspace with this slug already exists."));
        }

        var invitation = await WorkspaceService.CreateInvitationAsync(workspaceDb, organization.Id, ownerEmail, OrganizationRole.Owner, user.UserId, now, cancellationToken);
        if (invitation.Status != ServiceStatus.Ok)
        {
            return ServiceResult<PlatformWorkspaceCreated>.Fail(invitation.Status, [.. invitation.Errors]);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Workspace created {OrganizationId} {Slug} {CreatedBy}", organization.Id, organization.Slug, user.UserId);
        var view = (await ListAsync(cancellationToken)).Single(w => w.Id == organization.Id);
        return ServiceResult<PlatformWorkspaceCreated>.Ok(new PlatformWorkspaceCreated(view, invitation.Value!));
    }

    public Task<ServiceResult<PlatformWorkspaceView>> SuspendAsync(Guid organizationId, CancellationToken cancellationToken) =>
        ChangeAsync(organizationId, o => o.Suspend(clock.GetUtcNow()), "suspended", cancellationToken);

    public Task<ServiceResult<PlatformWorkspaceView>> ReactivateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        ChangeAsync(organizationId, o => o.Reactivate(), "reactivated", cancellationToken);

    private async Task<ServiceResult<PlatformWorkspaceView>> ChangeAsync(Guid organizationId, Action<Organization> change, string verb, CancellationToken cancellationToken)
    {
        if (organizationId == user.OrganizationId)
        {
            return ServiceResult<PlatformWorkspaceView>.Fail(ServiceStatus.Conflict, new ServiceError(null, "own-workspace", "You cannot change the status of the workspace you are signed in to."));
        }

        var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            return ServiceResult<PlatformWorkspaceView>.NotFound();
        }

        change(organization);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Workspace {Change} {OrganizationId} {ChangedBy}", verb, organizationId, user.UserId);
        return ServiceResult<PlatformWorkspaceView>.Ok((await ListAsync(cancellationToken)).Single(w => w.Id == organizationId));
    }
}
