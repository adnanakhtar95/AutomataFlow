using System.Text;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.Infrastructure.Security;
using AutomataFlow.Infrastructure.Workflows;
using AutomataFlow.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>The caller of the "Api side" of the rig: a fixed Owner of the rig's organization.</summary>
internal sealed class RigUser(Guid user, Guid organization) : ICurrentUser
{
    public bool IsAuthenticated => true;

    public Guid UserId => user;

    public Guid OrganizationId => organization;

    public OrganizationRole Role => OrganizationRole.Owner;
}

/// <summary>Test-only containment: the real supervisor and its handshake are step 7.</summary>
internal sealed class TestContainment : IContainmentHandshake
{
    public Task<ContainmentUnit> VerifyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ContainmentUnit(ContainmentKind.LinuxCgroup, "/test/" + Guid.NewGuid().ToString("N")));
}

/// <summary>
/// Phase 5 end-to-end rig: a fresh platform database, the real platform services acting for an Owner (job creation,
/// workflow publication, encrypted secrets) and the REAL Worker composition (claiming, PostgreSQL journal, real Chromium,
/// egress proxy) running in-process against the separately running DemoCrm.
/// </summary>
internal sealed class PlatformRig : IAsyncDisposable
{
    private readonly string _server;
    private readonly string _database;
    private readonly List<IHost> _workers = [];
    private readonly string _keys;
    private readonly string _artifacts;
    private readonly string _ledgers;

    private PlatformRig(DemoCrmProcess demoCrm, string? sharedRoot, string server, string database, string connectionString)
    {
        DemoCrm = demoCrm;
        // A shared root (the step 7 supervision container mounts the same volume at the same path) lets Workers in another
        // container decrypt secrets and store artifacts exactly as this rig expects.
        string Dir(string name) => sharedRoot is null
            ? Directory.CreateTempSubdirectory(name).FullName
            : Directory.CreateDirectory(Path.Combine(sharedRoot, name + Guid.NewGuid().ToString("N")[..12])).FullName;
        _keys = Dir("af-keys-");
        _artifacts = Dir("af-artifacts-");
        _ledgers = Dir("af-ledgers-");
        _server = server;
        _database = database;
        ConnectionString = connectionString;
    }

    public DemoCrmProcess DemoCrm { get; }

    public string ConnectionString { get; }

    public Guid Organization { get; private set; }

    public Guid User { get; } = Guid.CreateVersion7();

    public string ArtifactsRoot => _artifacts;

    public ServiceProvider Platform { get; private set; } = null!;

    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Everything the in-process Workers logged (for diagnosing failures; also used by leak checks).</summary>
    public CapturingLoggerProvider WorkerLogs { get; } = new();

    public static Task<PlatformRig> CreateAsync(BrowserFixture fixture) => CreateAsync(fixture.DemoCrm, null);

    public static async Task<PlatformRig> CreateAsync(DemoCrmProcess demoCrm, string? sharedRoot)
    {
        var server = Environment.GetEnvironmentVariable("AUTOMATAFLOW_TEST_DB")
            ?? throw new InvalidOperationException("Set AUTOMATAFLOW_TEST_DB to run the platform end-to-end tests.");
        var name = $"automataflow_e2e_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        var rig = new PlatformRig(demoCrm, sharedRoot, server, name, new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        await rig.InitializeAsync();
        return rig;
    }

