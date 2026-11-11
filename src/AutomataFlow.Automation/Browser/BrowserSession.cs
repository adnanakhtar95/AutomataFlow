using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AutomataFlow.Application.Execution;
using AutomataFlow.Automation.Network;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace AutomataFlow.Automation.Browser;

/// <summary>
/// One isolated browser session (= one record attempt): a fresh context with its own proxy credentials and policy.
/// All operations honour timeouts, the session deadline and the caller's cancellation token; cancellation closes the
/// context, which aborts in-flight Playwright calls (spike E7). Dispose always cleans up.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    Guid Id { get; }

    /// <summary>Requests blocked by any layer (route guard, redirect inspection or egress proxy).</summary>
    int BlockedRequestCount { get; }

    Task<NavigationResult> NavigateAsync(Uri url, CancellationToken cancellationToken);

    Task ClickAsync(ElementSelector selector, CancellationToken cancellationToken);

    /// <summary>
    /// Clicks an element that triggers a navigation (e.g. a form submit) and waits until the NEW document has
    /// loaded, so later reads never race with the old page (even when the URL does not change).
    /// </summary>
    Task SubmitAsync(ElementSelector selector, CancellationToken cancellationToken);

    Task FillAsync(ElementSelector selector, InputValue value, CancellationToken cancellationToken);

    Task SelectAsync(ElementSelector selector, SelectChoice choice, CancellationToken cancellationToken);

    Task WaitForAsync(WaitCondition condition, CancellationToken cancellationToken);

    Task<string> GetTextAsync(ElementSelector selector, CancellationToken cancellationToken);

    Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, CancellationToken cancellationToken);

    Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken);

    Task<DownloadResult> DownloadAsync(ElementSelector trigger, CancellationToken cancellationToken);
}

internal sealed class BrowserSession : IBrowserSession
{
    public const string MaskColor = "#FF00FF";
    private const string DeniedHeader = "x-automataflow-egress-denied";

    private static readonly string[] BuiltInMaskSelectors =
    [
        "input[type=password]",
        "[autocomplete^=\"cc-\"]",
        "[data-af-sensitive]",
    ];

    private readonly BrowserSessionRequest _request;
    private readonly BrowserEngineOptions _options;
    private readonly IBrowserContext _context;
    private readonly EgressSession _egress;
    private readonly BrowserLease _lease;
    private readonly IArtifactStore _artifacts;
    private readonly IExecutionLogSink _sink;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action _releaseSlot;
    private readonly CancellationTokenSource _deadline;
    private static readonly TimeSpan FailureScreenshotTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentQueue<ExecutionLogEntry> _pending = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ConcurrentBag<string> _sensitiveValues = [];
    private readonly ConcurrentBag<ElementSelector> _sensitiveSelectors = [];
    private readonly string _tempDirectory;
    private readonly Stopwatch _age = Stopwatch.StartNew();
    private IPage _page = null!;
    private int _pageCount;
    private int _blocked;
    private int _downloads;
    private int _disposed;
    private volatile bool _redirectViolation;
    private volatile bool _crashed;
    private volatile bool _aborted;

