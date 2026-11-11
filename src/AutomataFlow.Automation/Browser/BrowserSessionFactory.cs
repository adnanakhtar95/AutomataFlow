using AutomataFlow.Application.Execution;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AutomataFlow.Automation.Browser;

/// <summary>The only way to obtain a browser: an isolated session bound to one egress policy.</summary>
public interface IBrowserSessionFactory
{
    Task<IBrowserSession> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken);
}

public sealed class BrowserSessionFactory : IBrowserSessionFactory, IDisposable
{
    private readonly BrowserHost _host;
    private readonly BrowserEngineOptions _options;
    private readonly IArtifactStore _artifacts;
    private readonly IExecutionLogSink _sink;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _slots;

    public BrowserSessionFactory(
        BrowserHost host,
        IOptions<BrowserEngineOptions> options,
        IArtifactStore artifacts,
        IExecutionLogSink sink,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _host = host;
        _options = options.Value;
        _artifacts = artifacts;
        _sink = sink;
        _time = time;
        _loggerFactory = loggerFactory;
        _slots = new SemaphoreSlim(_options.MaxConcurrentSessions, _options.MaxConcurrentSessions);
    }

    public async Task<IBrowserSession> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePolicyForMode(request);

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        BrowserLease? lease = null;
        EgressSession? egress = null;
        IBrowserContext? context = null;
        try
        {
            lease = await _host.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid();
            egress = _host.Proxy.Sessions.Register(id, request.Policy);

            context = await lease.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                // Per-session credentials: the proxy applies THIS session's policy (spike E6).
                Proxy = new Proxy
                {
                    Server = $"http://{ChromiumLaunchArguments.ProxyHostAddress}:{_host.Proxy.Endpoint!.Port}",
                    Username = egress.Username,
                    Password = egress.Password,
                    Bypass = ChromiumLaunchArguments.ProxyLoopbackRule,
                },
                ServiceWorkers = ServiceWorkerPolicy.Block,
                AcceptDownloads = request.AcceptDownloads,
                IgnoreHTTPSErrors = false,
                BypassCSP = false,
                JavaScriptEnabled = true,
                Permissions = [],
                ViewportSize = new ViewportSize { Width = _options.ViewportWidth, Height = _options.ViewportHeight },
                DeviceScaleFactor = 1,
                Locale = _options.Locale,
                TimezoneId = _options.TimeZone,
            }).WaitAsync(cancellationToken).ConfigureAwait(false);

            var session = new BrowserSession(
                id,
                request,
                _options,
                context,
                egress,
                lease,
                _artifacts,
                _sink,
                _loggerFactory.CreateLogger<BrowserSession>(),
                _time,
                () => _slots.Release());
            try
            {
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return session;
        }
        catch (Exception) when (context is null)
        {
            // Session object never took ownership: undo partial work here.
            egress?.Dispose();
            if (lease is not null)
            {
                await lease.ReleaseAsync(faulted: false).ConfigureAwait(false);
            }

            _slots.Release();
            throw;
        }
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Hosted multi-tenant sessions may only reach public https destinations (ADR-029/031); private-network
    /// exceptions are a Development-only convenience (e.g. local DemoCrm).
    /// </summary>
    private void ValidatePolicyForMode(BrowserSessionRequest request)
    {
        if (request.Policy.Rules.Count == 0)
        {
            throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, "A session needs at least one allowed host.", isTransient: false);
        }

        foreach (var rule in request.Policy.Rules)
        {
            if (rule.AllowPrivateNetwork && _options.Mode != ExecutionMode.Development)
            {
                throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, $"Private-network rule {rule} is only allowed in Development mode.", isTransient: false);
            }

            if (rule.Scheme != "https" && _options.Mode == ExecutionMode.HostedMultiTenant)
            {
                throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, $"Rule {rule} is not allowed: hosted mode is https-only.", isTransient: false);
            }
        }
    }
}
