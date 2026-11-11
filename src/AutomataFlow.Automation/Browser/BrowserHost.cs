using System.Diagnostics;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AutomataFlow.Automation.Browser;

/// <summary>Owns Playwright, the Chromium process(es) and the egress proxy lifecycle.</summary>
public interface IBrowserHost : IAsyncDisposable
{
    /// <summary>Number of Chromium launches since start (lifecycle metric).</summary>
    int LaunchCount { get; }

    /// <summary>Duration of the most recent Chromium launch (basic lifecycle timing, ADR-029).</summary>
    TimeSpan? LastLaunchDuration { get; }
}

/// <summary>
/// Browser lifecycle, kept separate from session/workflow execution.
/// Shared mode: one Chromium per process, recycled after N sessions or M minutes and after crashes.
/// Per-session mode: a dedicated Chromium per session. Every launch routes through the egress proxy; the
/// launch-level proxy is the egress proxy WITHOUT credentials, so anything not using per-session credentials is
/// refused (fail-closed, ADR-036).
/// </summary>
public sealed class BrowserHost : IBrowserHost
{
    private readonly BrowserEngineOptions _options;
    private readonly EgressProxyServer _proxy;
    private readonly TimeProvider _time;
    private readonly ILogger<BrowserHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private BrowserInstance? _shared;
    private bool _disposed;
    private int _launchCount;

    public BrowserHost(IOptions<BrowserEngineOptions> options, EgressProxyServer proxy, TimeProvider time, ILogger<BrowserHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _proxy = proxy;
        _time = time;
        _logger = logger;
    }

    public int LaunchCount => Volatile.Read(ref _launchCount);

    public TimeSpan? LastLaunchDuration { get; private set; }

    internal EgressProxyServer Proxy => _proxy;

    /// <summary>For tests: the current shared browser, if any.</summary>
    internal IBrowser? SharedBrowserForTesting => _shared?.Browser;

    /// <summary>
    /// Fail-closed gate: starts the proxy on first use and refuses to hand out a browser unless the proxy is
    /// running and accepting connections right now.
    /// </summary>
    internal async Task EnsureEgressAvailableAsync(CancellationToken cancellationToken)
    {
        if (_proxy.State == EgressProxyState.NotStarted)
        {
            _proxy.Start();
        }

        if (!await _proxy.ProbeAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
        {
            throw new BrowserOperationException(
                BrowserErrorCode.EgressUnavailable,
                "The egress proxy is not available; browser execution is refused (fail-closed).",
                isTransient: true);
        }
    }

    internal async Task<BrowserLease> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureEgressAvailableAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);

            if (_options.Isolation == BrowserIsolation.BrowserPerSession)
            {
                var dedicated = await LaunchAsync(cancellationToken).ConfigureAwait(false);
                dedicated.Retired = true; // closed as soon as its only session ends
                dedicated.ActiveSessions = 1;
                return new BrowserLease(this, dedicated);
            }

            if (_shared is not null && NeedsReplacement(_shared))
            {
                RetireLocked(_shared, "recycle");
                _shared = null;
            }

