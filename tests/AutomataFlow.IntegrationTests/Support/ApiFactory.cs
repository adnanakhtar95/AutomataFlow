using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutomataFlow.IntegrationTests.Support;

/// <summary>Hosts the real Api in-memory with an explicit connection string (so no environment variable is needed).</summary>
internal sealed class ApiFactory(string connectionString, string environment = "Development", IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    /// <summary>A connection string that can never connect (nothing listens on port 1) and fails fast.</summary>
    public const string DisableQueryFiltersSetting = "Testing:DisableQueryFilters";

    public const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2;Command Timeout=2";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Default", connectionString);

        // A fresh random signing key per factory: never a committed value.
        builder.UseSetting("Auth:Jwt:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
        builder.UseSetting("Auth:AllowRegistration", "true");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        // Test-only: the same Api with the EF query-filter backstop OFF, to prove the application layer scopes every
        // query on its own (docs/ARCHITECTURE.md §7.1 layer 6). The write guard stays on.
        if (settings is not null && settings.TryGetValue(DisableQueryFiltersSetting, out var disable) && disable == "true")
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AutomataFlowDbContext>();
                services.AddScoped(sp => new AutomataFlowDbContext(sp.GetRequiredService<DbContextOptions<AutomataFlowDbContext>>(), sp.GetRequiredService<TenantScope>())
                {
                    QueryFiltersDisabled = true,
                });
            });
        }
    }
}
