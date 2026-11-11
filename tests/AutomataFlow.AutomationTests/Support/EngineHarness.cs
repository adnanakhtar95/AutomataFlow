using System.Net;
using AutomataFlow.Application.Execution;
using AutomataFlow.Automation;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Network;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutomataFlow.AutomationTests.Support;

/// <summary>
/// The real engine wired through the real DI registration (<see cref="DependencyInjection.AddBrowserEngine"/>),
/// with test doubles only for DNS, upstream connections, artifact storage and the log sink.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private EngineHarness(ServiceProvider provider, FakeDnsResolver dns, RecordingConnector connector, CapturingLoggerProvider logs)
    {
        _provider = provider;
        Dns = dns;
        Connector = connector;
        Logs = logs;
        Factory = provider.GetRequiredService<IBrowserSessionFactory>();
        Host = provider.GetRequiredService<BrowserHost>();
        Proxy = provider.GetRequiredService<EgressProxyServer>();
        Store = (InMemoryArtifactStore)provider.GetRequiredService<IArtifactStore>();
        Sink = (CollectingLogSink)provider.GetRequiredService<IExecutionLogSink>();
    }

    public IBrowserSessionFactory Factory { get; }

    public BrowserHost Host { get; }

    public EgressProxyServer Proxy { get; }

    public InMemoryArtifactStore Store { get; }

    public CollectingLogSink Sink { get; }

    public FakeDnsResolver Dns { get; }

    public RecordingConnector Connector { get; }

    public CapturingLoggerProvider Logs { get; }

    public static ExecutionScope Scope { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

    public static EngineHarness Create(
        FixtureServers servers,
        Action<BrowserEngineOptions>? configure = null,
        Action<EgressProxyOptions>? configureProxy = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var dns = new FakeDnsResolver();
        dns.Set("public.test", "8.8.8.8");
        dns.Set("private.test", "10.0.0.5");
        dns.Set("loop.test", "127.0.0.1");
        dns.Set("metadata.test", "169.254.169.254");
        dns.Set("mixed.test", "8.8.4.4", "192.168.1.10");
        dns.Set("v6loop.test", "::1");
        dns.Set("v6ula.test", "fd00::1");
        dns.Set("v6ll.test", "fe80::1");
        dns.Set("mapped.test", "::ffff:127.0.0.1");
        dns.Set("nat64.test", "64:ff9b::7f00:1");
        dns.SetSequence("rebind.test", ["8.8.8.8"], ["127.0.0.1"]);

        var connector = new RecordingConnector();
        var site = new IPEndPoint(IPAddress.Loopback, servers.PublicPort);
        connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), site);
        connector.Map(new IPEndPoint(IPAddress.Parse("8.8.4.4"), 80), site);

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddSingleton<IDnsResolver>(dns);
        services.AddSingleton<IUpstreamConnector>(connector);
        services.AddSingleton<IArtifactStore, InMemoryArtifactStore>();
        services.AddSingleton<IExecutionLogSink, CollectingLogSink>();
        services.AddBrowserEngine(new ConfigurationBuilder().Build());
        services.PostConfigure<BrowserEngineOptions>(o =>
        {
            o.Mode = ExecutionMode.Development; // Development only so the local DemoCrm (loopback) can be an explicit exception.
            o.ChromiumSandbox = true;            // sandbox stays ON (ADR-035)
            configure?.Invoke(o);
        });
        if (configureProxy is not null)
        {
            services.PostConfigure(configureProxy);
        }

        return new EngineHarness(services.BuildServiceProvider(validateScopes: true), dns, connector, logs);
    }

    public Task<IBrowserSession> CreateSessionAsync(EgressPolicy policy, bool acceptDownloads = false, IReadOnlyList<string>? maskSelectors = null) =>
        Factory.CreateAsync(new BrowserSessionRequest(Scope, policy) { AcceptDownloads = acceptDownloads, MaskSelectors = maskSelectors ?? [] }, CancellationToken.None);

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

/// <summary>Shared fixture servers, DemoCrm process and a default engine for the whole browser test run.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    public FixtureServers Servers { get; } = new();

    public DemoCrmProcess DemoCrm { get; } = new();

    public EngineHarness Engine { get; private set; } = null!;

    /// <summary>Rules for the hostile matrix: hosts are ALLOWED by rule so that the IP layer must do the blocking.</summary>
    public EgressPolicy HostilePolicy => new(
    [
        AllowedHostRule.Parse("http://public.test"),
        AllowedHostRule.Parse("http://rebind.test"),
        AllowedHostRule.Parse("http://mixed.test"),
        AllowedHostRule.Parse("http://private.test"),
        AllowedHostRule.Parse("http://loop.test"),
        AllowedHostRule.Parse("http://metadata.test"),
        AllowedHostRule.Parse("http://v6loop.test"),
        AllowedHostRule.Parse("http://v6ula.test"),
        AllowedHostRule.Parse("http://v6ll.test"),
        AllowedHostRule.Parse("http://mapped.test"),
        AllowedHostRule.Parse("http://nat64.test"),
        AllowedHostRule.Parse($"http://127.0.0.1:{Servers.InternalPort}"),
        AllowedHostRule.Parse($"https://127.0.0.1:{Servers.InternalPort}"),
        AllowedHostRule.Parse($"http://[::1]:{Servers.InternalPort}"),
        AllowedHostRule.Parse($"http://[::ffff:127.0.0.1]:{Servers.InternalPort}"),
        AllowedHostRule.Parse("http://10.0.0.5"),
        AllowedHostRule.Parse("http://169.254.169.254"),
        AllowedHostRule.Parse("http://[fd00::1]"),
    ]);

    public static EgressPolicy PublicPolicy => EgressPolicy.Create("http://public.test");

    /// <summary>DemoCrm is local, so it needs a Development-only, port-precise private-network exception.</summary>
    public EgressPolicy DemoCrmPolicy => new([AllowedHostRule.Parse(DemoCrm.BaseUrl, allowPrivateNetwork: true)]);

    public async Task InitializeAsync()
    {
        await Servers.StartAsync();
        await DemoCrm.StartAsync();
        Engine = EngineHarness.Create(Servers);
    }

    public async Task DisposeAsync()
    {
        await Engine.DisposeAsync();
        await DemoCrm.DisposeAsync();
        await Servers.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserTestGroup : ICollectionFixture<BrowserFixture>
{
    public const string Name = "browser";
}