            _shared ??= await LaunchAsync(cancellationToken).ConfigureAwait(false);
            _shared.ActiveSessions++;
            _shared.TotalSessions++;
            return new BrowserLease(this, _shared);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ReleaseAsync(BrowserInstance instance, bool faulted)
    {
        BrowserInstance? toClose = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            instance.ActiveSessions--;
            if (faulted)
            {
                instance.Faulted = true;
                if (ReferenceEquals(_shared, instance))
                {
                    RetireLocked(instance, "faulted");
                    _shared = null;
                }
            }

            if (instance.Retired && instance.ActiveSessions <= 0 && !instance.Closed)
            {
                instance.Closed = true;
                toClose = instance;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toClose is not null)
        {
            await CloseBrowserAsync(toClose).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        BrowserInstance? shared;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shared = _shared;
            _shared = null;
        }
        finally
        {
            _gate.Release();
        }

        if (shared is not null && !shared.Closed)
        {
            shared.Closed = true;
            await CloseBrowserAsync(shared).ConfigureAwait(false);
        }

        // Disposing Playwright terminates the driver, which terminates any browser that did not close in time.
        _playwright?.Dispose();
        _playwright = null;
        await _proxy.StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private bool NeedsReplacement(BrowserInstance instance) =>
        instance.Faulted
        || !instance.Browser.IsConnected
        || instance.TotalSessions >= _options.RecycleAfterSessions
        || _time.GetUtcNow() - instance.LaunchedAt >= TimeSpan.FromMinutes(_options.RecycleAfterMinutes);

    private void RetireLocked(BrowserInstance instance, string reason)
    {
        instance.Retired = true;
        _logger.LogInformation("Retiring browser {BrowserNumber} ({Reason}) after {Sessions} sessions", instance.Number, reason, instance.TotalSessions);
        if (instance.ActiveSessions <= 0 && !instance.Closed)
        {
            instance.Closed = true;
            _ = CloseBrowserAsync(instance);
        }
    }

    private async Task<BrowserInstance> LaunchAsync(CancellationToken cancellationToken)
    {
        var proxyServer = $"http://{ChromiumLaunchArguments.ProxyHostAddress}:{_proxy.Endpoint!.Port}";
        var stopwatch = Stopwatch.StartNew();
        IBrowser browser;
        try
        {
            browser = await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
                ChromiumSandbox = _options.ChromiumSandbox,
                Timeout = _options.Timeouts.LaunchSeconds * 1000f,
                Proxy = new Proxy { Server = proxyServer, Bypass = ChromiumLaunchArguments.ProxyLoopbackRule },
                Args = ChromiumLaunchArguments.Build(),
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            throw new BrowserOperationException(
                BrowserErrorCode.BrowserNotInstalled,
                "Chromium is not installed for this Playwright version. Use the verification/worker image (ADR-033); browsers are never downloaded at runtime.",
                isTransient: false);
        }

        stopwatch.Stop();
        LastLaunchDuration = stopwatch.Elapsed;
        var number = Interlocked.Increment(ref _launchCount);
        if (!_options.ChromiumSandbox)
        {
            _logger.LogWarning("Chromium sandbox is DISABLED (Development only)");
        }

        var instance = new BrowserInstance(browser, number, _time.GetUtcNow());
        browser.Disconnected += (_, _) =>
        {
            instance.Faulted = true;
            if (!instance.Closed)
            {
                _logger.LogWarning("Browser {BrowserNumber} disconnected unexpectedly", number);
            }
        };

        _logger.LogInformation("Launched Chromium {Version} (browser {BrowserNumber}) in {LaunchMs} ms, sandbox {Sandbox}", browser.Version, number, stopwatch.ElapsedMilliseconds, _options.ChromiumSandbox);
        return instance;
    }

    private async Task CloseBrowserAsync(BrowserInstance instance)
    {
        try
        {
            await instance.Browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(_options.Timeouts.CloseSeconds)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // Escalation: the browser did not close in time. Disposing Playwright kills the driver and its browsers;
            // a fresh Playwright instance is created on next use.
            _logger.LogWarning("Browser {BrowserNumber} did not close cleanly; resetting Playwright", instance.Number);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _playwright?.Dispose();
                _playwright = null;
                if (_shared is not null)
                {
                    _shared.Faulted = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

internal sealed class BrowserInstance(IBrowser browser, int number, DateTimeOffset launchedAt)
{
    public IBrowser Browser { get; } = browser;

    public int Number { get; } = number;

    public DateTimeOffset LaunchedAt { get; } = launchedAt;

    public int ActiveSessions { get; set; }

    public int TotalSessions { get; set; }

    public bool Retired { get; set; }

    public bool Faulted { get; set; }

    public bool Closed { get; set; }
}

internal sealed class BrowserLease(BrowserHost host, BrowserInstance instance)
{
    private int _released;

    public IBrowser Browser => instance.Browser;

    public Task ReleaseAsync(bool faulted) =>
        Interlocked.Exchange(ref _released, 1) == 0 ? host.ReleaseAsync(instance, faulted) : Task.CompletedTask;
}
