using AutomataFlow.Infrastructure;
using AutomataFlow.Supervisor;
using Npgsql;
using Serilog;

// The process-death supervisor (ADR-075). Started ONLY by deploy/worker/af-worker-launch.sh, which exec's into it as uid
// 10002 with CAP_NET_ADMIN alone, after creating the unit (its child). Configuration comes from environment variables.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));

// No connection pooling: while the drain runs, the supervisor holds no database socket of its own (ADR-075).
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:Default"] = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAttemptJournal(builder.Configuration);
builder.Services.AddOptions<SupervisorOptions>().Bind(builder.Configuration.GetSection(SupervisorOptions.SectionName))
    .Validate(SupervisorOptions.IsValid, "Supervisor options are out of range or the unit wrapper PID is missing.").ValidateOnStart();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(5)); // graceful stop runs the full fence
builder.Services.AddHostedService<SupervisorService>();

await builder.Build().RunAsync();
return Environment.ExitCode;
