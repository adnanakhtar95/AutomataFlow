using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AccountService>();
        return services;
    }
}