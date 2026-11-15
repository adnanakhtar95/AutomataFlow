using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutomataFlow.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.IntegrationTests.Support;

public sealed record TokenDto(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid OrganizationId, string Role);

/// <summary>A migrated database plus the real Api hosted in memory against it.</summary>
public sealed class PlatformApi : IAsyncLifetime
{
    public const string Password = "Correct-Horse-2026";

    private static int _sequence;

    public MigratedDatabase Database { get; } = new();

    internal ApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        var settings = new Dictionary<string, string?> { ["Auth:PermitsPerMinute"] = "100000" };
        foreach (var (key, value) in Settings)
        {
            settings[key] = value;
        }

        Factory = new ApiFactory(Database.ConnectionString, settings: settings);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>Extra configuration (the shared fixture lifts the auth rate limit, which has its own test).</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    public HttpClient Client(string? accessToken = null)
    {
        var client = Factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    /// <summary>Registers a fresh user with a fresh organization and returns its tokens.</summary>
    public async Task<(string Email, TokenDto Tokens)> RegisterAsync(string prefix = "user")
    {
        var n = Interlocked.Increment(ref _sequence);
        var email = $"{prefix}{n}-{Guid.NewGuid():N}@example.test";
        var response = await Client().PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, organizationName = $"Org {n}", organizationSlug = $"org-{n}-{Guid.NewGuid():N}"[..30].TrimEnd('-') });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (email, (await response.Content.ReadFromJsonAsync<TokenDto>())!);
    }

    public async Task GrantPlatformOperatorAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync(AccountService.PlatformOperatorRole))
        {
            await roles.CreateAsync(new IdentityRole<Guid>(AccountService.PlatformOperatorRole) { Id = Guid.NewGuid() });
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, AccountService.PlatformOperatorRole);
    }
}
