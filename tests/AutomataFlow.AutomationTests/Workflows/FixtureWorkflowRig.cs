using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Tests.Journal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.Workflows;

/// <summary>Runs workflows through the real executor and engine against the public fixture site (http://public.test).</summary>
internal sealed class FixtureWorkflowRig
{
    public FixtureWorkflowRig(BrowserFixture fixture, ExecutionMode mode = ExecutionMode.Development)
    {
        Fixture = fixture;
        Journal = new InMemoryAttemptJournal(Clock, TimeSpan.FromMinutes(10));
        Factory = new ObservingFactory(fixture.Engine.Factory);
        Mode = mode;
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(ExecutorLogs));
    }

    public BrowserFixture Fixture { get; }

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    public InMemoryAttemptJournal Journal { get; }

    public ObservingFactory Factory { get; }

    public ExecutionMode Mode { get; }

    public Dictionary<string, (string Name, string Type, byte[] Bytes)> Files { get; } = new(StringComparer.Ordinal);

    public Guid Organization { get; } = Guid.NewGuid();

    /// <summary>Everything the executor itself logged (leak checks).</summary>
    public CapturingLoggerProvider ExecutorLogs { get; } = new();

    private readonly ILoggerFactory _loggers;

    public static ValidatedWorkflow Workflow(string stepsJson, string inputsJson = "[]", int timeoutSeconds = 120) =>
        Validate($$$"""
            {"schemaVersion":1,"name":"fixture-flow","timeoutSeconds":{{{timeoutSeconds}}},
             "parameters":[{"name":"site","type":"url"}],"allowedHosts":["{{params.site}}"],
             "inputs":{{{inputsJson}}},"secrets":["pw"],"maskSelectors":[{"testId":"never-present"}],
             "steps":{{{stepsJson}}}}
            """);

    public static ValidatedWorkflow Validate(string json)
    {
        var result = ValidatedWorkflow.CreateForPublication(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}")));
        return result.Workflow!;
    }

    public async Task<(WorkflowExecutionResult Result, Guid Operation)> RunAsync(ValidatedWorkflow workflow, Dictionary<string, string?>? record = null, string site = "http://public.test", ExecutorTestHooks? hooks = null)
    {
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), "h", "b", 1, DateTimeOffset.UtcNow, new ContainmentUnit(ContainmentKind.LinuxCgroup, "/w/" + Guid.NewGuid().ToString("N")), "/l");
        await Journal.RegisterIncarnationAsync(incarnation, default);
        var options = new BrowserEngineOptions { Mode = Mode };
        var executor = new WorkflowExecutor(new ActionRegistry(ActionRegistry.CreateDefaultHandlers()), Journal, Factory, new Secrets(), incarnation,
            Options.Create(new WorkflowEngineOptions()), Options.Create(options), TimeProvider.System, _loggers.CreateLogger<WorkflowExecutor>(), new FileProvider(Files))
        {
            Hooks = hooks,
        };
        var operation = Guid.NewGuid();
        var request = new WorkflowExecutionRequest(new ExecutionScope(Organization, Guid.NewGuid(), Guid.NewGuid(), 0), operation, workflow, 1, workflow.DefinitionHash,
            record ?? [], new Dictionary<string, string> { ["site"] = site }, ExecutionIntent.Normal);
        return (await executor.ExecuteAsync(request, default), operation);
    }

    public const string SecretValue = "s3cret-pw-value";

    private sealed class Secrets : ISecretProvider
    {
        public Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken) => Task.FromResult(SecretValue);
    }

    private sealed class FileProvider(Dictionary<string, (string Name, string Type, byte[] Bytes)> files) : IExecutionFileProvider
    {
        public Task<ExecutionFile> OpenAsync(ExecutionScope scope, string fileReference, CancellationToken cancellationToken)
        {
            var (name, type, bytes) = files[fileReference];
            return Task.FromResult(new ExecutionFile(name, type, bytes.Length, new MemoryStream(bytes)));
        }
    }
}

