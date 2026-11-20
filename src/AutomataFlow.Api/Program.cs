using AutomataFlow.Api.Auth;
using AutomataFlow.Api.Health;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Container health probe (the release image has no shell or curl): exit 0 when the local readiness endpoint answers 200.
if (args is ["healthcheck", ..])
{
    return await HealthProbe.RunAsync(args.Length > 1 ? args[1] : "http://127.0.0.1:8080/health/ready");
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformServices();
builder.Services.AddAttemptJournal(builder.Configuration); // platform-operator fence confirmation only; the Api never executes

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SupportNonNullableReferenceTypes();
    o.SchemaFilter<AutomataFlow.Api.OpenApi.RequiredPropertiesSchemaFilter>();
});

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// Behind the gateway (deploy/web), the client address comes from X-Forwarded-For, but ONLY from proxies in
// Proxy:TrustedNetworks (CIDR list). Without it the auth rate limit would see every client as the gateway's address.
var trustedNetworks = TrustedProxies.Parse(builder.Configuration["Proxy:TrustedNetworks"]);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = Math.Clamp(builder.Configuration.GetValue("Proxy:ForwardLimit", 1), 1, 3); // proxies in front of the Api (gateway = 1, TLS proxy + gateway = 2)
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var network in trustedNetworks)
    {
        o.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();

// Explicit schema migration (docs/DEPLOYMENT.md): run once per release BEFORE starting the new Api and Workers.
// The Api never migrates on its own start, so an upgrade is always a deliberate, observable step.
if (args is ["migrate", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AutomataFlowDbContext>();
    var applied = await SchemaMigrator.MigrateAsync(db, app.Configuration["Database:ApplicationRole"], CancellationToken.None);
    Log.Information("Applied {Count} pending migrations: {Migrations}; database schema is up to date", applied.Count, applied);
    return 0;
}

if (trustedNetworks.Count > 0)
{
    app.UseForwardedHeaders();
}

// First platform operator (docs/DEPLOYMENT.md "Onboarding"): credentials from the environment only, never arguments
// (process lists) and never a default. Creates the operator's own operations workspace. Refuses an existing address.
if (args is ["bootstrap-operator", ..])
{
    var settings = app.Configuration;
    var email = settings["AF_BOOTSTRAP_EMAIL"];
    var password = settings["AF_BOOTSTRAP_PASSWORD"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
    {
        Log.Error("Set AF_BOOTSTRAP_EMAIL and AF_BOOTSTRAP_PASSWORD (and optionally AF_BOOTSTRAP_WORKSPACE_NAME / AF_BOOTSTRAP_WORKSPACE_SLUG)");
        return 2;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var result = await scope.ServiceProvider.GetRequiredService<AutomataFlow.Infrastructure.Identity.AccountService>().BootstrapOperatorAsync(
        email, password, settings["AF_BOOTSTRAP_WORKSPACE_NAME"] ?? "Platform operations", settings["AF_BOOTSTRAP_WORKSPACE_SLUG"] ?? "platform-operations",
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<Guid>>>(), CancellationToken.None);
    if (result.Status != AutomataFlow.Infrastructure.Identity.AccountStatus.Ok)
    {
        Log.Error("Bootstrap refused: {Status} {Errors}", result.Status, result.Errors);
        return 1;
    }

    Log.Information("Platform operator created {UserId} with workspace {OrganizationId}", result.Session!.UserId, result.Session.OrganizationId);
    return 0;
}

// Artifact retention (docs/RUNBOOK.md): `purge-artifacts <days>` deletes screenshots and downloads older than <days>
// days in every workspace. Explicit and scheduled by the operator; records, results and logs are kept.
if (args is ["purge-artifacts", var daysArgument, ..])
{
    if (!int.TryParse(daysArgument, out var days) || days < AutomataFlow.Infrastructure.Jobs.ArtifactRetention.MinimumDays)
    {
        Log.Error("Usage: purge-artifacts <days> (at least {Minimum})", AutomataFlow.Infrastructure.Jobs.ArtifactRetention.MinimumDays);
        return 2;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var purged = await scope.ServiceProvider.GetRequiredService<AutomataFlow.Infrastructure.Jobs.ArtifactRetention>().PurgeAsync(days, CancellationToken.None);
    Log.Information("Purged {Rows} artifacts ({Bytes} bytes)", purged.Rows, purged.BytesFreed);
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseMiddleware<MembershipVerificationMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Liveness: the process is up and serving. No dependencies are checked.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

// Readiness: dependencies (PostgreSQL) are reachable.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();
return 0;

/// <summary>Exposed so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
