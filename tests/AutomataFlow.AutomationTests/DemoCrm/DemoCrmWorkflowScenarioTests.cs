using System.Net.Sockets;
using System.Text;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Tests.Journal;

namespace AutomataFlow.AutomationTests.DemoCrm;

/// <summary>
/// Phase 4 real-browser scenarios (docs/PHASE4_PLAN.md §12.2, §13.4, F1–F6): real Chromium through the real egress proxy
/// and barrier, against the separately running DemoCrm. Every scenario resets DemoCrm, drives the executor attempt by
/// attempt with a shared journal, asks the evaluator for the next step as Phase 5 will, and asserts BOTH the customer
/// count and the registration POST count (so a resubmission is visible even when DemoCrm rejects it).
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class DemoCrmWorkflowScenarioTests(BrowserFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.DemoCrm.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TimeSpan JustAfterLatency = TimeSpan.FromSeconds(11); // contract 10 s + skew 1 s

    // ----- §12.2 -----

    [Fact]
    public async Task A_valid_record_is_registered_exactly_once()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var before = await rig.CustomersAsync();

        var result = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, WorkflowRig.Record("ada.valid")), default);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.StartsWith("C-", result.Results["customerId"], StringComparison.Ordinal);
        Assert.Equal("created", result.Results["registration"]);
        Assert.True(result.ExecutionFenced);
        Assert.Contains(result.Artifacts, a => a.Kind == Application.Execution.ArtifactKind.Screenshot);
        Assert.Equal((before + 1, 1), (await rig.CustomersAsync(), await rig.PostsAsync()));
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(WorkflowRig.Sample, operation).Kind);
    }

    [Fact]
    public async Task A_duplicate_email_is_a_declared_permanent_business_failure()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var first = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, Guid.NewGuid(), WorkflowRig.Record("dup.case")), default);
        var before = await rig.CustomersAsync();
        var operation = Guid.NewGuid();

        var duplicate = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, WorkflowRig.Record("dup.case")), default);

        Assert.Equal((WorkflowOutcome.Failed, "DuplicateCustomer", ErrorCategory.Business, false),
            (duplicate.Outcome, duplicate.Error!.Code, duplicate.Error.Category, duplicate.Error.IsTransient));
        Assert.Equal(first.Results["customerId"], duplicate.Results["existingCustomerId"]);
        Assert.Equal(SideEffectState.Started, duplicate.SideEffect);
        Assert.Equal((before, 2), (await rig.CustomersAsync(), await rig.PostsAsync()));
        Assert.Equal((RepeatDecisionKind.NotNeeded, Resolution.BusinessFailure), (rig.Decide(WorkflowRig.Sample, operation).Kind, rig.Decide(WorkflowRig.Sample, operation).Resolution));
    }

    [Fact]
    public async Task An_invalid_record_is_refused_before_any_journal_entry_or_browser_work()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("x");
        record["email"] = "not-an-email";

        var result = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);

        Assert.Equal((WorkflowOutcome.Refused, "RecordInvalid"), (result.Outcome, result.Error!.Code));
        Assert.Empty(rig.Journal.History(rig.Organization, operation));
        Assert.Equal(0, await rig.PostsAsync());
    }

    // ----- Variant A: the shipped sample (targetEnforced, advisory reconcile by operation id) -----

    [Fact]
    public async Task A1_commit_then_fail_is_replayed_safely_thanks_to_target_enforced_idempotency()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("a1", "commit-fail");
        var before = await rig.CustomersAsync();

        var first = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);
        Assert.Equal((WorkflowOutcome.Indeterminate, "RegistrationRejected"), (first.Outcome, first.Error!.Code));
        var decision = rig.Decide(WorkflowRig.Sample, operation);
        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency), (decision.Kind, decision.Evidence));

        var second = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);

        Assert.Equal((WorkflowOutcome.Succeeded, "replayed"), (second.Outcome, second.Results["registration"]));
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(WorkflowRig.Sample, operation).Kind);
        Assert.Equal((before + 1, 2), (await rig.CustomersAsync(), await rig.PostsAsync())); // the 2nd POST is an authorized, deduplicated replay
    }

    [Fact]
    public async Task A3_commit_then_fail_reconciled_first_is_found_without_a_second_post()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("a3", "commit-fail");

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default)).Outcome);
        var reconcile = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record, ExecutionIntent.Reconcile), default);

        Assert.Equal(WorkflowOutcome.ReconciledFound, reconcile.Outcome);
        Assert.StartsWith("C-", reconcile.Results["customerId"], StringComparison.Ordinal);
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(WorkflowRig.Sample, operation).Kind);
        Assert.Equal(1, await rig.PostsAsync());
    }

    [Fact]
    public async Task A2_two_failures_before_commit_are_replayed_until_created()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("a2", "fail-2");
        var before = await rig.CustomersAsync();

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default)).Outcome);
            Assert.Equal(SafetyEvidence.TargetEnforcedIdempotency, rig.Decide(WorkflowRig.Sample, operation).Evidence);
        }

        var third = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);
        Assert.Equal((WorkflowOutcome.Succeeded, "created"), (third.Outcome, third.Results["registration"]));
        Assert.Equal((before + 1, 3), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    [Fact]
    public async Task A4_an_advisory_NotFound_does_not_authorize_but_idempotency_does()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("a4", "fail-1");

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default)).Outcome);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        var decision = rig.Decide(WorkflowRig.Sample, operation);
        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.TargetEnforcedIdempotency), (decision.Kind, decision.Evidence));

        Assert.Equal(WorkflowOutcome.Succeeded, (await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default)).Outcome);
        Assert.Equal(2, await rig.PostsAsync());
    }

    [Fact]
    public async Task B6_a_newer_attesting_version_cannot_vouch_for_an_older_ambiguous_attempt()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var variantB = WorkflowRig.Validate(WorkflowRig.VariantB);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("b6", "commit-fail");

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(variantB, operation, record), default)).Outcome);
        var decision = rig.Decide(WorkflowRig.Sample, operation);
        Assert.Equal(RepeatDecisionKind.ReconcileRequired, decision.Kind);
        var refused = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);
        Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.RepeatNotAuthorized), (refused.Outcome, refused.Error!.Code));
        Assert.Equal(1, await rig.PostsAsync());
    }

    // ----- Variant B: a typical site (no idempotency, advisory exact-email reconcile) -----

    [Fact]
    public async Task B1_commit_then_fail_requires_reconciliation_which_finds_the_customer()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantB);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("b1", "commit-fail");

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, operation, record), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.ReconcileRequired, rig.Decide(workflow, operation).Kind);
        Assert.Equal(WorkflowOutcome.ReconciledFound, (await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);

        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(workflow, operation).Kind);
        Assert.Equal(1, await rig.PostsAsync());
    }

    [Fact]
    public async Task B2_B3_B4_an_advisory_NotFound_needs_review_and_the_guard_refuses_a_repeat()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantB);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("b2", "fail-2");
        var before = await rig.CustomersAsync();

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, operation, record), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.ReconcileRequired, rig.Decide(workflow, operation).Kind);
        var reconcile = await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, reconcile.Outcome);

        // B4: a reconcile attempt never falls through into main steps and never checkpoints.
        Assert.Equal(["open-login", "user", "password", "sign-in", "signed-in", "find", "match"], reconcile.Steps.Select(s => s.StepId));
        Assert.Equal(SideEffectState.NotStarted, rig.Journal.History(rig.Organization, operation).Single(h => h.Intent == ExecutionIntent.Reconcile).SideEffect);

        var decision = rig.Decide(workflow, operation);
        Assert.Equal((RepeatDecisionKind.NeedsReview, RepeatReason.AdvisoryNotFound), (decision.Kind, decision.Reason));

        // B3: calling the executor anyway is refused before any browser work; nothing is journaled.
        var bypass = await worker.ExecuteAsync(rig.Request(workflow, operation, record), default);
        Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.RepeatNotAuthorized), (bypass.Outcome, bypass.Error!.Code));
        Assert.Equal(2, rig.Journal.History(rig.Organization, operation).Length);
        Assert.Equal((before, 1), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    [Fact]
    public async Task B5_an_exact_email_match_is_not_fooled_by_a_substring_collision()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantB);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("ann", "commit-fail");
        record["email"] = "ann+commit-fail@example.test";

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, operation, record), default)).Outcome);
        var lookalike = WorkflowRig.Record("x");
        lookalike["email"] = "joann+commit-fail@example.test";
        var lookalikeOperation = Guid.NewGuid();
        await worker.ExecuteAsync(rig.Request(workflow, lookalikeOperation, lookalike), default); // stored, then 503

        var reconcile = await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default);
        var lookalikeReconcile = await worker.ExecuteAsync(rig.Request(workflow, lookalikeOperation, lookalike, ExecutionIntent.Reconcile), default);

        // The search for ann+commit-fail@ also lists joann+commit-fail@ (substring); the exact match picks ann.
        Assert.Equal(WorkflowOutcome.ReconciledFound, reconcile.Outcome);
        Assert.Equal(WorkflowOutcome.ReconciledFound, lookalikeReconcile.Outcome);
        Assert.NotEqual(lookalikeReconcile.Results["customerId"], reconcile.Results["customerId"]);
        Assert.Equal(2, await rig.PostsAsync());
    }

    // ----- Variant C: authoritative evidence with DemoCrm's commit-latency contract (fake journal clock) -----

    [Fact]
    public async Task C1_C3_early_lookups_are_ignored_and_a_repeat_needs_a_lookup_after_horizon_plus_latency()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantC);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("c1", "fail-2");
        var before = await rig.CustomersAsync();
        Task<WorkflowExecutionResult> Run(ExecutionIntent intent) => worker.ExecuteAsync(rig.Request(workflow, operation, record, intent), default);

        Assert.Equal(WorkflowOutcome.Indeterminate, (await Run(ExecutionIntent.Normal)).Outcome);   // #1 (fail 1), fenced at F1
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);
        rig.Manual.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await Run(ExecutionIntent.Reconcile)).Outcome); // #2 at F1+5
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);       // C3
        Assert.Equal(ExecutionErrorCodes.RepeatNotAuthorized, (await Run(ExecutionIntent.Normal)).Error!.Code);
        rig.Manual.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await Run(ExecutionIntent.Reconcile)).Outcome); // #3 at F1+11
        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.AuthoritativeNotFound), (rig.Decide(workflow, operation).Kind, rig.Decide(workflow, operation).Evidence));

        Assert.Equal(WorkflowOutcome.Indeterminate, (await Run(ExecutionIntent.Normal)).Outcome);   // #4 (fail 2), fenced at F4
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);       // #3 is before A* now
        rig.Manual.Advance(JustAfterLatency);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await Run(ExecutionIntent.Reconcile)).Outcome); // #5
        Assert.Equal(RepeatDecisionKind.Allowed, rig.Decide(workflow, operation).Kind);

        var final = await Run(ExecutionIntent.Normal);                                                    // #6
        Assert.Equal((WorkflowOutcome.Succeeded, "created"), (final.Outcome, final.Results["registration"]));
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(workflow, operation).Kind);
        Assert.Equal((before + 1, 3), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    [Fact]
    public async Task C2_C4_commit_then_fail_is_found_and_expired_evidence_requires_a_fresh_lookup()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantC);
        var found = Guid.NewGuid();
        var foundRecord = WorkflowRig.Record("c2", "commit-fail");
        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, found, foundRecord), default)).Outcome);
        rig.Manual.Advance(JustAfterLatency);
        Assert.Equal(WorkflowOutcome.ReconciledFound, (await worker.ExecuteAsync(rig.Request(workflow, found, foundRecord, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(workflow, found).Kind);

        var expired = Guid.NewGuid();
        var expiredRecord = WorkflowRig.Record("c4", "fail-1");
        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, expired, expiredRecord), default)).Outcome);
        rig.Manual.Advance(JustAfterLatency);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await worker.ExecuteAsync(rig.Request(workflow, expired, expiredRecord, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.Allowed, rig.Decide(workflow, expired).Kind);
        rig.Manual.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal((RepeatDecisionKind.ReconcileRequired, RepeatReason.EvidenceExpired), (rig.Decide(workflow, expired).Kind, rig.Decide(workflow, expired).Reason));
        Assert.Equal(ExecutionErrorCodes.RepeatNotAuthorized, (await worker.ExecuteAsync(rig.Request(workflow, expired, expiredRecord), default)).Error!.Code);
        Assert.Equal(2, await rig.PostsAsync());
    }

    // ----- Variant D: a server that commits after the client gave up (real clock) -----

    [Fact]
    public async Task D1_a_late_commit_within_the_contract_is_found_only_after_horizon_plus_latency()
    {
        var rig = new WorkflowRig(fixture, TimeProvider.System);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantD(authoritative: true));
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("d1", "late-commit");
        var before = await rig.CustomersAsync();

        var first = await worker.ExecuteAsync(rig.Request(workflow, operation, record), default);
        Assert.Equal((WorkflowOutcome.Indeterminate, "Timeout"), (first.Outcome, first.Error!.Code));
        Assert.True(first.ExecutionFenced);
        var fencedAt = first.FinishedAt;

        var early = await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, early.Outcome); // the server has not committed yet
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);

        var wait = fencedAt + JustAfterLatency - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        Assert.Equal(WorkflowOutcome.ReconciledFound, (await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(workflow, operation).Kind);
        Assert.Equal((before + 1, 1), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    [Fact]
    public async Task D2_with_advisory_evidence_an_immediate_NotFound_needs_review_and_nothing_is_resubmitted()
    {
        var rig = new WorkflowRig(fixture, TimeProvider.System);
        var (worker, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantD(authoritative: false));
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("d2", "late-commit");
        var before = await rig.CustomersAsync();

        Assert.Equal(WorkflowOutcome.Indeterminate, (await worker.ExecuteAsync(rig.Request(workflow, operation, record), default)).Outcome);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await worker.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.NeedsReview, rig.Decide(workflow, operation).Kind);

        // The late commit lands (9 s after the POST); poll with a bound instead of a fixed sleep.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (await rig.CustomersAsync() == before && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.Equal((before + 1, 1), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    // ----- Crash and concurrency (K1–K3) -----

    [Fact]
    public async Task K1_a_crash_after_the_commit_blocks_everything_until_a_process_death_fence_then_reconciles()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(60));
        var (crashing, incarnation) = await rig.NewWorkerAsync();
        var (survivor, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantB);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("k1", "commit-fail");
        var completion = new SuppressCompletion(rig);

        await completion.RunWithoutCompletionAsync(() => crashing.ExecuteAsync(rig.Request(workflow, operation, record), default)); // simulated kill: no completion
        rig.Manual.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(ExecutionErrorCodes.AwaitingFence, (await survivor.ExecuteAsync(rig.Request(workflow, operation, record), default)).Error!.Code);
        Assert.Equal(ExecutionErrorCodes.AwaitingFence, (await survivor.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Error!.Code);

        var fence = await new FakeSupervisor(rig.Journal, rig.Clock).TryConfirmProcessDeathAsync(incarnation, new FakeContainmentUnit());
        Assert.True(fence!.Accepted);
        Assert.Equal(RepeatDecisionKind.ReconcileRequired, rig.Decide(workflow, operation).Kind);
        Assert.Equal(WorkflowOutcome.ReconciledFound, (await survivor.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.NotNeeded, rig.Decide(workflow, operation).Kind);
        Assert.Equal(1, await rig.PostsAsync());
    }

    [Fact]
    public async Task K2_a_crash_before_the_checkpoint_provably_started_nothing_and_a_repeat_is_allowed()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(60));
        var (crashing, _) = await rig.NewWorkerAsync();
        var (survivor, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("k2");
        crashing.Hooks = new ExecutorTestHooks { BeforeCheckpoint = _ => throw new OperationCanceledException("simulated kill") };
        var completion = new SuppressCompletion(rig);

        await completion.RunWithoutCompletionAsync(() => crashing.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default));
        rig.Manual.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.NoSideEffectStarted), (rig.Decide(WorkflowRig.Sample, operation).Kind, rig.Decide(WorkflowRig.Sample, operation).Evidence));
        Assert.Equal(WorkflowOutcome.Succeeded, (await survivor.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default)).Outcome);
        Assert.Equal(1, await rig.PostsAsync());
    }

    [Fact]
    public async Task K3_two_concurrent_workers_for_one_operation_post_once()
    {
        var rig = new WorkflowRig(fixture);
        var (a, _) = await rig.NewWorkerAsync();
        var (b, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("k3");

        var results = await Task.WhenAll(
            a.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default),
            b.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default));

        Assert.Single(results, r => r.Outcome == WorkflowOutcome.Succeeded);
        Assert.Single(results, r => r.Outcome == WorkflowOutcome.Refused && r.Error!.Code == ExecutionErrorCodes.AttemptInFlight);
        Assert.Equal(1, await rig.PostsAsync());
    }

    // ----- Fencing (F1–F6) -----

    private static (ExecutorTestHooks Hooks, TaskCompletionSource Frozen, TaskCompletionSource<bool> Resume) Freeze(bool afterCheckpoint)
    {
        var frozen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Hook(string stepId)
        {
            frozen.TrySetResult();
            if (!await resume.Task)
            {
                throw new OperationCanceledException("the frozen worker never resumes");
            }
        }

        return (afterCheckpoint ? new ExecutorTestHooks { AfterCheckpoint = Hook } : new ExecutorTestHooks { BeforeCheckpoint = Hook }, frozen, resume);
    }

    [Fact]
    public async Task F1_F2_a_frozen_worker_blocks_takeover_and_its_resumed_click_is_the_only_extra_dispatch()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(60));
        var (a, _) = await rig.NewWorkerAsync();
        var (b, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantC);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("f1", "fail-1");
        var before = await rig.CustomersAsync();
        var (hooks, frozen, resume) = Freeze(afterCheckpoint: true);
        a.Hooks = hooks;

        var attemptA = a.ExecuteAsync(rig.Request(workflow, operation, record), default);
        await frozen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        rig.Manual.Advance(TimeSpan.FromSeconds(61)); // A's lease expires while it is frozen after its checkpoint

        // F1: nothing is admitted while A could still dispatch.
        Assert.Equal(ExecutionErrorCodes.AwaitingFence, (await b.ExecuteAsync(rig.Request(workflow, operation, record), default)).Error!.Code);
        Assert.Equal(ExecutionErrorCodes.AwaitingFence, (await b.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Error!.Code);
        var entry = Assert.Single(rig.Journal.History(rig.Organization, operation));
        Assert.Equal((AttemptState.Abandoned, SideEffectState.Started), (entry.State, entry.SideEffect));
        Assert.False(RepeatSafetyEvaluator.IsFenced(entry));
        Assert.Equal(0, await rig.PostsAsync());

        // F2: A resumes, clicks (nothing in-process can stop it), gets the 503, disposes and completes LATE.
        resume.SetResult(true);
        var resultA = await attemptA;
        Assert.Equal(WorkflowOutcome.Indeterminate, resultA.Outcome);
        Assert.True(resultA.ExecutionFenced);
        Assert.Equal(1, await rig.PostsAsync());
        var lateFence = RepeatSafetyEvaluator.FenceTime(rig.Journal.History(rig.Organization, operation).Single())!.Value;
        Assert.Equal(rig.Clock.GetUtcNow(), lateFence);

        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);
        rig.Manual.Advance(JustAfterLatency);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await b.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        Assert.Equal(RepeatDecisionKind.Allowed, rig.Decide(workflow, operation).Kind);
        var resultB = await b.ExecuteAsync(rig.Request(workflow, operation, record), default);
        Assert.Equal((WorkflowOutcome.Succeeded, "created"), (resultB.Outcome, resultB.Results["registration"]));
        Assert.Equal((before + 1, 2), (await rig.CustomersAsync(), await rig.PostsAsync()));
    }

    [Fact]
    public async Task F3_a_process_death_fence_from_the_supervisor_lets_reconciliation_proceed_from_its_commit_time()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(60));
        var (a, incarnationA) = await rig.NewWorkerAsync();
        var (b, _) = await rig.NewWorkerAsync();
        var workflow = WorkflowRig.Validate(WorkflowRig.VariantC);
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("f3", "fail-1");
        var (hooks, frozen, resume) = Freeze(afterCheckpoint: true);
        a.Hooks = hooks;
        var attemptA = a.ExecuteAsync(rig.Request(workflow, operation, record), default);
        await frozen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        rig.Manual.Advance(TimeSpan.FromSeconds(61));

        var fence = await new FakeSupervisor(rig.Journal, rig.Clock).TryConfirmProcessDeathAsync(incarnationA, new FakeContainmentUnit());
        Assert.True(fence!.Accepted);
        var p = fence.FencedAt!.Value;
        Assert.Equal(0, await rig.PostsAsync());
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, rig.Decide(workflow, operation).Kind);
        rig.Manual.Advance(JustAfterLatency);
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await b.ExecuteAsync(rig.Request(workflow, operation, record, ExecutionIntent.Reconcile), default)).Outcome);
        var decision = rig.Decide(workflow, operation);
        Assert.Equal((RepeatDecisionKind.Allowed, SafetyEvidence.AuthoritativeNotFound), (decision.Kind, decision.Evidence));
        Assert.True(rig.Clock.GetUtcNow() >= p + JustAfterLatency);

        resume.SetResult(false); // the "dead" worker never dispatches
        await attemptA;
        Assert.Equal(0, await rig.PostsAsync());
    }

    [Fact]
    public async Task F4_a_worker_frozen_before_its_checkpoint_can_never_dispatch_after_takeover()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(60));
        var (a, _) = await rig.NewWorkerAsync();
        var (b, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var record = WorkflowRig.Record("f4");
        var (hooks, frozen, resume) = Freeze(afterCheckpoint: false);
        a.Hooks = hooks;
        var attemptA = a.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);
        await frozen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        rig.Manual.Advance(TimeSpan.FromSeconds(61));

        var resultB = await b.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, record), default);
        Assert.Equal(WorkflowOutcome.Succeeded, resultB.Outcome);
        resume.SetResult(true);
        var resultA = await attemptA;

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.CheckpointFailed, SideEffectState.NotStarted), (resultA.Outcome, resultA.Error!.Code, resultA.SideEffect));
        Assert.Equal(1, await rig.PostsAsync());
    }

    [Fact]
    public async Task F5_a_checkpoint_with_too_short_a_lease_is_refused_and_nothing_is_dispatched()
    {
        var rig = new WorkflowRig(fixture, lease: TimeSpan.FromSeconds(35)); // submit timeout 30 s + margin 10 s > 35 s
        var (worker, _) = await rig.NewWorkerAsync();

        var result = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, Guid.NewGuid(), WorkflowRig.Record("f5")), default);

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.CheckpointFailed), (result.Outcome, result.Error!.Code));
        Assert.Equal(0, await rig.PostsAsync());
    }

    [Fact]
    public async Task F6_after_the_attempt_the_old_proxy_credentials_are_refused_and_the_completion_is_fenced()
    {
        var rig = new WorkflowRig(fixture);
        var (worker, _) = await rig.NewWorkerAsync();
        var operation = Guid.NewGuid();
        var credentials = new List<(string User, string Password)>();
        worker.Hooks = new ExecutorTestHooks
        {
            AfterCheckpoint = _ =>
            {
                credentials.AddRange(fixture.Engine.Proxy.Sessions.SnapshotForTests().Where(s => !s.IsRevoked).Select(s => (s.Username, s.Password)));
                return Task.CompletedTask;
            },
        };

        var result = await worker.ExecuteAsync(rig.Request(WorkflowRig.Sample, operation, WorkflowRig.Record("f6")), default);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.True(result.ExecutionFenced);
        Assert.True(rig.Journal.History(rig.Organization, operation).Single().ExecutionFenced);
        Assert.NotEmpty(credentials);
        foreach (var (user, password) in credentials)
        {
            Assert.Equal(407, await ConnectStatusAsync(user, password));
        }
    }

    private async Task<int> ConnectStatusAsync(string user, string password)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(fixture.Engine.Proxy.Endpoint!);
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        var target = new Uri(fixture.DemoCrm.BaseUrl).Authority;
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
        var buffer = new byte[12];
        var read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cts.Token) is var n and > 0)
        {
            read += n;
        }

        return int.Parse(Encoding.ASCII.GetString(buffer, 9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Simulates a killed Worker from the journal's view: its completion never arrives.</summary>
    private sealed class SuppressCompletion(WorkflowRig rig)
    {
        public async Task RunWithoutCompletionAsync(Func<Task<WorkflowExecutionResult>> attempt)
        {
            rig.Journal.SuppressCompletions = true;
            try
            {
                await attempt();
            }
            finally
            {
                rig.Journal.SuppressCompletions = false;
            }
        }
    }
}
