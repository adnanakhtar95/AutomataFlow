using System.Security.Cryptography;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutomataFlow.Infrastructure.Organizations;

public sealed record WorkspaceView(Guid Id, string Name, string Slug, OrganizationStatus Status, DateTimeOffset CreatedAt, int MemberCount);

public sealed record MemberView(Guid UserId, string Email, OrganizationRole Role, DateTimeOffset JoinedAt);

public sealed record InvitationView(Guid Id, string Email, OrganizationRole Role, InvitationState State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <param name="Token">Shown ONCE: deliver it to the invitee (the platform sends no e-mail). Only its hash is stored.</param>
public sealed record InvitationCreated(Guid Id, string Email, OrganizationRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record UsageView(DateTimeOffset From, DateTimeOffset To, IReadOnlyDictionary<string, long> Totals);

/// <summary>
/// Workspace administration for its own members (Phase 8). The workspace is always the caller's verified one
/// (<see cref="ICurrentUser"/>); a foreign id is "not found". Role rules: Admins manage Viewers, Operators and Admins;
/// only an Owner may grant, change or remove the Owner role; the last Owner can never be removed or demoted.
/// </summary>
public sealed class WorkspaceService(AutomataFlowDbContext db, ICurrentUser user, TimeProvider clock, ILogger<WorkspaceService> logger)
{
    public const int MaxUsageDays = 366;

    public async Task<WorkspaceView> GetAsync(CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var o = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == org, cancellationToken);
        var members = await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org, cancellationToken);
        return new WorkspaceView(o.Id, o.Name, o.Slug, o.Status, o.CreatedAt, members);
    }

    public async Task<ServiceResult<WorkspaceView>> RenameAsync(string name, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var o = await db.Organizations.SingleAsync(x => x.Id == org, cancellationToken);
        try
        {
            o.Rename(name);
        }
        catch (ArgumentException)
        {
            return ServiceResult<WorkspaceView>.Fail(ServiceStatus.Invalid, new ServiceError("name", "invalid", "The workspace name is invalid."));
        }

        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<WorkspaceView>.Ok(await GetAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<MemberView>> MembersAsync(CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var rows = await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == org)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Email, m.Role, m.CreatedAt })
            .OrderBy(r => r.Email)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new MemberView(r.UserId, r.Email ?? string.Empty, r.Role, r.CreatedAt))];
    }

    public async Task<ServiceResult<MemberView>> ChangeRoleAsync(Guid memberId, OrganizationRole role, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role))
        {
            return ServiceResult<MemberView>.Fail(ServiceStatus.Invalid, new ServiceError("role", "invalid", "Unknown role."));
        }

        var org = user.OrganizationId;
        var member = await db.OrganizationMembers.SingleOrDefaultAsync(m => m.OrganizationId == org && m.UserId == memberId, cancellationToken);
        if (member is null)
        {
            return ServiceResult<MemberView>.NotFound();
        }

        if ((member.Role == OrganizationRole.Owner || role == OrganizationRole.Owner) && user.Role != OrganizationRole.Owner)
        {
            return ServiceResult<MemberView>.Fail(ServiceStatus.Forbidden, new ServiceError("role", "owner-only", "Only an Owner can grant or change the Owner role."));
        }

        if (member.Role == OrganizationRole.Owner && role != OrganizationRole.Owner && await IsLastOwnerAsync(org, cancellationToken))
        {
            return ServiceResult<MemberView>.Fail(ServiceStatus.Conflict, new ServiceError("role", "last-owner", "The workspace needs at least one Owner."));
        }

        member.ChangeRole(role);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Member role changed {OrganizationId} {UserId} {Role} {ChangedBy}", org, memberId, role, user.UserId);
        return ServiceResult<MemberView>.Ok((await MembersAsync(cancellationToken)).Single(m => m.UserId == memberId));
    }

    /// <summary>Removes a member (or the caller leaving). Their sessions in this workspace end immediately.</summary>
    public async Task<ServiceResult<bool>> RemoveAsync(Guid memberId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var member = await db.OrganizationMembers.SingleOrDefaultAsync(m => m.OrganizationId == org && m.UserId == memberId, cancellationToken);
        if (member is null)
        {
            return ServiceResult<bool>.NotFound();
        }

        if (member.Role == OrganizationRole.Owner && user.Role != OrganizationRole.Owner)
        {
            return ServiceResult<bool>.Fail(ServiceStatus.Forbidden, new ServiceError(null, "owner-only", "Only an Owner can remove an Owner."));
        }

        if (member.Role == OrganizationRole.Owner && await IsLastOwnerAsync(org, cancellationToken))
        {
            return ServiceResult<bool>.Fail(ServiceStatus.Conflict, new ServiceError(null, "last-owner", "The workspace needs at least one Owner."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.OrganizationMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await db.Set<RefreshToken>().Where(t => t.UserId == memberId && t.ActiveOrganizationId == org && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Member removed {OrganizationId} {UserId} {RemovedBy}", org, memberId, user.UserId);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<IReadOnlyList<InvitationView>> InvitationsAsync(CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var now = clock.GetUtcNow();
        var rows = await db.OrganizationInvitations.AsNoTracking().Where(i => i.OrganizationId == org)
            .OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync(cancellationToken);
        return [.. rows.Select(i => new InvitationView(i.Id, i.Email, i.Role, i.StateAt(now), i.CreatedAt, i.ExpiresAt))];
    }

    /// <summary>Invites an e-mail address. A newer invitation for the same address replaces any open one.</summary>
    public async Task<ServiceResult<InvitationCreated>> InviteAsync(string email, OrganizationRole role, CancellationToken cancellationToken)
    {
        if (role == OrganizationRole.Owner && user.Role != OrganizationRole.Owner)
        {
            return ServiceResult<InvitationCreated>.Fail(ServiceStatus.Forbidden, new ServiceError("role", "owner-only", "Only an Owner can invite an Owner."));
        }

        var org = user.OrganizationId;
        var normalized = OrganizationInvitation.NormalizeEmail(email);
        var identityEmail = normalized.ToUpperInvariant(); // Identity's NormalizedEmail
        var isMember = await db.OrganizationMembers.Where(m => m.OrganizationId == org)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.NormalizedEmail)
            .AnyAsync(e => e == identityEmail, cancellationToken);
        if (isMember)
        {
            return ServiceResult<InvitationCreated>.Fail(ServiceStatus.Conflict, new ServiceError("email", "already-member", "This person is already a member."));
        }

        return await CreateInvitationAsync(db, org, normalized, role, user.UserId, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<ServiceResult<bool>> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(i => i.OrganizationId == org && i.Id == invitationId, cancellationToken);
        if (invitation is null)
        {
            return ServiceResult<bool>.NotFound();
        }

        invitation.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Usage totals by metric (records imported, attempts started) over the last <paramref name="days"/> days.</summary>
    public async Task<UsageView> UsageAsync(int days, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var to = clock.GetUtcNow();
        var from = to.AddDays(-Math.Clamp(days, 1, MaxUsageDays));
        var totals = await db.UsageRecords.AsNoTracking().Where(u => u.OrganizationId == org && u.RecordedAt >= from)
            .GroupBy(u => u.Metric).Select(g => new { g.Key, Total = g.Sum(u => (long)u.Quantity) }).ToListAsync(cancellationToken);
        return new UsageView(from, to, totals.ToDictionary(t => t.Key, t => t.Total, StringComparer.Ordinal));
    }

    /// <summary>Shared by workspace admins and platform operators (who create a workspace with its first Owner invitation).</summary>
    internal static async Task<ServiceResult<InvitationCreated>> CreateInvitationAsync(AutomataFlowDbContext db, Guid org, string email, OrganizationRole role, Guid? createdBy,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        OrganizationInvitation invitation;
        try
        {
            invitation = OrganizationInvitation.Create(org, email, role, InvitationTokens.Hash(token), createdBy, now, OrganizationInvitation.DefaultLifetime);
        }
        catch (ArgumentException)
        {
            return ServiceResult<InvitationCreated>.Fail(ServiceStatus.Invalid, new ServiceError("email", "invalid", "A valid e-mail address is required."));
        }

        var open = await db.OrganizationInvitations.Where(i => i.OrganizationId == org && i.Email == invitation.Email && i.AcceptedAt == null && i.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var previous in open)
        {
            previous.Revoke(now);
        }

        db.OrganizationInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<InvitationCreated>.Ok(new InvitationCreated(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, token));
    }

    private async Task<bool> IsLastOwnerAsync(Guid org, CancellationToken cancellationToken) =>
        await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org && m.Role == OrganizationRole.Owner, cancellationToken) <= 1;
}

/// <summary>Invitation tokens are stored only as SHA-256 hashes (like refresh tokens).</summary>
public static class InvitationTokens
{
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty)));
}