/// <summary>Wraps the real factory; each session is wrapped so tests can observe element positions at screenshot time.</summary>
internal sealed class ObservingFactory(IBrowserSessionFactory inner) : IBrowserSessionFactory
{
    /// <summary>Test ids whose bounding boxes are recorded just before every screenshot.</summary>
    public List<string> TrackedTestIds { get; } = [];

    public List<Dictionary<string, (double X, double Y, double W, double H)>> BoxesPerScreenshot { get; } = [];

    public List<BrowserSessionRequest> Requests { get; } = [];

    /// <summary>Every navigation: the validated URI handed to the session and the URL Chromium reported afterwards.</summary>
    public List<(Uri Requested, string Reported)> Navigations { get; } = [];

    public async Task<IBrowserSession> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return new ObservingSession(await inner.CreateAsync(request, cancellationToken), this);
    }

    private sealed class ObservingSession(IBrowserSession inner, ObservingFactory owner) : IBrowserSession
    {
        public Guid Id => inner.Id;

        public int BlockedRequestCount => inner.BlockedRequestCount;

        public bool EgressRevoked => inner.EgressRevoked;

        public string CurrentUrl => inner.CurrentUrl;

        public void RegisterSensitive(string value, ElementSelector? maskSelector) => inner.RegisterSensitive(value, maskSelector);

        public async Task<NavigationResult> NavigateAsync(Uri url, OperationOptions? options, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.NavigateAsync(url, options, cancellationToken);
            }
            finally
            {
                owner.Navigations.Add((url, inner.CurrentUrl));
            }
        }

        public Task ClickAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => inner.ClickAsync(selector, options, cancellationToken);

        public Task SubmitAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => inner.SubmitAsync(selector, options, cancellationToken);

        public Task FillAsync(ElementSelector selector, InputValue value, OperationOptions? options, CancellationToken cancellationToken) => inner.FillAsync(selector, value, options, cancellationToken);

        public Task SelectAsync(ElementSelector selector, SelectChoice choice, OperationOptions? options, CancellationToken cancellationToken) => inner.SelectAsync(selector, choice, options, cancellationToken);

        public Task WaitForAsync(WaitCondition condition, OperationOptions? options, CancellationToken cancellationToken) => inner.WaitForAsync(condition, options, cancellationToken);

        public Task<string> GetTextAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => inner.GetTextAsync(selector, options, cancellationToken);

        public Task<string?> GetAttributeAsync(ElementSelector selector, string attributeName, OperationOptions? options, CancellationToken cancellationToken) => inner.GetAttributeAsync(selector, attributeName, options, cancellationToken);

        public async Task<ArtifactDescriptor?> ScreenshotAsync(ScreenshotRequest request, OperationOptions? options, CancellationToken cancellationToken)
        {
            var boxes = new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);
            foreach (var testId in owner.TrackedTestIds)
            {
                var box = await ((BrowserSession)inner).BoundingBoxForTestingAsync(ElementSelector.TestId(testId));
                boxes[testId] = (box.X, box.Y, box.Width, box.Height);
            }

            owner.BoxesPerScreenshot.Add(boxes);
            return await inner.ScreenshotAsync(request, options, cancellationToken);
        }

        public Task<DownloadResult> DownloadAsync(ElementSelector trigger, OperationOptions? options, CancellationToken cancellationToken) => inner.DownloadAsync(trigger, options, cancellationToken);

        public Task UploadAsync(ElementSelector selector, UploadFile file, OperationOptions? options, CancellationToken cancellationToken) => inner.UploadAsync(selector, file, options, cancellationToken);

        public Task<int> CountAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => inner.CountAsync(selector, options, cancellationToken);

        public Task<bool> IsVisibleAsync(ElementSelector selector, OperationOptions? options, CancellationToken cancellationToken) => inner.IsVisibleAsync(selector, options, cancellationToken);

        public Task<RowSnapshot> QueryRowsAsync(ElementSelector rows, IReadOnlyList<RowValueSpec> values, int maxRows, OperationOptions? options, CancellationToken cancellationToken) =>
            inner.QueryRowsAsync(rows, values, maxRows, options, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
