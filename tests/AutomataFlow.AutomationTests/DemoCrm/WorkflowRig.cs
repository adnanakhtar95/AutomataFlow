using System.Text.Json.Nodes;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Tests.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.DemoCrm;

/// <summary>
/// Drives the real executor (real Chromium, real egress proxy, sandbox on) against the separately running DemoCrm, with
/// the test-only in-memory journal shared across attempts and Workers, exactly as Phase 5 will call the engine.
/// </summary>
internal sealed class WorkflowRig
{
    private static int _sequence;

    public WorkflowRig(BrowserFixture fixture, TimeProvider? journalClock = null, TimeSpan? lease = null)
    {
        Fixture = fixture;
        Clock = journalClock ?? new ManualClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        Journal = new InMemoryAttemptJournal(Clock, lease ?? TimeSpan.FromMinutes(10));
    }

    public BrowserFixture Fixture { get; }

    public TimeProvider Clock { get; }

    public ManualClock Manual => (ManualClock)Clock;

    public InMemoryAttemptJournal Journal { get; }

    public Guid Organization { get; } = Guid.NewGuid();

    public static string SampleJson => File.ReadAllText(Path.Combine(DemoCrmProcess.RepositoryRoot, "docs", "workflows", "democrm-register-customer.json"));

    public static ValidatedWorkflow Sample => Validate(SampleJson);

    /// <summary>Variant B: no idempotency, no operation-id fill, advisory reconcile by business key (exact email).</summary>
    public static string VariantB => Mutate(SampleJson, r =>
    {
        var steps = (JsonArray)r["steps"]!;
        steps.Remove(steps.Single(s => (string?)s!["id"] == "operation"));
        steps.Single(s => (string?)s!["id"] == "submit")!.AsObject().Remove("idempotency");
        r["reconciliation"] = JsonNode.Parse("""{"evidence":"advisory","matchKey":"businessKey"}""");
        r["reconcile"]![0]!["config"]!["url"] = "{{params.crmBaseUrl}}/Customers/Search?Field=Email&Term={{record.email}}";
        var match = r["reconcile"]![1]!["config"]!;
        match["key"] = JsonNode.Parse("""{"text":{"testId":"customer-email"}}""");
        match["equals"] = "{{record.email}}";
    });

    /// <summary>Variant C: B with authoritative evidence and DemoCrm's documented commit-latency contract.</summary>
    public static string VariantC => Mutate(VariantB, r => r["reconciliation"] = JsonNode.Parse(
        """{"evidence":"authoritative","matchKey":"businessKey","authority":{"maxCommitLatencySeconds":10,"contractReference":"docs/DEMO_CRM.md commit-latency contract","maxEvidenceAgeSeconds":60}}"""));

    /// <summary>Variant D: C with a 5 s submit deadline (shorter than DemoCrm's 7 s late commit).</summary>
    public static string VariantD(bool authoritative) => Mutate(authoritative ? VariantC : VariantB,
        r => ((JsonArray)r["steps"]!).Single(s => (string?)s!["id"] == "submit")!["timeoutMs"] = 5000);

    public static ValidatedWorkflow Validate(string json)
    {
        var result = ValidatedWorkflow.CreateForPublication(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Path} {e.Code}")));
        return result.Workflow!;
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    public static Dictionary<string, string?> Record(string emailLocal, string tag = "")
    {
        var n = Interlocked.Increment(ref _sequence);
        return new Dictionary<string, string?>
        {
            ["firstName"] = "Grace",
            ["lastName"] = "Hopper",
            ["email"] = $"{emailLocal}{(tag.Length == 0 ? string.Empty : "+" + tag)}@example.test",
            ["phone"] = $"+1 555 {7000 + n}",
            ["company"] = "Fictional Compilers",
            ["country"] = "Canada",
            ["customerType"] = "Premium",
        };
    }

    public async Task<(WorkflowExecutor Executor, WorkerIncarnation Incarnation)> NewWorkerAsync()
    {
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), "test-host", "boot-1", Environment.ProcessId, DateTimeOffset.UtcNow,
            new ContainmentUnit(ContainmentKind.LinuxCgroup, "/workers/" + Guid.NewGuid().ToString("N")), "/ledger");
        await Journal.RegisterIncarnationAsync(incarnation, default);
        var options = new BrowserEngineOptions { Mode = ExecutionMode.Development, DevelopmentPrivateNetworkOrigins = [Fixture.DemoCrm.BaseUrl] };
        var executor = new WorkflowExecutor(new ActionRegistry(ActionRegistry.CreateDefaultHandlers()), Journal, Fixture.Engine.Factory, new Secrets(), incarnation,
            Options.Create(new WorkflowEngineOptions { ClockSkewSeconds = 1 }), Options.Create(options), TimeProvider.System, NullLogger<WorkflowExecutor>.Instance);
        return (executor, incarnation);
    }

    public WorkflowExecutionRequest Request(ValidatedWorkflow workflow, Guid operation, Dictionary<string, string?> record, ExecutionIntent intent = ExecutionIntent.Normal) =>
        new(new ExecutionScope(Organization, Guid.NewGuid(), Guid.NewGuid(), 0), operation, workflow, 1, workflow.DefinitionHash, record,
            new Dictionary<string, string> { ["crmBaseUrl"] = Fixture.DemoCrm.BaseUrl, ["crmUser"] = "agent@democrm.test" }, intent);

    public RepeatDecision Decide(ValidatedWorkflow current, Guid operation) =>
        RepeatSafetyEvaluator.Evaluate(current, operation, Journal.History(Organization, operation), Clock.GetUtcNow(), RepeatSafetyPolicy.Create(1, 3, 3));

    public Task<int> PostsAsync() => Fixture.DemoCrm.RegistrationSubmissionsAsync();

    public Task<int> CustomersAsync() => Fixture.DemoCrm.CustomerCountAsync();

    private sealed class Secrets : ISecretProvider
    {
        public Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken) =>
            name == "crmPassword" ? Task.FromResult("DemoPass-2026!") : throw new KeyNotFoundException("unknown secret");
    }
}
