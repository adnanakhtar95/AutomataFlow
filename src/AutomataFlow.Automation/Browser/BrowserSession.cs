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
/// Every operation accepts optional <see cref="OperationOptions"/>; their timeout is ONE total deadline for the whole
/// operation (ADR-053). The two-argument forms keep the configured per-operation defaults.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    Guid Id { get; }

    /// <summary>Requests blocked by any layer (route guard, redirect inspection or egress proxy).</summary>
    int BlockedRequestCount { get; }

    /// <summary>
    /// True only after <see cref="IAsyncDisposable.DisposeAsync"/> ran the egress revocation barrier to COMPLETION
    /// (ADR-060 B1-B5): admission closed, every admitted upstream write finished, every upstream socket aborted, every
    /// browser-side socket closed and every proxy handler finished. A timed-out barrier leaves this false. This proves
    /// connection destruction (E2) only; it never proves physical departure of queued packets (E3) and never proves that
    /// an already-dispatched request failed.
    /// </summary>
    bool EgressRevoked { get; }

    /// <summary>
    /// The page's current URL, unredacted, for condition evaluation (urlMatches). It is never logged; reading it
    /// records a probe event with the redacted URL only.
    /// </summary>
    string CurrentUrl { get; }

    /// <summary>Registers a value (e.g. a sensitive extract) for redaction and, optionally, its element for screenshot masking.</summary>
    void RegisterSensitive(string value, ElementSelector? maskSelector);

    Task<NavigationResult> NavigateAsync(Uri url, OperationOptions? options, CancellationToken cancellationToken);

    Task ClickAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken);

    /// <summary>
    /// Clicks an element that triggers a navigation (e.g. a form submit) and waits until the NEW document has
    /// loaded, so later reads never race with the old page (even when the URL does not change). With options, the
    /// click and the load wait share one total deadline.
    /// </summary>
    Task SubmitAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken);

    Task FillAsync(ElementSelector selector, InputValue value, OperationOptions? options, CancellationToken cancellationToken);

    Task SelectAsync(ElementSelector selector, SelectChoice choice, OperationOptions? options, CancellationToken cancellationToken);

    Task WaitForAsync(WaitCondition condition, OperationOptions? options, CancellationToken cancellationToken);

    Task<string> GetTextAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken);

    Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, OperationOptions? options, CancellationToken cancellationToken);

    Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, OperationOptions? options, CancellationToken cancellationToken);

    Task<DownloadResult> DownloadAsync(ElementSelector trigger, OperationOptions? options, CancellationToken cancellationToken);

    /// <summary>Sets a file input from an in-memory payload. Never accepts a filesystem path (ADR-038).</summary>
    Task UploadAsync(ElementSelector selector, UploadFile file, OperationOptions? options, CancellationToken cancellationToken);

    /// <summary>Immediate count of matching elements; no auto-wait; 0 for missing elements (never throws for them).</summary>
    Task<int> CountAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken);

    /// <summary>Immediate visibility check; false for missing elements; ElementNotUnique for several matches.</summary>
    Task<bool> IsVisibleAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="maxRows"/> rows (1..50) with element reads only, never script evaluation. If more
    /// rows match, only the count is returned (<see cref="RowSnapshot.Truncated"/>). Not atomic: the page may change
    /// between reads.
    /// </summary>
    Task<RowSnapshot> QueryRowsAsync(ElementSelector rows, IReadOnlyList<RowValueSpec> values, int maxRows, OperationOptions? options, CancellationToken cancellationToken);

    Task<NavigationResult> NavigateAsync(Uri url, CancellationToken cancellationToken) => NavigateAsync(url, null, cancellationToken);

    Task ClickAsync(ElementSelector selector, CancellationToken cancellationToken) => ClickAsync(selector, null, cancellationToken);

    Task SubmitAsync(ElementSelector selector, CancellationToken cancellationToken) => SubmitAsync(selector, null, cancellationToken);

    Task FillAsync(ElementSelector selector, InputValue value, CancellationToken cancellationToken) => FillAsync(selector, value, null, cancellationToken);

    Task SelectAsync(ElementSelector selector, SelectChoice choice, CancellationToken cancellationToken) => SelectAsync(selector, choice, null, cancellationToken);

    Task WaitForAsync(WaitCondition condition, CancellationToken cancellationToken) => WaitForAsync(condition, null, cancellationToken);

    Task<string> GetTextAsync(ElementSelector selector, CancellationToken cancellationToken) => GetTextAsync(selector, null, cancellationToken);

    Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, CancellationToken cancellationToken) => GetAttributeAsync(selector, attributeName, null, cancellationToken);

    Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken) => ScreenshotAsync(request, null, cancellationToken);

    Task<DownloadResult> DownloadAsync(ElementSelector trigger, CancellationToken cancellationToken) => DownloadAsync(trigger, null, cancellationToken);
}

