using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Automation;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the browser engine and its in-process egress proxy. The host must also register
    /// <c>IArtifactStore</c> and <c>IExecutionLogSink</c> (Infrastructure, Phase 5). The Api never calls this.
    /// </summary>
    public static IServiceCollection AddBrowserEngine(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BrowserEngineOptions>().Bind(configuration.GetSection(BrowserEngineOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BrowserEngineOptions>, BrowserEngineOptionsValidator>();
        services.AddOptions<EgressProxyOptions>().Bind(configuration.GetSection(EgressProxyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EgressProxyOptions>, EgressProxyOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<IUpstreamConnector, DirectUpstreamConnector>();
        services.TryAddSingleton<IConnectionIdentityProvider, OsConnectionIdentityProvider>();

        // One ledger per process (= Worker incarnation). Phase 5 passes the registered incarnation id instead.
        services.TryAddSingleton<IConnectionLedger>(sp => new FileConnectionLedger(
            sp.GetRequiredService<IOptions<EgressProxyOptions>>().Value.LedgerDirectory
                ?? Path.Combine(Path.GetTempPath(), "automataflow", "ledger"),
            Guid.NewGuid()));
        services.AddSingleton<EgressProxyServer>();
        services.AddSingleton<BrowserHost>();
        services.AddSingleton<IBrowserHost>(sp => sp.GetRequiredService<BrowserHost>());
        services.AddSingleton<IBrowserSessionFactory, BrowserSessionFactory>();
        return services;
    }

    /// <summary>
    /// Registers the workflow engine (docs/PHASE4_PLAN.md §3): the 13 handlers, the registry (which fails startup on any
    /// mismatch with the action catalog) and the executor. It deliberately registers NO <c>IAttemptJournal</c> and no
    /// <c>ISecretProvider</c> or <c>WorkerIncarnation</c>: a host without a durable, fencing journal cannot resolve the
    /// executor (fail closed, ADR-058). Requires <see cref="AddBrowserEngine"/>. The Api never calls this.
    /// </summary>
    public static IServiceCollection AddWorkflowEngine(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<Workflows.WorkflowEngineOptions>().Bind(configuration.GetSection(Workflows.WorkflowEngineOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<Workflows.WorkflowEngineOptions>, Workflows.WorkflowEngineOptionsValidator>();
        foreach (var handler in Workflows.ActionRegistry.CreateDefaultHandlers())
        {
            services.AddSingleton(handler);
        }

        services.AddSingleton<Workflows.ActionRegistry>();
        services.AddSingleton<Workflows.IWorkflowExecutor>(sp => new Workflows.WorkflowExecutor(
            sp.GetRequiredService<Workflows.ActionRegistry>(),
            sp.GetRequiredService<Application.Workflows.Execution.IAttemptJournal>(),
            sp.GetRequiredService<IBrowserSessionFactory>(),
            sp.GetRequiredService<Application.Workflows.Execution.ISecretProvider>(),
            sp.GetRequiredService<Application.Workflows.Execution.WorkerIncarnation>(),
            sp.GetRequiredService<IOptions<Workflows.WorkflowEngineOptions>>(),
            sp.GetRequiredService<IOptions<BrowserEngineOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Workflows.WorkflowExecutor>>(),
            sp.GetService<Application.Workflows.Execution.IExecutionFileProvider>()));
        return services;
    }
}
