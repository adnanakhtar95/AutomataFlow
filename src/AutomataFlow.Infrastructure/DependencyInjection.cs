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
        services.AddScoped<OrganizationSecretProvider>();
        services.AddScoped<Jobs.AttemptRecorder>();
        services.AddOptions<Jobs.ArtifactStorageOptions>().Bind(configuration.GetSection(Jobs.ArtifactStorageOptions.SectionName));
        services.AddSingleton<Jobs.FileSystemArtifactStore>();
        services.AddKeyedSingleton(QueueDataSourceKey, (_, _) => Npgsql.NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new Jobs.RecordDispatcher(sp.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(QueueDataSourceKey)));
        services.AddOptions<Jobs.ImportOptions>().Bind(configuration.GetSection(Jobs.ImportOptions.SectionName))
            .Validate(o => o.MaxBytes is > 0 and <= 100L * 1024 * 1024 && o.MaxRecords is > 0 and <= 1_000_000 && o.MaxColumns is > 0 and <= 100 && o.MaxCellChars is > 0 and <= 10_000,
                "Imports limits are out of range (MaxBytes <= 100 MB, MaxRecords <= 1,000,000, MaxColumns <= 100, MaxCellChars <= 10,000).")
            .ValidateOnStart();
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.SectionName));

        return services;
    }

    /// <summary>Api use cases that act for an authenticated caller (they require <c>ICurrentUser</c>).</summary>
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<SecretService>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<Jobs.JobService>();
        services.AddScoped<Jobs.ExecutionQueryService>();
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

    /// <summary>
    /// The durable, fencing PostgreSQL attempt journal (ADR-071) on its own data source. Production registration always uses
    /// the database clock; there is no configuration that replaces it.
    /// </summary>
    public static IServiceCollection AddAttemptJournal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
        services.AddOptions<Persistence.Journal.AttemptJournalOptions>().Bind(configuration.GetSection(Persistence.Journal.AttemptJournalOptions.SectionName))
            .Validate(o => o.LeaseSeconds is >= 30 and <= 3_600 && o.LeaseSafetyMarginSeconds is >= 1 and <= 300, "Journal:LeaseSeconds must be 30-3600 and LeaseSafetyMarginSeconds 1-300.")
            .ValidateOnStart();
        services.AddKeyedSingleton(JournalDataSourceKey, (_, _) => Npgsql.NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<Persistence.Journal.PostgresAttemptJournal>(sp => new Persistence.Journal.PostgresAttemptJournal(
            sp.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(JournalDataSourceKey),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Persistence.Journal.AttemptJournalOptions>>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Persistence.Journal.PostgresAttemptJournal>>()));
        services.AddSingleton<Application.Workflows.Execution.IAttemptJournal>(sp => sp.GetRequiredService<Persistence.Journal.PostgresAttemptJournal>());
        services.AddSingleton(sp => new Persistence.Journal.FenceAdministration(
            sp.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(JournalDataSourceKey),
            sp.GetRequiredService<Application.Workflows.Execution.IAttemptJournal>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    private const string JournalDataSourceKey = "attempt-journal";
    private const string QueueDataSourceKey = "job-queue";
}