using System.Collections.Concurrent;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.Tests.Journal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutomataFlow.UnitTests.Automation.Workflows;

/// <summary>A shared, ordered event log (session operations, checkpoints, completions) for ordering assertions.</summary>
public sealed class EventLog
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Add(string entry) => _events.Enqueue(entry);

    public IReadOnlyList<string> Events => [.. _events];

    public int IndexOf(string prefix) => Events.ToList().FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>A scripted <see cref="IBrowserSession"/>: no browser, every call recorded.</summary>
public sealed class FakeBrowserSession(EventLog log) : IBrowserSession
{
    public Guid Id { get; } = Guid.NewGuid();

    public int BlockedRequestCount => 0;

    public bool EgressRevoked { get; private set; }

    public bool RevokeOnDispose { get; set; } = true;

    public bool Disposed { get; private set; }

    public string CurrentUrl { get; set; } = "https://crm.example.test/";

    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);

    public RowSnapshot? Rows { get; set; }

    public int NavigateStatus { get; set; } = 200;

    /// <summary>Per-URL status overrides.</summary>
    public Dictionary<string, int> NavigateStatuses { get; } = new(StringComparer.Ordinal);

    /// <summary>Throws for an operation name (e.g. "click", "navigate").</summary>
    public Dictionary<string, Func<Exception>> Failures { get; } = new(StringComparer.Ordinal);

    public Func<string, CancellationToken, Task>? OnOperation { get; set; }

    public List<OperationOptions?> Options { get; } = [];

    public List<(string Value, ElementSelector? Selector)> Sensitive { get; } = [];

    public List<InputValue> Filled { get; } = [];

    public List<UploadFile> Uploaded { get; } = [];

    public void RegisterSensitive(string value, ElementSelector? maskSelector)
    {
        Sensitive.Add((value, maskSelector));
        log.Add("register");
    }

    private async Task Op(string name, string detail, OperationOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Options.Add(options);
        log.Add(name + ":" + detail);
        if (OnOperation is not null)
        {
            await OnOperation(name, cancellationToken);
        }

        if (Failures.TryGetValue(name, out var failure) || Failures.TryGetValue(name + ":" + detail, out failure))
        {
            throw failure();
        }
    }

    public async Task<NavigationResult> NavigateAsync(Uri url, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("navigate", url.AbsoluteUri, options, cancellationToken);
        CurrentUrl = url.AbsoluteUri;
        return new NavigationResult(NavigateStatuses.GetValueOrDefault(url.AbsoluteUri, NavigateStatus), url.GetLeftPart(UriPartial.Path), []);
    }

    public Task ClickAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => Op("click", selector.Value, options, cancellationToken);

    public Task SubmitAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => Op("submit", selector.Value, options, cancellationToken);

    public async Task FillAsync(ElementSelector selector, InputValue value, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("fill", selector.Value, options, cancellationToken);
        Filled.Add(value);
    }

    public Task SelectAsync(ElementSelector selector, SelectChoice choice, OperationOptions? options, CancellationToken cancellationToken) => Op("select", selector.Value, options, cancellationToken);

    public Task WaitForAsync(WaitCondition condition, OperationOptions? options, CancellationToken cancellationToken) => Op("wait", condition.Kind.ToString(), options, cancellationToken);

    public async Task<string> GetTextAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("text", selector.Value, options, cancellationToken);
        return Texts.TryGetValue(selector.Value, out var text) ? text : throw new BrowserOperationException(BrowserErrorCode.ElementNotFound, "Element not found.", true);
    }

    public async Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("attribute", selector.Value + "@" + attributeName, options, cancellationToken);
        return Attributes.GetValueOrDefault(selector.Value + "@" + attributeName);
    }

    public async Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("screenshot", request.Label, options, cancellationToken);
        return new ArtifactDescriptor(Guid.NewGuid(), ArtifactKind.Screenshot, "k", "shot.png", "image/png", 3, new string('0', 64), DateTimeOffset.UnixEpoch);
    }

    public async Task<DownloadResult> DownloadAsync(ElementSelector trigger, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("download", trigger.Value, options, cancellationToken);
        return new DownloadResult(new ArtifactDescriptor(Guid.NewGuid(), ArtifactKind.Download, "k", "receipt.pdf", "application/pdf", 5, new string('a', 64), DateTimeOffset.UnixEpoch), "receipt.pdf");
    }

    public async Task UploadAsync(ElementSelector selector, UploadFile file, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("upload", selector.Value, options, cancellationToken);
        Uploaded.Add(file);
    }

    public async Task<int> CountAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("count", selector.Value, options, cancellationToken);
        return Counts.GetValueOrDefault(selector.Value);
    }

    public async Task<bool> IsVisibleAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("visible", selector.Value, options, cancellationToken);
        return Visible.Contains(selector.Value);
    }

    public async Task<RowSnapshot> QueryRowsAsync(ElementSelector rows, IReadOnlyList<RowValueSpec> values, int maxRows, OperationOptions? options, CancellationToken cancellationToken)
    {
        await Op("rows", rows.Value, options, cancellationToken);
        return Rows ?? new RowSnapshot(0, []);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        EgressRevoked = RevokeOnDispose;
        log.Add("dispose");
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeSessionFactory(Func<FakeBrowserSession> create) : IBrowserSessionFactory
{
    public List<BrowserSessionRequest> Requests { get; } = [];

    public List<FakeBrowserSession> Sessions { get; } = [];

    public Task<IBrowserSession> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var session = create();
        Sessions.Add(session);
        return Task.FromResult<IBrowserSession>(session);
    }
}

