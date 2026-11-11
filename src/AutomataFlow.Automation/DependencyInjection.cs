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
        services.AddOptions<EgressProxyOptions>().Bind(configuration.GetSection(EgressProxyOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<IUpstreamConnector, DirectUpstreamConnector>();
        services.AddSingleton<EgressProxyServer>();
        services.AddSingleton<BrowserHost>();
        services.AddSingleton<IBrowserHost>(sp => sp.GetRequiredService<BrowserHost>());
        services.AddSingleton<IBrowserSessionFactory, BrowserSessionFactory>();
        return services;
    }
}
