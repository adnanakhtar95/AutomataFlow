using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutomataFlow.Api.Auth;

/// <summary>JWT settings (docs/ARCHITECTURE.md §8). The signing key comes only from configuration (environment / user-secrets).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    /// <summary>HMAC-SHA256 key, at least 256 bits (32 UTF-8 bytes). Never committed.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "automataflow";

    public string Audience { get; set; } = "automataflow-api";

    public int AccessTokenMinutes { get; set; } = 15;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));

    public static bool IsValid(JwtOptions o) =>
        Encoding.UTF8.GetByteCount(o.SigningKey ?? string.Empty) >= 32 && !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience)
        && o.AccessTokenMinutes is >= 1 and <= 60;
}

public static class PlatformClaims
{
    public const string Organization = "org";
}

/// <summary>Issues short-lived access tokens carrying only the user id and the active organization (roles are never trusted from the token).</summary>
public sealed class TokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid organizationId)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(PlatformClaims.Organization, organizationId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(settings.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
