using AutomataFlow.Infrastructure;
using AutomataFlow.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<WorkerHeartbeatService>();

await builder.Build().RunAsync();
