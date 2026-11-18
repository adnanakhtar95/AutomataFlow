using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Worker;
using Serilog;

// Settings live next to the binaries: the launcher starts the Worker from an arbitrary working directory (ADR-075).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddWorkerServices(builder.Configuration);
builder.Services.AddProductionContainment(builder.Configuration);

await builder.Build().RunAsync();

namespace AutomataFlow.Worker
{
    public static class WorkerRegistration
    {
        /// <summary>
        /// The Worker's composition (also used by the end-to-end tests): persistence, the durable journal, the browser and
        /// workflow engines, artifact and log persistence, and the fail-closed loop. The platform containment handshake is
        /// registered separately (step 7); without it the Worker refuses to start.
        /// </summary>
        public static IServiceCollection AddWorkerServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddInfrastructure(configuration);
            services.AddAttemptJournal(configuration);
            services.AddBrowserEngine(configuration);
            services.AddWorkflowEngine(configuration);
            services.AddSingleton<IArtifactStore>(sp => sp.GetRequiredService<FileSystemArtifactStore>());
            services.AddSingleton<ExecutionLogBuffer>();
            services.AddSingleton<IExecutionLogSink>(sp => sp.GetRequiredService<ExecutionLogBuffer>());
            services.AddSingleton<ISecretProvider, ScopedSecretProvider>();
            services.AddSingleton<WorkerIncarnationHolder>();
            services.AddSingleton(sp => sp.GetRequiredService<WorkerIncarnationHolder>().Incarnation);
            services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.SectionName))
                .Validate(WorkerOptions.IsValid, "Worker options are out of range (the heartbeat must be at most a third of the claim lease).").ValidateOnStart();
            services.AddSingleton<WorkerStartup>();
            // The executor needs the incarnation, which exists only after the startup checks passed: resolve it lazily.
            services.AddSingleton(sp => new Lazy<AutomataFlow.Automation.Workflows.IWorkflowExecutor>(sp.GetRequiredService<AutomataFlow.Automation.Workflows.IWorkflowExecutor>));
            services.AddSingleton<RecordProcessor>();
            services.AddHostedService<WorkerService>();
            return services;
        }

        /// <summary>
        /// The production containment handshake (ADR-075): Linux namespace containment under the process-death supervisor.
        /// Registered only when the launcher configured a supervisor socket; otherwise no handshake exists and the Worker
        /// refuses to start (fail closed). There is no Windows supervisor, so a Windows Worker always refuses.
        /// </summary>
        public static IServiceCollection AddProductionContainment(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<ContainmentOptions>().Bind(configuration.GetSection(ContainmentOptions.SectionName));
            if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(configuration[$"{ContainmentOptions.SectionName}:SupervisorSocket"]))
            {
                services.AddSingleton<IContainmentHandshake, LinuxNamespaceContainmentHandshake>();
            }

            return services;
        }
    }
}