    public BrowserSession(
        Guid id,
        BrowserSessionRequest request,
        BrowserEngineOptions options,
        IBrowserContext context,
        EgressSession egress,
        BrowserLease lease,
        IArtifactStore artifacts,
        IExecutionLogSink sink,
        ILogger logger,
        TimeProvider time,
        Action releaseSlot)
    {
        Id = id;
        _request = request;
        _options = options;
        _context = context;
        _egress = egress;
        _lease = lease;
        _artifacts = artifacts;
        _sink = sink;
        _logger = logger;
        _time = time;
        _releaseSlot = releaseSlot;
        _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Timeouts.SessionSeconds));
        _tempDirectory = Path.Combine(Path.GetTempPath(), "automataflow", id.ToString("N"));
    }

    public Guid Id { get; }

    public int BlockedRequestCount => Volatile.Read(ref _blocked);

    private TimeSpan ActionTimeout => TimeSpan.FromSeconds(_options.Timeouts.ActionSeconds);

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _context.SetDefaultTimeout(_options.Timeouts.ActionSeconds * 1000f);
        _context.SetDefaultNavigationTimeout(_options.Timeouts.NavigationSeconds * 1000f);

        await _context.RouteAsync("**/*", HandleRouteAsync).ConfigureAwait(false);
        await _context.RouteWebSocketAsync(new Regex(".*"), webSocket => { _ = HandleWebSocketAsync(webSocket); }).ConfigureAwait(false);
        _context.Page += OnPage;
        _context.Request += OnRequest;
        _egress.Decision += OnEgressDecision;

        _page = await _context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_tempDirectory);

        Record(ExecutionLogLevel.Information, "SessionStarted", "Browser session started", new()
        {
            ["isolation"] = _options.Isolation.ToString(),
            ["mode"] = _options.Mode.ToString(),
            ["sandbox"] = _options.ChromiumSandbox ? "on" : "off",
            ["allowedHosts"] = string.Join(", ", _request.Policy.Rules.Select(r => r.ToString())),
        });
        await FlushAsync().ConfigureAwait(false);
    }

    public Task<NavigationResult> NavigateAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var redacted = LogRedactor.RedactUrl(url.OriginalString);

        var decision = RequestGuard.Evaluate(url, _request.Policy);
        if (!decision.Allowed)
        {
            RecordBlocked("pre-navigation", redacted, decision.Reason!, null);
            _ = FlushAsync();
            throw new BrowserOperationException(BrowserErrorCode.NavigationBlocked, $"Navigation to {redacted} is not allowed ({decision.Reason}).", isTransient: false);
        }

        _redirectViolation = false;
        return RunAsync("Navigate", redacted, isNavigation: true, TimeSpan.FromSeconds(_options.Timeouts.NavigationSeconds), cancellationToken: cancellationToken, action: async timeout =>
        {
            var response = await _page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { Timeout = timeout, WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
            if (response is null)
            {
                return new NavigationResult(0, LogRedactor.RedactUrl(_page.Url), []);
            }

            var chain = new List<string>();
            for (var request = response.Request.RedirectedFrom; request is not null; request = request.RedirectedFrom)
            {
                chain.Insert(0, LogRedactor.RedactUrl(request.Url));
            }

            if (_redirectViolation || response.Headers.ContainsKey(DeniedHeader))
            {
                var reason = response.Headers.TryGetValue(DeniedHeader, out var r) ? r : "redirect-hop-not-allowed";
                throw new BrowserOperationException(BrowserErrorCode.NavigationBlocked, $"Navigation to {redacted} was blocked by the egress policy ({reason}).", isTransient: false);
            }

            return new NavigationResult(response.Status, LogRedactor.RedactUrl(response.Url), chain);
        });
    }

    public Task ClickAsync(ElementSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("Click", selector.ToString(), isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: async timeout =>
        {
            await Locator(selector).ClickAsync(new LocatorClickOptions { Timeout = timeout }).ConfigureAwait(false);
            return true;
        });
    }

    public Task SubmitAsync(ElementSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("Submit", selector.ToString(), isNavigation: true, TimeSpan.FromSeconds(_options.Timeouts.NavigationSeconds), cancellationToken: cancellationToken, action: async timeout =>
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoad(object? sender, IPage page) => loaded.TrySetResult();
            _page.Load += OnLoad;
            try
            {
                _redirectViolation = false;
                await Locator(selector).ClickAsync(new LocatorClickOptions { Timeout = (float)ActionTimeout.TotalMilliseconds }).ConfigureAwait(false);
                await loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(timeout)).ConfigureAwait(false);
            }
            finally
            {
                _page.Load -= OnLoad;
            }

            if (_redirectViolation)
            {
                throw new BrowserOperationException(BrowserErrorCode.NavigationBlocked, "Submit navigation was blocked by the egress policy.", isTransient: false);
            }

            return true;
        });
    }

    /// <summary>For tests: number of open pages in this session's context.</summary>
    internal int OpenPageCountForTesting => _context.Pages.Count;

    /// <summary>For tests: element position in viewport pixels (screenshots are taken at device scale factor 1).</summary>
    internal async Task<(double X, double Y, double Width, double Height)> BoundingBoxForTestingAsync(ElementSelector selector)
    {
        var box = await Locator(selector).BoundingBoxAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("Element has no box.");
        return (box.X, box.Y, box.Width, box.Height);
    }

    public Task FillAsync(ElementSelector selector, InputValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsSensitive)
        {
            // Registered before the fill so that any failure message or screenshot is already redacted/masked.
            _sensitiveValues.Add(value.Value);
            _sensitiveSelectors.Add(selector);
        }

        return RunAsync("Fill", $"{selector} {value}", isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: async timeout =>
        {
            await Locator(selector).FillAsync(value.Value, new LocatorFillOptions { Timeout = timeout }).ConfigureAwait(false);
            return true;
        });
    }

    public Task SelectAsync(ElementSelector selector, SelectChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(choice);
        return RunAsync("Select", $"{selector} {choice}", isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: async timeout =>
        {
            var option = new SelectOptionValue { Value = choice.Value, Label = choice.Label, Index = choice.Index };
            await Locator(selector).SelectOptionAsync(option, new LocatorSelectOptionOptions { Timeout = timeout }).ConfigureAwait(false);
            return true;
        });
    }

    public Task WaitForAsync(WaitCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return RunAsync("WaitFor", condition.Kind + " " + (condition.Selector?.ToString() ?? condition.UrlPattern), isNavigation: condition.Kind is WaitKind.UrlMatches or WaitKind.LoadComplete, ActionTimeout, cancellationToken: cancellationToken, action: async timeout =>
        {
            switch (condition.Kind)
            {
                case WaitKind.UrlMatches:
                    await _page.WaitForURLAsync(condition.UrlPattern ?? throw new ArgumentException("UrlPattern is required."), new PageWaitForURLOptions { Timeout = timeout }).ConfigureAwait(false);
                    break;
                case WaitKind.LoadComplete:
                    await _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = timeout }).ConfigureAwait(false);
                    break;
                default:
                    var state = condition.Kind switch
                    {
                        WaitKind.Visible => WaitForSelectorState.Visible,
                        WaitKind.Hidden => WaitForSelectorState.Hidden,
                        WaitKind.Attached => WaitForSelectorState.Attached,
                        _ => WaitForSelectorState.Detached,
                    };
                    await Locator(condition.Selector ?? throw new ArgumentException("Selector is required.")).WaitForAsync(new LocatorWaitForOptions { State = state, Timeout = timeout }).ConfigureAwait(false);
                    break;
            }

            return true;
        });
    }

    public Task<string> GetTextAsync(ElementSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("GetText", selector.ToString(), isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: timeout =>
            Locator(selector).InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeout }));
    }

    public Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        return RunAsync("GetAttribute", $"{selector} [{attributeName}]", isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: timeout =>
            Locator(selector).GetAttributeAsync(attributeName, new LocatorGetAttributeOptions { Timeout = timeout }));
    }

    public Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.Screenshots == ScreenshotPolicy.Never)
        {
            throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, "Screenshots are disabled by configuration.", isTransient: false);
        }

        return RunAsync("Screenshot", request.Label, isNavigation: false, ActionTimeout, cancellationToken: cancellationToken, action: _ => CaptureScreenshotAsync(request, cancellationToken));
    }

    public Task<DownloadResult> DownloadAsync(ElementSelector trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!_request.AcceptDownloads)
        {
            throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, "Downloads are not enabled for this session.", isTransient: false);
        }

        if (Interlocked.Increment(ref _downloads) > _options.Downloads.MaxCount)
        {
            throw new BrowserOperationException(BrowserErrorCode.DownloadRejected, "Download count limit reached for this session.", isTransient: false);
        }

        return RunAsync("Download", trigger.ToString(), isNavigation: false, TimeSpan.FromSeconds(_options.Timeouts.DownloadSeconds), cancellationToken: cancellationToken, action: async timeout =>
        {
            var download = await _page.RunAndWaitForDownloadAsync(
                () => Locator(trigger).ClickAsync(new LocatorClickOptions { Timeout = (float)ActionTimeout.TotalMilliseconds }),
                new PageRunAndWaitForDownloadOptions { Timeout = timeout }).ConfigureAwait(false);

            var suggested = FileNameSanitizer.Sanitize(download.SuggestedFilename, "download");
            if (await download.FailureAsync().ConfigureAwait(false) is not null)
            {
                await download.DeleteAsync().ConfigureAwait(false);
                throw Rejected(suggested, "download failed");
            }

            var extension = Path.GetExtension(suggested);
            if (_options.Downloads.BlockedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                await download.DeleteAsync().ConfigureAwait(false);
                throw Rejected(suggested, $"file type {extension} is not allowed");
            }

            // Saved under a generated name inside the session's private temp directory; never opened or executed.
            var path = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
            try
            {
                await download.SaveAsAsync(path).ConfigureAwait(false);
                await download.DeleteAsync().ConfigureAwait(false);
                var size = new FileInfo(path).Length;
                if (size > _options.Downloads.MaxBytes)
                {
                    throw Rejected(suggested, $"file is larger than {_options.Downloads.MaxBytes} bytes");
                }

                var stream = File.OpenRead(path);
                await using (stream.ConfigureAwait(false))
                {
                    var artifact = await _artifacts.SaveAsync(
                        new ArtifactSaveRequest(_request.Scope, Id, ArtifactKind.Download, suggested, "application/octet-stream"),
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    Record(ExecutionLogLevel.Information, "DownloadSaved", "Download stored as artifact", new()
                    {
                        ["artifactId"] = artifact.Id.ToString(),
                        ["fileName"] = suggested,
                        ["sizeBytes"] = artifact.SizeBytes.ToString(CultureInfo.InvariantCulture),
                        ["sha256"] = artifact.Sha256,
                    });
                    return new DownloadResult(artifact, suggested);
                }
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var faulted = _crashed || _aborted;
        try
        {
            await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(_options.Timeouts.CloseSeconds)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            faulted = true;
        }

        // Revoke the proxy credentials: any late connection from this context is refused with 407.
        _egress.Decision -= OnEgressDecision;
        var bytes = _egress.BytesTransferred;
        _egress.Dispose();

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            faulted = true;
        }

        Record(ExecutionLogLevel.Information, "SessionEnded", "Browser session ended", new()
        {
            ["durationMs"] = _age.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["blockedRequests"] = BlockedRequestCount.ToString(CultureInfo.InvariantCulture),
            ["egressBytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["outcome"] = _crashed ? "crashed" : _aborted ? "aborted" : "closed",
        });
        await FlushAsync().ConfigureAwait(false);

        await _lease.ReleaseAsync(faulted).ConfigureAwait(false);
        _deadline.Dispose();
        _releaseSlot();
    }

    private async Task<T> RunAsync<T>(string operation, string description, bool isNavigation, TimeSpan timeout, Func<float, Task<T>> action, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1 || _aborted || _crashed)
        {
            throw new BrowserOperationException(BrowserErrorCode.SessionClosed, $"{operation} failed: the browser session is no longer usable.", isTransient: true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        var registration = linked.Token.Register(() => _ = AbortAsync());
        await using (registration.ConfigureAwait(false))
        {
            var blockedBefore = BlockedRequestCount;
            var stopwatch = Stopwatch.StartNew();
            var safeDescription = Redact(description);
            Record(ExecutionLogLevel.Debug, "OperationStarted", $"{operation} {safeDescription}", new() { ["operation"] = operation });
            try
            {
                var result = await action((float)timeout.TotalMilliseconds).ConfigureAwait(false);
                Record(ExecutionLogLevel.Information, "OperationCompleted", $"{operation} {safeDescription}", new()
                {
                    ["operation"] = operation,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                });
                return result;
            }
            catch (BrowserOperationException ex)
            {
                RecordFailure(operation, safeDescription, ex.Code, stopwatch);
                await CaptureFailureScreenshotAsync(operation).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Record(ExecutionLogLevel.Warning, "OperationCancelled", $"{operation} cancelled by caller", new() { ["operation"] = operation });
                    throw new OperationCanceledException($"{operation} was cancelled; the browser session was closed.", cancellationToken);
                }

                BrowserOperationException error;
                if (_deadline.IsCancellationRequested)
                {
                    error = new BrowserOperationException(BrowserErrorCode.SessionTimeout, $"{operation} aborted: the session exceeded {_options.Timeouts.SessionSeconds} s.", isTransient: true);
                }
                else
                {
                    var (code, transient) = ErrorClassifier.Classify(ex, isNavigation);
                    if (isNavigation && BlockedRequestCount > blockedBefore && code is BrowserErrorCode.NetworkError or BrowserErrorCode.NavigationBlocked)
                    {
                        (code, transient) = (BrowserErrorCode.NavigationBlocked, false);
                    }

                    if (_crashed)
                    {
                        (code, transient) = (BrowserErrorCode.BrowserCrashed, true);
                    }

                    var token = ErrorClassifier.NetworkErrorToken(ex.Message);
                    error = new BrowserOperationException(code, $"{operation} failed ({code}) for {safeDescription}{(token is null ? string.Empty : ": " + token)}.", transient);
                }

                RecordFailure(operation, safeDescription, error.Code, stopwatch);
                await CaptureFailureScreenshotAsync(operation).ConfigureAwait(false);
                throw error;
            }
            finally
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
    }

    private void RecordFailure(string operation, string description, BrowserErrorCode code, Stopwatch stopwatch) =>
        Record(ExecutionLogLevel.Warning, "OperationFailed", $"{operation} {description} failed: {code}", new()
        {
            ["operation"] = operation,
            ["errorCode"] = code.ToString(),
            ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
        });

    private async Task CaptureFailureScreenshotAsync(string operation)
    {
        if (_options.Screenshots != ScreenshotPolicy.OnFailure || _aborted || _crashed || _page.IsClosed)
        {
            return;
        }

        try
        {
            // Best effort and short: a stuck page must not delay reporting the actual failure.
            await CaptureScreenshotAsync(new ScreenshotRequest(Label: $"failure-{operation.ToLowerInvariant()}"), CancellationToken.None, FailureScreenshotTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
        {
            Record(ExecutionLogLevel.Warning, "ScreenshotFailed", "Failure screenshot could not be captured", new());
        }
    }

    private async Task<ArtifactDescriptor?> CaptureScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var masks = BuiltInMaskSelectors
            .Concat(_options.ExtraMaskSelectors)
            .Concat(_request.MaskSelectors)
            .Select(css => _page.Locator(css))
            .Concat(_sensitiveSelectors.Select(Locator))
            .ToList();

        var bytes = await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            FullPage = request.FullPage,
            Mask = masks,
            MaskColor = MaskColor,
            Animations = ScreenshotAnimations.Disabled,
            Timeout = (float)(timeout ?? ActionTimeout).TotalMilliseconds,
        }).ConfigureAwait(false);

        if (bytes.Length > _options.MaxScreenshotBytes)
        {
            Record(ExecutionLogLevel.Warning, "ScreenshotDiscarded", "Screenshot exceeded the size limit and was discarded", new()
            {
                ["sizeBytes"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
            });
            return null;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var artifact = await _artifacts.SaveAsync(
            new ArtifactSaveRequest(_request.Scope, Id, ArtifactKind.Screenshot, FileNameSanitizer.Sanitize(request.Label + ".png", "screenshot.png"), "image/png"),
            stream,
            cancellationToken).ConfigureAwait(false);
        Record(ExecutionLogLevel.Information, "ScreenshotSaved", "Screenshot stored as artifact", new()
        {
            ["artifactId"] = artifact.Id.ToString(),
            ["sizeBytes"] = artifact.SizeBytes.ToString(CultureInfo.InvariantCulture),
            ["label"] = request.Label,
        });
        return artifact;
    }

    private ILocator Locator(ElementSelector selector) => selector.Kind switch
    {
        SelectorKind.TestId => _page.GetByTestId(selector.Value),
        SelectorKind.Css => _page.Locator(selector.Value),
        SelectorKind.Role => _page.GetByRole(
            Enum.TryParse<AriaRole>(selector.Value, ignoreCase: true, out var role)
                ? role
                : throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, $"Unknown ARIA role '{selector.Value}'.", isTransient: false),
            new PageGetByRoleOptions { Name = selector.AccessibleName, Exact = selector.AccessibleName is not null }),
        SelectorKind.Text => _page.GetByText(selector.Value, new PageGetByTextOptions { Exact = true }),
        SelectorKind.Label => _page.GetByLabel(selector.Value, new PageGetByLabelOptions { Exact = true }),
        SelectorKind.Placeholder => _page.GetByPlaceholder(selector.Value, new PageGetByPlaceholderOptions { Exact = true }),
        SelectorKind.XPath => _page.Locator("xpath=" + selector.Value),
        _ => throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, "Unknown selector kind.", isTransient: false),
    };

    private async Task HandleRouteAsync(IRoute route)
    {
        try
        {
            var url = route.Request.Url;
            var decision = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? RequestGuard.Evaluate(uri, _request.Policy)
                : RequestDecision.Deny(EgressReasons.InvalidHost);
            if (decision.Allowed)
            {
                await route.ContinueAsync().ConfigureAwait(false);
                return;
            }

            RecordBlocked("route", LogRedactor.RedactUrl(url), decision.Reason!, route.Request.ResourceType);
            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Context or page already closed.
        }
    }

    private async Task HandleWebSocketAsync(IWebSocketRoute webSocket)
    {
        try
        {
            var decision = Uri.TryCreate(webSocket.Url, UriKind.Absolute, out var uri)
                ? RequestGuard.Evaluate(uri, _request.Policy)
                : RequestDecision.Deny(EgressReasons.InvalidHost);
            if (decision.Allowed)
            {
                webSocket.ConnectToServer();
                return;
            }

            RecordBlocked("websocket", LogRedactor.RedactUrl(webSocket.Url), decision.Reason!, "websocket");
            await webSocket.CloseAsync(new WebSocketRouteCloseOptions { Code = 1008, Reason = "Blocked by egress policy" }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Context or page already closed.
        }
    }

    private void OnPage(object? sender, IPage page)
    {
        if (Interlocked.Increment(ref _pageCount) > _options.MaxPagesPerSession)
        {
            Record(ExecutionLogLevel.Warning, "PopupBlocked", "Page limit reached; extra page closed", new()
            {
                ["maxPages"] = _options.MaxPagesPerSession.ToString(CultureInfo.InvariantCulture),
            });
            _ = ClosePageQuietlyAsync(page);
            return;
        }

        page.Dialog += (_, dialog) =>
        {
            // Dialog text can contain page data; only the type is recorded. Dismissing prevents hangs.
            Record(ExecutionLogLevel.Information, "DialogDismissed", "JavaScript dialog dismissed", new() { ["dialogType"] = dialog.Type });
            _ = DismissQuietlyAsync(dialog);
        };
        page.Crash += (_, _) =>
        {
            _crashed = true;
            Record(ExecutionLogLevel.Error, "BrowserCrashed", "Page crashed", new());
        };
    }

    private void OnRequest(object? sender, IRequest request)
    {
        // Route interception does not see redirect hops (spike); every hop is checked here as well. The egress
        // proxy already blocks forbidden hops at the network level; this makes the violation explicit.
        if (request.RedirectedFrom is null)
        {
            return;
        }

        var decision = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            ? RequestGuard.Evaluate(uri, _request.Policy)
            : RequestDecision.Deny(EgressReasons.InvalidHost);
        if (!decision.Allowed)
        {
            if (request.IsNavigationRequest)
            {
                _redirectViolation = true;
            }

            RecordBlocked("redirect", LogRedactor.RedactUrl(request.Url), decision.Reason!, request.ResourceType);
        }
    }

    private void OnEgressDecision(object? sender, EgressEvent egressEvent)
    {
        if (!egressEvent.Allowed)
        {
            RecordBlocked("egress-proxy", egressEvent.Target, egressEvent.Reason, null, egressEvent.Address);
        }
    }

    private void RecordBlocked(string layer, string target, string reason, string? resourceType, string? address = null)
    {
        Interlocked.Increment(ref _blocked);
        Record(ExecutionLogLevel.Warning, "RequestBlocked", $"Blocked {target} ({reason})", new()
        {
            ["layer"] = layer,
            ["target"] = target,
            ["reason"] = reason,
            ["resourceType"] = resourceType,
            ["address"] = address,
        });
    }

    private void Record(ExecutionLogLevel level, string eventType, string message, Dictionary<string, string?> properties)
    {
        var safeMessage = Redact(message);
        foreach (var key in properties.Keys.ToList())
        {
            properties[key] = properties[key] is { } value ? Redact(value) : null;
        }

        properties["sessionId"] = Id.ToString();
        var entry = new ExecutionLogEntry(_request.Scope, Id, _time.GetUtcNow(), level, eventType, safeMessage, properties);
        _pending.Enqueue(entry);

        // Events can arrive between operations (late subresources, proxy denials, popups); flush in the
        // background so security events reach the execution log promptly. Flushes are serialized, so order holds.
        _ = FlushAsync();

        var logLevel = level switch
        {
            ExecutionLogLevel.Debug => LogLevel.Debug,
            ExecutionLogLevel.Information => LogLevel.Information,
            ExecutionLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error,
        };
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["OrganizationId"] = _request.Scope.OrganizationId,
            ["JobId"] = _request.Scope.JobId,
            ["JobRecordId"] = _request.Scope.JobRecordId,
            ["AttemptNumber"] = _request.Scope.AttemptNumber,
            ["SessionId"] = Id,
        }))
        {
            _logger.Log(logLevel, "{EventType}: {Message}", eventType, safeMessage);
        }
    }

    private string Redact(string text) => LogRedactor.RedactValues(text, _sensitiveValues);

    private async Task FlushAsync()
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_pending.TryDequeue(out var entry))
            {
                try
                {
                    await _sink.WriteAsync(entry, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A failing log sink must never break or hide the browser operation's own outcome.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "Execution log sink failed for session {SessionId}", Id);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task AbortAsync()
    {
        _aborted = true;
        try
        {
            await _context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // already closed
        }
    }

    private static async Task ClosePageQuietlyAsync(IPage page)
    {
        try
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // already closed
        }
    }

    private static async Task DismissQuietlyAsync(IDialog dialog)
    {
        try
        {
            await dialog.DismissAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // page closed meanwhile
        }
    }

    private static BrowserOperationException Rejected(string fileName, string reason) =>
        new(BrowserErrorCode.DownloadRejected, $"Download '{fileName}' rejected: {reason}.", isTransient: false);
}
