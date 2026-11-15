using System.Net;
using System.Net.Http.Json;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutomataFlow.IntegrationTests.Api;

public sealed record MeDto(Guid UserId, Guid OrganizationId, string Role);

/// <summary>Phase 5 step 2: authentication and authorization (docs/ARCHITECTURE.md §7.1 layer 1, §8).</summary>
public sealed class AuthenticationTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    private static object Login(string email, string password, Guid? organizationId = null) => new { email, password, organizationId };

    [Fact]
    public async Task Registration_creates_an_owner_whose_token_reaches_protected_endpoints()
    {
        var (_, tokens) = await api.RegisterAsync();
        Assert.Equal("Owner", tokens.Role);
        Assert.True(tokens.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(16));

        var me = await api.Client(tokens.AccessToken).GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal((tokens.OrganizationId, "Owner"), (me!.OrganizationId, me.Role));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Failures_are_uniform_and_repeated_failures_lock_the_account()
    {
        var (email, _) = await api.RegisterAsync();
        var unknown = await api.Client().PostAsJsonAsync("/api/v1/auth/login", Login("nobody-" + email, PlatformApi.Password));
        var wrong = await api.Client().PostAsJsonAsync("/api/v1/auth/login", Login(email, "wrong-password-123"));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (unknown.StatusCode, wrong.StatusCode));
        Assert.Contains("Authentication failed", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Authentication failed", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        for (var i = 0; i < 4; i++)
        {
            await api.Client().PostAsJsonAsync("/api/v1/auth/login", Login(email, "wrong-password-123"));
        }

        // Locked: even the right password is refused now.
        Assert.Equal((HttpStatusCode)423, (await api.Client().PostAsJsonAsync("/api/v1/auth/login", Login(email, PlatformApi.Password))).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_whole_family()
    {
        var (_, first) = await api.RegisterAsync();
        var rotated = await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<TokenDto>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the rotated token is treated as theft: it fails AND the newest token of the family dies too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);

        // Only hashes are stored.
        await using var db = api.Database.Context(null);
        Assert.False(await db.Set<AutomataFlow.Infrastructure.Identity.RefreshToken>().AnyAsync(t => t.TokenHash == first.RefreshToken || t.TokenHash == second.RefreshToken));
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, tokens) = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client().PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task A_removed_membership_takes_effect_immediately_although_the_access_token_is_still_valid()
    {
        var (_, tokens) = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.Client(tokens.AccessToken).GetAsync("/api/v1/me")).StatusCode);

        await using (var db = api.Database.Context(tokens.OrganizationId))
        {
            db.OrganizationMembers.Remove(await db.OrganizationMembers.SingleAsync());
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(tokens.AccessToken).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_sign_in_to_an_organization_they_do_not_belong_to()
    {
        var (email, _) = await api.RegisterAsync();
        var (_, other) = await api.RegisterAsync();
        var response = await api.Client().PostAsJsonAsync("/api/v1/auth/login", Login(email, PlatformApi.Password, other.OrganizationId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_or_for_another_audience_are_rejected()
    {
        var (_, tokens) = await api.RegisterAsync();
        var payload = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = payload.Issuer,
            Audience = payload.Audiences.Single(),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = payload.Subject, ["org"] = tokens.OrganizationId.ToString() },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(new byte[48]), SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client(forged).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client(tokens.AccessToken + "x").GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Roles_come_from_the_membership_not_the_token()
    {
        var (_, tokens) = await api.RegisterAsync();
        await using (var db = api.Database.Context(tokens.OrganizationId))
        {
            (await db.OrganizationMembers.SingleAsync()).ChangeRole(OrganizationRole.Viewer);
            await db.SaveChangesAsync();
        }

        var me = await api.Client(tokens.AccessToken).GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal("Viewer", me!.Role);
    }
}

/// <summary>Configuration-dependent behaviour, each with its own Api instance.</summary>
public sealed class AuthenticationConfigurationTests
{
    [Fact]
    public async Task Registration_is_disabled_unless_configured()
    {
        var api = new PlatformApi { Settings = new Dictionary<string, string?> { ["Auth:AllowRegistration"] = "false" } };
        await api.InitializeAsync();
        try
        {
            var response = await api.Client().PostAsJsonAsync("/api/v1/auth/register", new { email = "x@example.test", password = PlatformApi.Password, organizationName = "X", organizationSlug = "x-org" });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    [Fact]
    public async Task Authentication_endpoints_are_rate_limited()
    {
        var api = new PlatformApi { Settings = new Dictionary<string, string?> { ["Auth:PermitsPerMinute"] = "3" } };
        await api.InitializeAsync();
        try
        {
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 5; i++)
            {
                statuses.Add((await api.Client().PostAsJsonAsync("/api/v1/auth/login", new { email = "a@example.test", password = "whatever-123456" })).StatusCode);
            }

            Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
            Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_Api_refuses_to_start_with_a_weak_signing_key()
    {
        var api = new PlatformApi { Settings = new Dictionary<string, string?> { ["Auth:Jwt:SigningKey"] = "too-short" } };
        await api.InitializeAsync();
        try
        {
            Assert.Throws<OptionsValidationException>(() => api.Client());
        }
        finally
        {
            await api.DisposeAsync();
        }
    }
}
