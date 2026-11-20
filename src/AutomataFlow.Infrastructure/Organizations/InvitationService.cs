using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutomataFlow.Infrastructure.Organizations;

/// <param name="AccountExists">Whether an account with the invited address exists (accept with its password) or will be created.</param>
public sealed record InvitationPreview(string OrganizationName, string Email, OrganizationRole Role, DateTimeOffset ExpiresAt, bool AccountExists);

public enum InvitationOutcome
{
    Accepted,
    NotFound,
    WrongPassword,
    InvalidPassword,
    LockedOut,
    Suspended,
    AlreadyMember,
}

public sealed record InvitationAcceptance(InvitationOutcome Outcome, AccountSession? Session = null, IReadOnlyList<string>? Errors = null);

/// <summary>
/// Invitation preview and acceptance (Phase 8), BEFORE the invitee is signed in. Reviewed cross-organization access
/// (§7.1 layer 3): every read is constrained by the SHA-256 hash of a 256-bit token the invitee holds, and the
/// membership is written inside the invitation's own organization scope. Unknown, expired, revoked and used tokens
/// are indistinguishable (NotFound). The invitation is bound to its e-mail address: an existing account must prove
/// its password, a new account is created with that address.
/// </summary>
public sealed class InvitationService(AutomataFlowDbContext db, UserManager<ApplicationUser> users, TenantScope tenant, AccountService accounts, TimeProvider clock,
    ILogger<InvitationService> logger)
{
    public async Task<InvitationPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var invitation = await FindOpenAsync(token, now, cancellationToken);
        if (invitation is null)
        {
            return null;
        }

        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == invitation.OrganizationId, cancellationToken);
        return new InvitationPreview(organization.Name, invitation.Email, invitation.Role, invitation.ExpiresAt, await users.FindByEmailAsync(invitation.Email) is not null);
    }

    public async Task<InvitationAcceptance> AcceptAsync(string token, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var invitation = await FindOpenAsync(token, now, cancellationToken);
        if (invitation is null)
        {
            return new InvitationAcceptance(InvitationOutcome.NotFound);
        }

        if (await db.Organizations.AsNoTracking().AnyAsync(o => o.Id == invitation.OrganizationId && o.Status == OrganizationStatus.Suspended, cancellationToken))
        {
            return new InvitationAcceptance(InvitationOutcome.Suspended);
        }

        var user = await users.FindByEmailAsync(invitation.Email);
        if (user is not null)
        {
            if (await users.IsLockedOutAsync(user))
            {
                return new InvitationAcceptance(InvitationOutcome.LockedOut);
            }

            if (!await users.CheckPasswordAsync(user, password))
            {
                await users.AccessFailedAsync(user);
                return new InvitationAcceptance(await users.IsLockedOutAsync(user) ? InvitationOutcome.LockedOut : InvitationOutcome.WrongPassword);
            }

            await users.ResetAccessFailedCountAsync(user);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (user is null)
        {
            user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = invitation.Email, Email = invitation.Email, CreatedAt = now };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                return new InvitationAcceptance(InvitationOutcome.InvalidPassword, Errors: [.. created.Errors.Select(e => e.Code)]);
            }
        }

        tenant.Enter(invitation.OrganizationId);
        if (await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == user.Id, cancellationToken))
        {
            return new InvitationAcceptance(InvitationOutcome.AlreadyMember);
        }

        // Single use, even under concurrent acceptance: only one conditional update can win.
        var won = await db.OrganizationInvitations
            .Where(i => i.Id == invitation.Id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedBy, user.Id), cancellationToken);
        if (won != 1)
        {
            return new InvitationAcceptance(InvitationOutcome.NotFound);
        }

        db.OrganizationMembers.Add(OrganizationMember.Create(invitation.OrganizationId, user.Id, invitation.Role, now));
        await db.SaveChangesAsync(cancellationToken);
        var session = await accounts.StartSessionAsync(user.Id, invitation.OrganizationId, invitation.Role, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Invitation accepted {OrganizationId} {UserId} {Role}", invitation.OrganizationId, user.Id, invitation.Role);
        return new InvitationAcceptance(InvitationOutcome.Accepted, session);
    }

    private async Task<OrganizationInvitation?> FindOpenAsync(string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = InvitationTokens.Hash(token);
        var invitation = await db.OrganizationInvitations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        return invitation?.StateAt(now) == InvitationState.Open ? invitation : null;
    }
}
