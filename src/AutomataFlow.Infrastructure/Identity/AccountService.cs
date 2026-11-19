using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AutomataFlow.Infrastructure.Identity;

public sealed class AccountOptions
{
    public const string SectionName = "Auth";

    /// <summary>Self-service registration (creates a user and a new organization). Off unless configured.</summary>
    public bool AllowRegistration { get; set; }

    public int RefreshTokenDays { get; set; } = 14;
}

public enum AccountStatus
{
    Ok,
    InvalidCredentials,
    LockedOut,
    NotAMember,
    InvalidToken,
    Conflict,
    Invalid,
    RegistrationDisabled,
}

/// <summary>An authenticated session: who, which organization, which role, and the plaintext refresh token (returned once).</summary>
public sealed record AccountSession(Guid UserId, Guid OrganizationId, OrganizationRole Role, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>One workspace the user belongs to.</summary>
public sealed record MembershipView(Guid OrganizationId, string OrganizationName, string OrganizationSlug, OrganizationRole Role);

/// <summary>The signed-in user as the dashboard shows them: identity, active workspace and every membership.</summary>
public sealed record UserProfile(Guid UserId, string Email, Guid OrganizationId, string OrganizationName, string OrganizationSlug, OrganizationRole Role,
    bool IsPlatformOperator, IReadOnlyList<MembershipView> Memberships);

public sealed record AccountResult(AccountStatus Status, AccountSession? Session = null, IReadOnlyList<string>? Errors = null)
{
    public static AccountResult Fail(AccountStatus status, params string[] errors) => new(status, null, errors);
}

/// <summary>
/// Sign-up, sign-in, refresh-token rotation and membership checks (docs/ARCHITECTURE.md §7.1 layer 1, §8). Credentials
/// and tokens are never logged; failures are reported with codes only.
/// </summary>
public sealed class AccountService(
    AutomataFlowDbContext db,
    UserManager<ApplicationUser> users,
    TenantScope tenant,
    IOptions<AccountOptions> options,
    TimeProvider clock,
    ILogger<AccountService> logger)
{
    public const string PlatformOperatorRole = "PlatformOperator";

    // A fixed hash verified for unknown e-mail addresses so that both paths cost the same.
    private static readonly string DummyHash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "not-a-real-password-" + Guid.NewGuid());

    public async Task<AccountResult> RegisterAsync(string email, string password, string organizationName, string organizationSlug, CancellationToken cancellationToken)
    {
        if (!options.Value.AllowRegistration)
        {
            return AccountResult.Fail(AccountStatus.RegistrationDisabled);
        }

        var now = clock.GetUtcNow();
        Organization organization;
        try
        {
            organization = Organization.Create(organizationName, organizationSlug, now);
        }
        catch (ArgumentException)
        {
            return AccountResult.Fail(AccountStatus.Invalid, "organization");
        }

        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, CreatedAt = now };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            var codes = created.Errors.Select(e => e.Code).ToArray();
            return AccountResult.Fail(codes.Any(c => c.Contains("Duplicate", StringComparison.Ordinal)) ? AccountStatus.Conflict : AccountStatus.Invalid, codes);
        }

        tenant.Enter(organization.Id);
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(OrganizationMember.Create(organization.Id, user.Id, OrganizationRole.Owner, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountResult.Fail(AccountStatus.Conflict, "organization-slug");
        }

        var session = await IssueAsync(user.Id, organization.Id, OrganizationRole.Owner, Guid.CreateVersion7(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Account registered {UserId} {OrganizationId}", user.Id, organization.Id);
        return new AccountResult(AccountStatus.Ok, session);
    }

    public async Task<AccountResult> LoginAsync(string email, string password, Guid? organizationId, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password);
            return AccountResult.Fail(AccountStatus.InvalidCredentials);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return AccountResult.Fail(AccountStatus.LockedOut);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            logger.LogWarning("Sign-in failed {UserId}", user.Id);
            return AccountResult.Fail(await users.IsLockedOutAsync(user) ? AccountStatus.LockedOut : AccountStatus.InvalidCredentials);
        }

        await users.ResetAccessFailedCountAsync(user);
        var memberships = await MembershipsAsync(user.Id, cancellationToken);
        var membership = organizationId is { } requested ? memberships.FirstOrDefault(m => m.OrganizationId == requested) : memberships.FirstOrDefault();
        if (membership is null)
        {
            return AccountResult.Fail(AccountStatus.NotAMember);
        }

        var session = await IssueAsync(user.Id, membership.OrganizationId, membership.Role, Guid.CreateVersion7(), cancellationToken);
        return new AccountResult(AccountStatus.Ok, session);
    }

    /// <summary>Rotates a refresh token. Presenting a rotated or revoked token revokes its whole family.</summary>
    public async Task<AccountResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hash = RefreshToken.Hash(refreshToken);
        var stored = await db.Set<RefreshToken>().AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return AccountResult.Fail(AccountStatus.InvalidToken);
        }

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            logger.LogWarning("Refresh token reuse detected; family revoked {UserId} {FamilyId}", stored.UserId, stored.FamilyId);
            return AccountResult.Fail(AccountStatus.InvalidToken);
        }

        if (stored.ExpiresAt <= now)
        {
            return AccountResult.Fail(AccountStatus.InvalidToken);
        }

        var user = await users.FindByIdAsync(stored.UserId.ToString());
        var role = await FindRoleAsync(stored.UserId, stored.ActiveOrganizationId, cancellationToken);
        if (user is null || await users.IsLockedOutAsync(user) || role is null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return AccountResult.Fail(role is null ? AccountStatus.NotAMember : AccountStatus.InvalidToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await IssueAsync(stored.UserId, stored.ActiveOrganizationId, role.Value, stored.FamilyId, cancellationToken);
        var replacement = await db.Set<RefreshToken>().Where(t => t.TokenHash == RefreshToken.Hash(session.RefreshToken)).Select(t => t.Id).SingleAsync(cancellationToken);

        // Conditional: exactly one concurrent refresh can rotate a token; the loser is treated as reuse.
        var rotated = await db.Set<RefreshToken>()
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, replacement), cancellationToken);
        if (rotated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return AccountResult.Fail(AccountStatus.InvalidToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AccountResult(AccountStatus.Ok, session);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = RefreshToken.Hash(refreshToken);
        var family = await db.Set<RefreshToken>().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (family is { } id)
        {
            await RevokeFamilyAsync(id, clock.GetUtcNow(), cancellationToken);
        }
    }

    /// <summary>The caller's role in <paramref name="organizationId"/>, or null when not (or no longer) a member.</summary>
    public async Task<OrganizationRole?> FindRoleAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken) =>
        (await MembershipsAsync(userId, cancellationToken)).FirstOrDefault(m => m.OrganizationId == organizationId)?.Role;

    /// <summary>
    /// The caller's profile in the active organization, or null when the membership no longer exists. Lists only the
    /// caller's own memberships (the reviewed cross-organization read below, constrained to the user id).
    /// </summary>
    public async Task<UserProfile?> ProfileAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        var memberships = await MembershipsAsync(userId, cancellationToken);
        var ids = memberships.Select(m => m.OrganizationId).ToList();
        var organizations = await db.Organizations.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var views = memberships.Where(m => organizations.ContainsKey(m.OrganizationId))
            .Select(m => new MembershipView(m.OrganizationId, organizations[m.OrganizationId].Name, organizations[m.OrganizationId].Slug, m.Role))
            .ToList();
        var active = views.FirstOrDefault(v => v.OrganizationId == organizationId);
        if (active is null)
        {
            return null;
        }

        return new UserProfile(userId, user.Email ?? string.Empty, active.OrganizationId, active.OrganizationName, active.OrganizationSlug, active.Role,
            await users.IsInRoleAsync(user, PlatformOperatorRole), views);
    }

    public async Task<bool> IsPlatformOperatorAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is not null && await users.IsInRoleAsync(user, PlatformOperatorRole);
    }

    // Reviewed cross-organization read (§7.1 layer 3): sign-in must list the caller's own memberships in every
    // organization. Always constrained to the authenticated user's id.
    private async Task<List<OrganizationMember>> MembershipsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.OrganizationMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

    private async Task<AccountSession> IssueAsync(Guid userId, Guid organizationId, OrganizationRole role, Guid family, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var value = RefreshToken.NewTokenValue();
        var token = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ActiveOrganizationId = organizationId,
            FamilyId = family,
            TokenHash = RefreshToken.Hash(value),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        };
        db.Set<RefreshToken>().Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new AccountSession(userId, organizationId, role, value, token.ExpiresAt);
    }

    private Task<int> RevokeFamilyAsync(Guid family, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<RefreshToken>().Where(t => t.FamilyId == family && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
}
