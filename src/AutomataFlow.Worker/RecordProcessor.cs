using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Worker;

/// <summary>
/// Runs one claimed record (Phase 5 step 6): loads it inside its organization's scope, re-verifies the pinned definition's
/// hash, executes one attempt with the real engine (whose admission guard re-evaluates the journal), then evaluates the
/// journal's fresh history with the <see cref="RepeatSafetyEvaluator"/> and applies the <see cref="RecordScheduler"/>'s
/// transition — only while this Worker still holds the claim. A cancelled job is never executed again.
/// </summary>
public sealed class RecordProcessor(
    IServiceScopeFactory scopes,
    Lazy<IWorkflowExecutor> executor,
    IAttemptJournal journal,
    ExecutionLogBuffer logs,
    WorkerIncarnationHolder incarnation,
    IOptions<WorkerOptions> options,
    IOptions<WorkflowEngineOptions> engine,
    TimeProvider clock,
    ILogger<RecordProcessor> logger)
{
    public async Task ProcessAsync(ClaimedRecord claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var self = incarnation.Incarnation.Id.Value;
        await using var scope = scopes.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AttemptRecorder>();
        var work = await recorder.LoadAsync(claim, cancellationToken);
        if (work is null)
        {
            return;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["OrganizationId"] = claim.OrganizationId, ["JobId"] = claim.JobId, ["JobRecordId"] = claim.Id });
        var policy = Policy(work.Job.Options);
        var loaded = ValidatedWorkflow.Load(work.Version.DefinitionJson, work.Version.DefinitionHash);
        if (loaded.Workflow is not { } workflow)
        {
            logger.LogError("Pinned workflow version failed hash verification {WorkflowVersionId}", work.Version.Id);
            await FinishAsync(recorder, claim, self, null, new RecordTransition(JobRecordStatus.Failed, claim.NextIntent, null, null, EngineErrorCodes.WorkflowTampered, false), [], cancellationToken);
            return;
        }

        if (work.Job.CancelRequested)
        {
            var (history, now) = await ReadAsync(claim);
            var decision = history is { } h ? RepeatSafetyEvaluator.Evaluate(workflow, claim.OperationId, h, now, engine.Value.ToPolicy()) : null;
            await FinishAsync(recorder, claim, self, null,
                RecordScheduler.Next(null, decision, NormalAttempts(history), claim.NextIntent, policy, cancelRequested: true, claim.AwaitingFenceSince, now), [], cancellationToken);
            return;
        }

        var request = new WorkflowExecutionRequest(new ExecutionScope(claim.OrganizationId, claim.JobId, claim.Id, 0), claim.OperationId, workflow,
            work.Version.VersionNumber, work.Version.DefinitionHash, work.Values, work.Parameters, claim.NextIntent == RecordIntent.Reconcile ? ExecutionIntent.Reconcile : ExecutionIntent.Normal);
        var result = await executor.Value.ExecuteAsync(request, cancellationToken);
        var entries = result.AttemptNumber is { } number ? logs.Drain(claim.OrganizationId, claim.Id, number) : [];
        logs.Discard(claim.OrganizationId, claim.Id);

        var (after, at) = await ReadAsync(claim);
        var next = after is { } a ? RepeatSafetyEvaluator.Evaluate(workflow, claim.OperationId, a, at, engine.Value.ToPolicy()) : null;
        var cancelled = await recorder.IsCancelRequestedAsync(claim, CancellationToken.None);
        var transition = RecordScheduler.Next(result, next, NormalAttempts(after), claim.NextIntent, policy, cancelled, claim.AwaitingFenceSince, at);
        logger.LogInformation("Record attempt finished {Outcome} {ErrorCode} {NextStatus} {NextIntent} {Decision}",
            result.Outcome, result.Error?.Code, transition.Status, transition.NextIntent, next?.Kind);
        await FinishAsync(recorder, claim, self, result, transition, entries, CancellationToken.None);
    }

    private SchedulerPolicy Policy(JobOptions job)
    {
        var o = options.Value;
        return new SchedulerPolicy(job.MaxAttempts, TimeSpan.FromSeconds(job.RetryBackoffSeconds), TimeSpan.FromSeconds(o.FenceWaitSeconds),
            TimeSpan.FromSeconds(o.FenceRecheckSeconds), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    private async Task<(System.Collections.Immutable.ImmutableArray<AttemptSummary>? History, DateTimeOffset Now)> ReadAsync(ClaimedRecord claim)
    {
        try
        {
            var (history, now) = await journal.ReadHistoryAsync(claim.OrganizationId, claim.OperationId, CancellationToken.None);
            return (history, now);
        }
        catch (AttemptJournalException)
        {
            return (null, clock.GetUtcNow());
        }
    }

    private static int NormalAttempts(System.Collections.Immutable.ImmutableArray<AttemptSummary>? history) =>
        history?.Count(h => h.Intent == ExecutionIntent.Normal && h.State != AttemptState.Reserved) ?? 0;

    private async Task FinishAsync(AttemptRecorder recorder, ClaimedRecord claim, Guid self, WorkflowExecutionResult? result, RecordTransition transition,
        IReadOnlyList<ExecutionLogEntry> entries, CancellationToken cancellationToken)
    {
        if (!await recorder.CompleteAsync(claim, self, result, transition, entries, clock.GetUtcNow(), cancellationToken))
        {
            // The claim expired and another Worker owns the record now; the journal already holds this attempt's truth.
            logger.LogWarning("Record claim was lost before the result could be stored {JobRecordId}", claim.Id);
        }
    }
}
