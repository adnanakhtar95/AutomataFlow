using System.Text.Json.Nodes;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Tests.Journal;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Automation.Workflows;

/// <summary>Phase 5 step 6: an attempt longer than the journal lease keeps it alive; a failed renewal stops the attempt.</summary>
public class LeaseRenewalTests
{
    /// <summary>No side effect: a 3 s delay, then a terminal success. The journal lease is only 1.5 s.</summary>
    private static readonly string Slow = With(Full, r =>
    {
        r["steps"] = new JsonArray(
            JsonNode.Parse("""{"id":"wait","type":"delay","config":{"milliseconds":3000}}"""),
            JsonNode.Parse("""{"id":"done","type":"captureResult","config":{"outcome":{"status":"succeeded"}}}"""));
        r.Remove("reconciliation");
        r.Remove("reconcile");
    });

    private static WorkflowExecutionRequest Request(Guid operation) =>
        new(new ExecutionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0), operation, Valid(Slow), 1, Valid(Slow).DefinitionHash,
            new Dictionary<string, string?> { ["email"] = "ada.lovelace@example.test", ["taxId"] = "TX-998877" },
            new Dictionary<string, string> { ["crmBaseUrl"] = "https://crm.example.test", ["crmUser"] = "agent" }, ExecutionIntent.Normal);

    private sealed class FailingRenewalJournal(TimeProvider clock, EventLog log) : RecordingJournal(clock, log, TimeSpan.FromMilliseconds(1500))
    {
        public override Task<DateTimeOffset> RenewLeaseAsync(AttemptReservation reservation, CancellationToken cancellationToken) =>
            throw new AttemptJournalException(JournalErrorCodes.Fenced, "lease lost");
    }

    [Fact]
    public async Task An_attempt_longer_than_the_lease_renews_it_and_completes_in_time()
    {
        var harness = await ExecutorHarness.CreateAsync((_, log) => new RecordingJournal(TimeProvider.System, log, TimeSpan.FromMilliseconds(1500)));
        var operation = Guid.NewGuid();
        var request = Request(operation);

        var result = await harness.Executor.ExecuteAsync(request, default);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        var entry = Assert.Single(harness.Journal.History(request.Scope.OrganizationId, operation));
        Assert.Equal(AttemptState.Completed, entry.State); // never abandoned: the lease was renewed while the attempt ran
    }

    [Fact]
    public async Task A_failed_renewal_stops_the_attempt()
    {
        var harness = await ExecutorHarness.CreateAsync((_, log) => new FailingRenewalJournal(TimeProvider.System, log));
        var started = DateTimeOffset.UtcNow;

        var result = await harness.Executor.ExecuteAsync(Request(Guid.NewGuid()), default);

        Assert.Equal(WorkflowOutcome.Cancelled, result.Outcome); // no side effect started: Cancelled, never Succeeded
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2.9), "the attempt stopped at the first failed renewal, before the 3 s step ended");
        Assert.Contains(harness.Logger.Messages, l => l.Contains("lease renewal failed", StringComparison.Ordinal));
    }
}