/// <summary>
/// The time budget of one operation. With an explicit <see cref="OperationOptions.Timeout"/> it is ONE total deadline
/// shared by every sub-step (ADR-053); otherwise each sub-step keeps its configured default (Phase 3 behaviour).
/// </summary>
internal sealed class OperationBudget
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public OperationBudget(TimeSpan total, bool isExplicit)
    {
        Total = total;
        IsExplicit = isExplicit;
    }

    public TimeSpan Total { get; }

    public bool IsExplicit { get; }

    public TimeSpan Remaining
    {
        get
        {
            var left = Total - _clock.Elapsed;
            return left > TimeSpan.Zero ? left : throw new TimeoutException("The operation deadline was exceeded.");
        }
    }

    /// <summary>Non-throwing variant of <see cref="Remaining"/>: false once the total budget is exhausted.</summary>
    public bool TryGetRemaining(out TimeSpan remaining)
    {
        remaining = Total - _clock.Elapsed;
        return remaining > TimeSpan.Zero;
    }

    /// <summary>Timeout for the next sub-step: the remaining total budget, or <paramref name="legacy"/> without explicit options.</summary>
    public float NextMs(TimeSpan legacy) => (float)Math.Max(1, (IsExplicit ? Remaining : legacy).TotalMilliseconds);
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
    private static readonly TimeSpan MinimumFailureScreenshotBudget = TimeSpan.FromMilliseconds(250);

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
    private int _egressRevoked;
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

    private TimeSpan NavigationTimeout => TimeSpan.FromSeconds(_options.Timeouts.NavigationSeconds);

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

    public Task<NavigationResult> NavigateAsync(Uri url, OperationOptions? options, CancellationToken cancellationToken)
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
        return RunAsync("Navigate", redacted, isNavigation: true, NavigationTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var response = await _page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { Timeout = budget.NextMs(NavigationTimeout), WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
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

    public Task ClickAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("Click", selector.ToString(), isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            await Locator(selector).ClickAsync(new LocatorClickOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
            return true;
        });
    }

    public Task SubmitAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);

        // Without options: click (ActionSeconds) + load wait (NavigationSeconds), as in Phase 3. With options: both
        // sub-steps share ONE total deadline (ADR-053), so a submit can never take longer than its step timeout.
        return RunAsync("Submit", selector.ToString(), isNavigation: true, ActionTimeout + NavigationTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoad(object? sender, IPage page) => loaded.TrySetResult();
            _page.Load += OnLoad;
            try
            {
                _redirectViolation = false;
                await Locator(selector).ClickAsync(new LocatorClickOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
                await loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(budget.NextMs(NavigationTimeout))).ConfigureAwait(false);
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

    /// <summary>For tests: the egress session backing this browser session.</summary>
    internal EgressSession EgressForTesting => _egress;

    public bool EgressRevoked => Volatile.Read(ref _egressRevoked) == 1;

    public string CurrentUrl
    {
        get
        {
            var url = _page.Url;
            Record(ExecutionLogLevel.Debug, "ProbeCurrentUrl", "Current URL read", new() { ["url"] = LogRedactor.RedactUrl(url) });
            return url;
        }
    }

    public void RegisterSensitive(string value, ElementSelector? maskSelector)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0)
        {
            _sensitiveValues.Add(value);
        }

        if (maskSelector is not null)
        {
            _sensitiveSelectors.Add(maskSelector);
        }
    }

    public Task FillAsync(ElementSelector selector, InputValue value, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsSensitive)
        {
            // Registered before the fill so that any failure message or screenshot is already redacted/masked.
            _sensitiveValues.Add(value.Value);
            _sensitiveSelectors.Add(selector);
        }

        return RunAsync("Fill", $"{selector} {value}", isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            await Locator(selector).FillAsync(value.Value, new LocatorFillOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
            return true;
        });
    }

    public Task SelectAsync(ElementSelector selector, SelectChoice choice, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(choice);
        return RunAsync("Select", $"{selector} {choice}", isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var option = new SelectOptionValue { Value = choice.Value, Label = choice.Label, Index = choice.Index };
            await Locator(selector).SelectOptionAsync(option, new LocatorSelectOptionOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
            return true;
        });
    }

    public Task WaitForAsync(WaitCondition condition, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return RunAsync("WaitFor", condition.Kind + " " + (condition.Selector?.ToString() ?? condition.UrlPattern), isNavigation: condition.Kind is WaitKind.UrlMatches or WaitKind.LoadComplete, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var timeout = budget.NextMs(ActionTimeout);
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

    public Task<string> GetTextAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("GetText", selector.ToString(), isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: budget =>
            Locator(selector).InnerTextAsync(new LocatorInnerTextOptions { Timeout = budget.NextMs(ActionTimeout) }));
    }

    public Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        return RunAsync("GetAttribute", $"{selector} [{attributeName}]", isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: budget =>
            Locator(selector).GetAttributeAsync(attributeName, new LocatorGetAttributeOptions { Timeout = budget.NextMs(ActionTimeout) }));
    }

    public Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.Screenshots == ScreenshotPolicy.Never)
        {
            throw new BrowserOperationException(BrowserErrorCode.PolicyViolation, "Screenshots are disabled by configuration.", isTransient: false);
        }

        return RunAsync("Screenshot", request.Label, isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: budget =>
            CaptureScreenshotAsync(request, cancellationToken, TimeSpan.FromMilliseconds(budget.NextMs(ActionTimeout))));
    }

    public Task<DownloadResult> DownloadAsync(ElementSelector trigger, OperationOptions? options, CancellationToken cancellationToken)
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

        var downloadTimeout = TimeSpan.FromSeconds(_options.Timeouts.DownloadSeconds);
        return RunAsync("Download", trigger.ToString(), isNavigation: false, downloadTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var waitTimeout = budget.NextMs(downloadTimeout);
            var download = await _page.RunAndWaitForDownloadAsync(
                () => Locator(trigger).ClickAsync(new LocatorClickOptions { Timeout = budget.NextMs(ActionTimeout) }),
                new PageRunAndWaitForDownloadOptions { Timeout = waitTimeout }).ConfigureAwait(false);

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

    public Task UploadAsync(ElementSelector selector, UploadFile file, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(file);

        // Content-only (ADR-038): the payload comes from memory and the browser only ever sees the sanitized last
        // segment of the name. A filesystem path is never passed to Playwright (source-scanned).
        var name = FileNameSanitizer.Sanitize(file.FileName, "upload");
        if (file.Content.Length > _options.Uploads.MaxBytes)
        {
            throw new BrowserOperationException(BrowserErrorCode.UploadRejected, $"Upload '{name}' rejected: it is larger than {_options.Uploads.MaxBytes} bytes.", isTransient: false);
        }

        var payload = new FilePayload { Name = name, MimeType = file.ContentType, Buffer = file.Content.ToArray() };
        return RunAsync("Upload", $"{selector} [{name}, {file.Content.Length} bytes]", isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            await Locator(selector).SetInputFilesAsync(payload, new LocatorSetInputFilesOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
            return true;
        });
    }

    public Task<int> CountAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("Count", selector.ToString(), isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: _ =>
            Locator(selector).CountAsync());
    }

    public Task<bool> IsVisibleAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return RunAsync("IsVisible", selector.ToString(), isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async _ =>
        {
            // Immediate (no auto-wait): count first so a missing element is "not visible" rather than an error, and
            // several matches are a strict-mode failure (ElementNotUnique) rather than an arbitrary pick.
            var locator = Locator(selector);
            var count = await locator.CountAsync().ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            if (count > 1)
            {
                throw new BrowserOperationException(BrowserErrorCode.ElementNotUnique, $"IsVisible matched {count} elements for {Redact(selector.ToString())}.", isTransient: false);
            }

            return await locator.IsVisibleAsync().ConfigureAwait(false);
        });
    }

    public Task<RowSnapshot> QueryRowsAsync(ElementSelector rows, IReadOnlyList<RowValueSpec> values, int maxRows, OperationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);
        if (maxRows is < 1 or > RowSnapshot.MaxRowsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, $"maxRows must be between 1 and {RowSnapshot.MaxRowsLimit}.");
        }

        if (values.Count == 0 || values.Any(v => v is null))
        {
            throw new ArgumentException("At least one non-null row value is required.", nameof(values));
        }

        var description = $"{rows} [{string.Join(", ", values)}] max {maxRows}";
        return RunAsync("QueryRows", description, isNavigation: false, ActionTimeout, options, cancellationToken: cancellationToken, action: async budget =>
        {
            var locator = Locator(rows);
            var count = await locator.CountAsync().ConfigureAwait(false);
            if (count > maxRows)
            {
                return new RowSnapshot(count, []);
            }

            var result = new List<IReadOnlyList<string?>>(count);
            for (var i = 0; i < count; i++)
            {
                var row = locator.Nth(i);
                var rowValues = new List<string?>(values.Count);
                foreach (var spec in values)
                {
                    rowValues.Add(await ReadRowValueAsync(row, spec, budget).ConfigureAwait(false));
                }

                result.Add(rowValues);
            }

            return new RowSnapshot(count, result);
        });
    }

    private async Task<string?> ReadRowValueAsync(ILocator row, RowValueSpec spec, OperationBudget budget)
    {
        if (spec.Kind == RowValueKind.Attribute)
        {
            return await row.GetAttributeAsync(spec.Attribute!, new LocatorGetAttributeOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
        }

        var child = Locator(spec.Child!, row);
        var matches = await child.CountAsync().ConfigureAwait(false);
        if (matches == 0)
        {
            return null;
        }

        if (matches > 1)
        {
            throw new BrowserOperationException(BrowserErrorCode.ElementNotUnique, $"Row child {Redact(spec.Child!.ToString())} matched {matches} elements.", isTransient: false);
        }

        return await child.InnerTextAsync(new LocatorInnerTextOptions { Timeout = budget.NextMs(ActionTimeout) }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var faulted = _crashed || _aborted;

        // Revocation barrier FIRST (ADR-060), before the browser context is closed, so nothing can leave through the
        // proxy while a slow close is in progress. EgressRevoked is true only for a COMPLETED barrier (E2 proven).
        var revocationClock = Stopwatch.StartNew();
        var revocation = await _egress.RevokeAsync().ConfigureAwait(false);
        Volatile.Write(ref _egressRevoked, revocation == RevocationResult.Completed ? 1 : 0);
        _egress.Decision -= OnEgressDecision;
        var bytes = _egress.BytesTransferred;
        Record(revocation == RevocationResult.Completed ? ExecutionLogLevel.Information : ExecutionLogLevel.Error, "EgressRevocation", "Egress revocation barrier " + revocation, new()
        {
            ["result"] = revocation.ToString(),
            ["durationMs"] = revocationClock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
        });

        try
        {
            await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(_options.Timeouts.CloseSeconds)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            faulted = true;
        }

        Record(ExecutionLogLevel.Debug, "ContextClosed", "Browser context closed", new());

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

    private async Task<T> RunAsync<T>(string operation, string description, bool isNavigation, TimeSpan defaultBudget, OperationOptions? options, Func<OperationBudget, Task<T>> action, CancellationToken cancellationToken)
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
            var budget = new OperationBudget(options?.Timeout ?? defaultBudget, options?.Timeout is not null);
            try
            {
                var work = action(budget);

                // With an explicit total deadline, our wait ends at the deadline even if a sub-step overran it. Work
                // already started in the browser (e.g. a POST) may still complete; that is why side-effect steps end
                // Indeterminate on timeout (ADR-051/053).
                var result = budget.IsExplicit
                    ? await work.WaitAsync(budget.Total, CancellationToken.None).ConfigureAwait(false)
                    : await work.ConfigureAwait(false);
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
                await CaptureFailureScreenshotAsync(operation, budget).ConfigureAwait(false);
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
                await CaptureFailureScreenshotAsync(operation, budget).ConfigureAwait(false);
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

    private async Task CaptureFailureScreenshotAsync(string operation, OperationBudget budget)
    {
        if (_options.Screenshots != ScreenshotPolicy.OnFailure || _aborted || _crashed || _page.IsClosed)
        {
            return;
        }

        // With an explicit total deadline (ADR-053, audit defect D2) the failure screenshot is part of the operation:
        // it is skipped when too little budget remains and otherwise bounded by what remains, so the operation never
        // returns after its deadline because of diagnostics. Without explicit options the Phase 3 behaviour is unchanged.
        var timeout = FailureScreenshotTimeout;
        if (budget.IsExplicit)
        {
            if (!budget.TryGetRemaining(out var remaining) || remaining < MinimumFailureScreenshotBudget)
            {
                Record(ExecutionLogLevel.Information, "ScreenshotSkipped", "Failure screenshot skipped: the operation deadline is exhausted", new());
                return;
            }

            timeout = remaining < timeout ? remaining : timeout;
        }

        try
        {
            // Best effort and short: a stuck page must not delay reporting the actual failure.
            var capture = CaptureScreenshotAsync(new ScreenshotRequest(Label: $"failure-{operation.ToLowerInvariant()}"), CancellationToken.None, timeout);
            await (budget.IsExplicit ? capture.WaitAsync(timeout, CancellationToken.None) : capture).ConfigureAwait(false);
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
            ParseRole(selector),
            new PageGetByRoleOptions { Name = selector.AccessibleName, Exact = selector.AccessibleName is not null }),
        SelectorKind.Text => _page.GetByText(selector.Value, new PageGetByTextOptions { Exact = true }),
        SelectorKind.Label => _page.GetByLabel(selector.Value, new PageGetByLabelOptions { Exact = true }),
        SelectorKind.Placeholder => _page.GetByPlaceholder(selector.Value, new PageGetByPlaceholderOptions { Exact = true }),
        SelectorKind.XPath => _page.Locator("xpath=" + selector.Value),
        _ => throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, "Unknown selector kind.", isTransient: false),
    };

    /// <summary>The same typed selector mapping, scoped to the descendants of <paramref name="scope"/> (row children).</summary>
    private static ILocator Locator(ElementSelector selector, ILocator scope) => selector.Kind switch
    {
        SelectorKind.TestId => scope.GetByTestId(selector.Value),
        SelectorKind.Css => scope.Locator(selector.Value),
        SelectorKind.Role => scope.GetByRole(
            ParseRole(selector),
            new LocatorGetByRoleOptions { Name = selector.AccessibleName, Exact = selector.AccessibleName is not null }),
        SelectorKind.Text => scope.GetByText(selector.Value, new LocatorGetByTextOptions { Exact = true }),
        SelectorKind.Label => scope.GetByLabel(selector.Value, new LocatorGetByLabelOptions { Exact = true }),
        SelectorKind.Placeholder => scope.GetByPlaceholder(selector.Value, new LocatorGetByPlaceholderOptions { Exact = true }),
        // Defence in depth (D3): RowValueSpec already rejects XPath children; XPath can escape the row scope.
        SelectorKind.XPath => throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, "XPath selectors cannot be scoped to a row.", isTransient: false),
        _ => throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, "Unknown selector kind.", isTransient: false),
    };

    private static AriaRole ParseRole(ElementSelector selector) =>
        Enum.TryParse<AriaRole>(selector.Value, ignoreCase: true, out var role)
            ? role
            : throw new BrowserOperationException(BrowserErrorCode.InvalidSelector, $"Unknown ARIA role '{selector.Value}'.", isTransient: false);

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
