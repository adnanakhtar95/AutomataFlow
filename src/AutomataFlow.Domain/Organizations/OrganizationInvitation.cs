using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Organizations;

/// <summary>
/// An invitation to join a workspace (Phase 8). The token itself is shown once to the inviting admin and stored only as a
/// SHA-256 hash. An invitation is single-use, expires, can be revoked, and is bound to one e-mail address: it can only
/// be accepted by an account with that address.
/// </summary>
public sealed class OrganizationInvitation : IOrganizationOwned
{
    public const int EmailMaxLength = 254;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private OrganizationInvitation()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Normalized (trimmed, lower-case).</summary>
    public string Email { get; private set; } = string.Empty;

    public OrganizationRole Role { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static OrganizationInvitation Create(Guid organizationId, string email, OrganizationRole role, string tokenHash, Guid? createdBy, DateTimeOffset now, TimeSpan lifetime)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id is required.", nameof(organizationId));
        }

        var normalized = NormalizeEmail(email);
        if (normalized.Length is 0 or > EmailMaxLength || !normalized.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid e-mail address is required.", nameof(email));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "An invitation lives between 1 second and 30 days.");
        }

        return new OrganizationInvitation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Email = normalized,
            Role = role,
            TokenHash = tokenHash,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    public InvitationState StateAt(DateTimeOffset now) =>
        RevokedAt is not null ? InvitationState.Revoked
        : AcceptedAt is not null ? InvitationState.Accepted
        : now >= ExpiresAt ? InvitationState.Expired
        : InvitationState.Open;

    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (StateAt(now) != InvitationState.Open)
        {
            throw new InvalidOperationException("Only an open invitation can be accepted.");
        }

        AcceptedAt = now;
        AcceptedBy = userId;
    }

    public void Revoke(DateTimeOffset now)
    {
        if (StateAt(now) == InvitationState.Open)
        {
            RevokedAt = now;
        }
    }
}

public enum InvitationState
{
    Open,
    Accepted,
    Expired,
    Revoked,
}
