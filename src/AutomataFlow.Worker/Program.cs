using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddWorkerServices(builder.Configuration);

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
    }
}
