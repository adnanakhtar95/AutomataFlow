using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.Infrastructure.Security;
using AutomataFlow.Infrastructure.Workflows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutomataFlow.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>Registers persistence. The connection string is read from configuration (environment variables in practice).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured. " +
                "Set the ConnectionStrings__Default environment variable (see deploy/.env.example).");
        }

        services.AddScoped<TenantScope>();
        services.AddDbContext<AutomataFlowDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Data Protection keys must be shared by the Api (encrypts) and the Worker (decrypts): a directory on a shared,
        // access-restricted volume in deployments (DataProtection:KeysDirectory). Without it, keys stay in the user profile.
        var dataProtection = services.AddDataProtection().SetApplicationName("AutomataFlow");
        if (configuration["DataProtection:KeysDirectory"] is { Length: > 0 } keys)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ValueProtection>();
        services.AddScoped<SecretService>();
        services.AddScoped<OrganizationSecretProvider>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<Jobs.JobService>();
        services.AddOptions<Jobs.ImportOptions>().Bind(configuration.GetSection(Jobs.ImportOptions.SectionName))
            .Validate(o => o.MaxBytes is > 0 and <= 100L * 1024 * 1024 && o.MaxRecords is > 0 and <= 1_000_000 && o.MaxColumns is > 0 and <= 100 && o.MaxCellChars is > 0 and <= 10_000,
                "Imports limits are out of range (MaxBytes <= 100 MB, MaxRecords <= 1,000,000, MaxColumns <= 100, MaxCellChars <= 10,000).")
            .ValidateOnStart();
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Identity for the Api (docs/ARCHITECTURE.md §8): user store, password hashing (Identity PBKDF2), lockout, the
    /// platform-operator role, and <see cref="AccountService"/>. No cookie or UI endpoints.
    /// </summary>
    public static IServiceCollection AddPlatformIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AutomataFlowDbContext>();
        services.AddOptions<AccountOptions>().Bind(configuration.GetSection(AccountOptions.SectionName))
            .Validate(o => o.RefreshTokenDays is >= 1 and <= 90, "Auth:RefreshTokenDays must be 1-90.").ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AccountService>();
        return services;
    }
}