    private async Task InitializeAsync()
    {
        Settings["ConnectionStrings:Default"] = ConnectionString;
        Settings["DataProtection:KeysDirectory"] = _keys;
        Settings["Artifacts:RootDirectory"] = _artifacts;
        Settings["Automation:Browser:Mode"] = "Development";
        Settings["Automation:Browser:DevelopmentPrivateNetworkOrigins:0"] = DemoCrm.BaseUrl;
        Settings["Automation:EgressProxy:LedgerDirectory"] = _ledgers;
        Settings["Worker:PollIntervalMilliseconds"] = "200";
        Settings["Worker:HeartbeatSeconds"] = "2";
        Settings["Worker:ClaimLeaseSeconds"] = "30";
        Settings["Worker:RecoveryIntervalSeconds"] = "2";
        Settings["Worker:FenceRecheckSeconds"] = "5";
        Settings["Worker:FenceWaitSeconds"] = "60";

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddPlatformServices();
        services.AddScoped<ICurrentUser>(_ => new RigUser(ActingUser, ActingOrganization));
        Platform = services.BuildServiceProvider();

        await using var scope = Platform.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>();
        await db.Database.MigrateAsync();
        var organization = AutomataFlow.Domain.Organizations.Organization.Create("Rig", "rig-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow);
        Organization = organization.Id;
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(Organization);
        db.Users.Add(new ApplicationUser { Id = User, UserName = $"owner-{User:N}@example.test", Email = $"owner-{User:N}@example.test", CreatedAt = DateTimeOffset.UtcNow });
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(OrganizationMember.Create(Organization, User, OrganizationRole.Owner, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private readonly AsyncLocal<(Guid User, Guid Organization)?> _actingAs = new();

    private Guid ActingUser => _actingAs.Value?.User ?? User;

    private Guid ActingOrganization => _actingAs.Value?.Organization ?? Organization;

    /// <summary>Within the returned scope, the rig's use cases act for ANOTHER organization's Owner (cross-tenant tests).</summary>
    public IDisposable ActAs(Guid organization, Guid user)
    {
        _actingAs.Value = (user, organization);
        return new Restore(() => _actingAs.Value = null);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Creates a second organization with its own Owner (for cross-tenant tests).</summary>
    public async Task<(Guid Organization, Guid User)> AddOrganizationAsync()
    {
        var user = Guid.CreateVersion7();
        var organization = AutomataFlow.Domain.Organizations.Organization.Create("Other", "other-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow);
        await using var scope = Platform.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(organization.Id);
        var db = scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>();
        db.Users.Add(new ApplicationUser { Id = user, UserName = $"owner-{user:N}@example.test", Email = $"owner-{user:N}@example.test", CreatedAt = DateTimeOffset.UtcNow });
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(OrganizationMember.Create(organization.Id, user, OrganizationRole.Owner, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (organization.Id, user);
    }

    /// <summary>Runs a use case in a fresh scope bound to the acting organization (the rig's own unless <see cref="ActAs"/>).</summary>
    public async Task<T> AsOwnerAsync<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull
    {
        await using var scope = Platform.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(ActingOrganization);
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task<Guid> PublishAsync(string definition, string name = "democrm")
    {
        var created = await AsOwnerAsync<WorkflowService, ServiceResult<VersionDetail>>(w => w.CreateAsync(name + "-" + Guid.NewGuid().ToString("N")[..6], null, definition, default));
        Assert.Equal(ServiceStatus.Ok, created.Status);
        var published = await AsOwnerAsync<WorkflowService, ServiceResult<VersionSummary>>(w => w.PublishAsync(created.Value!.WorkflowId, created.Value.Version.Id, true, default));
        Assert.Equal(ServiceStatus.Ok, published.Status);
        await AsOwnerAsync<SecretService, bool>(s => s.SetAsync(ActingOrganization, "crmPassword", "DemoPass-2026!", default));
        return created.Value!.Version.Id;
    }

    /// <summary>Publishes a workflow package and stores its secret (Phase 9 packages use their own parameter and secret names).</summary>
    public async Task<Guid> PublishPackageAsync(string definition, string secretName, string name)
    {
        var created = await AsOwnerAsync<WorkflowService, ServiceResult<VersionDetail>>(w => w.CreateAsync(name + "-" + Guid.NewGuid().ToString("N")[..6], null, definition, default));
        Assert.True(created.Status == ServiceStatus.Ok, string.Join("; ", created.Errors.Select(e => e.Path + " " + e.Code)));
        var published = await AsOwnerAsync<WorkflowService, ServiceResult<VersionSummary>>(w => w.PublishAsync(created.Value!.WorkflowId, created.Value.Version.Id, true, default));
        Assert.Equal(ServiceStatus.Ok, published.Status);
        await AsOwnerAsync<SecretService, bool>(s => s.SetAsync(ActingOrganization, secretName, "DemoPass-2026!", default));
        return created.Value!.Version.Id;
    }

    public async Task<Guid> CreateJobAsync(Guid version, string csv, Dictionary<string, string> parameters, JobOptions options)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var created = await AsOwnerAsync<JobService, ServiceResult<JobCreated>>(j =>
            j.CreateAsync(new CreateJobCommand(version, parameters, options, null, content, "records.csv", ImportFormat.Csv), default));
        Assert.True(created.Status == ServiceStatus.Ok, string.Join("; ", created.Errors.Select(e => e.Path + " " + e.Code)));
        return created.Value!.JobId;
    }

    public async Task<Guid> CreateJobAsync(Guid version, string csv, JobOptions? options = null)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parameters = new Dictionary<string, string> { ["crmBaseUrl"] = DemoCrm.BaseUrl, ["crmUser"] = "agent@democrm.test" };
        var created = await AsOwnerAsync<JobService, ServiceResult<JobCreated>>(j =>
            j.CreateAsync(new CreateJobCommand(version, parameters, options ?? new JobOptions(2, 3, 1), null, content, "records.csv", ImportFormat.Csv), default));
        Assert.Equal(ServiceStatus.Ok, created.Status);
        return created.Value!.JobId;
    }

    /// <summary>Starts one in-process Worker (the real composition) with the test containment handshake.</summary>
    public async Task<IHost> StartWorkerAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>(Settings);
        configure?.Invoke(settings);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(WorkerLogs);
        builder.Services.AddWorkerServices(builder.Configuration);
        builder.Services.AddSingleton<IContainmentHandshake, TestContainment>();
        var host = builder.Build();
        await host.StartAsync();
        _workers.Add(host);
        return host;
    }

    public async Task<JobStatus> WaitForJobAsync(Guid job, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var status = await QueryAsync(db => db.Jobs.Where(j => j.Id == job).Select(j => j.Status).SingleAsync());
            if (status is not (JobStatus.Pending or JobStatus.Running))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                var records = await QueryAsync(db => db.JobRecords.AsNoTracking().Where(r => r.JobId == job)
                    .Select(r => r.RowNumber + ":" + r.Status + ":" + r.ErrorCode + ":" + r.AttemptCount).ToListAsync());
                Assert.Fail($"Job {job} still {status} after {timeout}. Records: {string.Join(" | ", records)}{Environment.NewLine}"
                    + string.Join(Environment.NewLine, WorkerLogs.Lines.Where(l => !l.StartsWith("Debug", StringComparison.Ordinal)).TakeLast(80)));
            }

            await Task.Delay(250);
        }
    }

    /// <summary>Reads with another organization's scope (e.g. one registered through the Api process).</summary>
    public async Task<T> QueryAsync<T>(Guid organization, Func<AutomataFlowDbContext, Task<T>> query)
    {
        await using var scope = Platform.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(organization);
        return await query(scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>());
    }

    /// <summary>Grants the platform-operator Identity role (a platform administration act, done directly in the database).</summary>
    public async Task GrantPlatformOperatorAsync(Guid user)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var grant = new NpgsqlCommand("""
            INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp")
            SELECT @role, 'PlatformOperator', 'PLATFORMOPERATOR', @stamp WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'PLATFORMOPERATOR');
            INSERT INTO "AspNetUserRoles" ("UserId","RoleId") SELECT @user, "Id" FROM "AspNetRoles" WHERE "NormalizedName" = 'PLATFORMOPERATOR';
            """, connection);
        grant.Parameters.AddWithValue("role", Guid.NewGuid());
        grant.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString());
        grant.Parameters.AddWithValue("user", user);
        await grant.ExecuteNonQueryAsync();
    }

    /// <summary>Reads with the rig's organization scope.</summary>
    public async Task<T> QueryAsync<T>(Func<AutomataFlowDbContext, Task<T>> query)
    {
        await using var scope = Platform.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(Organization);
        return await query(scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            try
            {
                await worker.StopAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                worker.Dispose();
            }
        }

        await Platform.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
        foreach (var dir in new[] { _keys, _artifacts, _ledgers })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
