using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AutomataFlow.IntegrationTests.Support;

/// <summary>Hosts the real Api in-memory with an explicit connection string (so no environment variable is needed).</summary>
internal sealed class ApiFactory(string connectionString, string environment = "Development", IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    /// <summary>A connection string that can never connect (nothing listens on port 1) and fails fast.</summary>
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
    }
}