/// <summary>An in-memory journal that also records checkpoint/observation/completion events in the shared log.</summary>
public class RecordingJournal(TimeProvider clock, EventLog log, TimeSpan? lease = null) : InMemoryAttemptJournal(clock, lease)
{
    public override async Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken)
    {
        await base.SideEffectStartingAsync(reservation, stepId, stepTimeout, cancellationToken);
        log.Add("checkpoint:" + stepId);
    }

    public override async Task<DateTimeOffset> MarkObservationAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        var at = await base.MarkObservationAsync(reservation, cancellationToken);
        log.Add("observe");
        return at;
    }

    public override async Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken)
    {
        var at = await base.CompleteAsync(reservation, completion, cancellationToken);
        log.Add("complete:" + completion.Outcome);
        return at;
    }
}

public sealed class FakeSecrets : ISecretProvider
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal) { ["crmPassword"] = "pw-S3cret-value" };

    public Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken) =>
        Values.TryGetValue(name, out var value) ? Task.FromResult(value) : throw new KeyNotFoundException("missing secret");
}

public sealed class FakeFiles : IExecutionFileProvider
{
    public Dictionary<string, (string Name, string ContentType, byte[] Bytes, long? DeclaredSize)> Files { get; } = new(StringComparer.Ordinal);

    public Task<ExecutionFile> OpenAsync(ExecutionScope scope, string fileReference, CancellationToken cancellationToken)
    {
        var (name, type, bytes, declared) = Files[fileReference];
        return Task.FromResult(new ExecutionFile(name, type, declared ?? bytes.Length, new MemoryStream(bytes)));
    }
}

/// <summary>Captures every formatted log message (to prove that no values are logged).</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
}

/// <summary>Builds an executor with fakes, a manual journal clock and a registered incarnation.</summary>
public sealed class ExecutorHarness
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private ExecutorHarness(InMemoryAttemptJournal journal, EventLog log, ManualClock clock, WorkerIncarnation incarnation, BrowserEngineOptions options)
    {
        Journal = journal;
        Log = log;
        Clock = clock;
        Incarnation = incarnation;
        Factory = new FakeSessionFactory(() =>
        {
            var session = new FakeBrowserSession(log);
            ConfigureSession?.Invoke(session);
            return session;
        });
        Executor = new WorkflowExecutor(new ActionRegistry(ActionRegistry.CreateDefaultHandlers()), journal, Factory, Secrets, incarnation,
            Options.Create(new WorkflowEngineOptions { ClockSkewSeconds = 1 }), Options.Create(options), TimeProvider.System, Logger, Files);
    }

    public InMemoryAttemptJournal Journal { get; }

    public EventLog Log { get; }

    public ManualClock Clock { get; }

    public WorkerIncarnation Incarnation { get; }

    public FakeSessionFactory Factory { get; }

    public FakeSecrets Secrets { get; } = new();

    public FakeFiles Files { get; } = new();

    public CapturingLogger<WorkflowExecutor> Logger { get; } = new();

    public WorkflowExecutor Executor { get; }

    public Action<FakeBrowserSession>? ConfigureSession { get; set; }

    public FakeBrowserSession Session => Factory.Sessions[^1];

    public static async Task<ExecutorHarness> CreateAsync(Func<ManualClock, EventLog, InMemoryAttemptJournal>? journal = null, BrowserEngineOptions? options = null)
    {
        var clock = new ManualClock(T0);
        var log = new EventLog();
        var j = journal?.Invoke(clock, log) ?? new RecordingJournal(clock, log, TimeSpan.FromMinutes(5));
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), "host", "boot", 1, T0, new ContainmentUnit(ContainmentKind.LinuxCgroup, "/w"), "/ledger");
        await j.RegisterIncarnationAsync(incarnation, default);
        return new ExecutorHarness(j, log, clock, incarnation, options ?? new BrowserEngineOptions { Mode = ExecutionMode.Development });
    }
}
