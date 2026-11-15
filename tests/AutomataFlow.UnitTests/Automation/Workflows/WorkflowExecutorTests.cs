using System.Text.Json.Nodes;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Workflows;
using AutomataFlow.Tests.Journal;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Automation.Workflows;

/// <summary>Phase 4 step 7: the executor with a fake session and the in-memory journal (§7.3, §9, §13.2).</summary>
public class WorkflowExecutorTests
{
    private static readonly Guid Org = Guid.NewGuid();

    /// <summary>Full sample without the idempotency attestation: an ambiguous attempt can only be reconciled.</summary>
    private static readonly string NoAttestation = With(Full, r => FirstStep(r).Remove("idempotency"));

    /// <summary>fill op → click (side effect) → navigate → extract → terminal capture.</summary>
    private static string Linear(string? terminal = null, int extractMax = 1000) => With(Full, r => r["steps"] = new JsonArray(
        JsonNode.Parse("""{"id":"submit","type":"click","config":{"selector":{"testId":"save"}},"sideEffect":true,"timeoutMs":15000}"""),
        JsonNode.Parse("""{"id":"go","type":"navigate","config":{"url":"{{params.crmBaseUrl}}/Customers/Confirmation"}}"""),
        JsonNode.Parse("""{"id":"read-id","type":"extract","config":{"selector":{"testId":"customer-id"},"source":"text","maxLength":""" + extractMax + "}}"),
        JsonNode.Parse(terminal ?? """{"id":"done","type":"captureResult","config":{"fields":{"customerId":"{{steps.read-id.value}}"},"outcome":{"status":"succeeded"}}}""")));

    private static WorkflowExecutionRequest Request(ValidatedWorkflow workflow, Guid operation, ExecutionIntent intent = ExecutionIntent.Normal, string? hash = null, Dictionary<string, string?>? record = null) =>
        new(new ExecutionScope(Org, Guid.NewGuid(), Guid.NewGuid(), 0), operation, workflow, 1, hash ?? workflow.DefinitionHash,
            record ?? new Dictionary<string, string?> { ["email"] = "ada.lovelace@example.test", ["taxId"] = "TX-998877" },
            new Dictionary<string, string> { ["crmBaseUrl"] = "https://crm.example.test", ["crmUser"] = "agent" }, intent);

    private static void DefaultPage(FakeBrowserSession session)
    {
        session.Texts["customer-id"] = "C-42";
    }

    private static async Task<(ExecutorHarness Harness, WorkflowExecutionResult Result)> RunAsync(string json, ExecutionIntent intent = ExecutionIntent.Normal, Action<FakeBrowserSession>? page = null, ExecutorHarness? harness = null, Guid? operation = null)
    {
        harness ??= await ExecutorHarness.CreateAsync();
        harness.ConfigureSession = s =>
        {
            DefaultPage(s);
            page?.Invoke(s);
        };
        var result = await harness.Executor.ExecuteAsync(Request(Valid(json), operation ?? Guid.NewGuid(), intent), default);
        return (harness, result);
    }

    // ----- Happy path, ordering, sections -----

    [Fact]
    public async Task J1_a_normal_attempt_runs_setup_then_steps_and_checkpoints_before_the_side_effect()
    {
        var (h, result) = await RunAsync(Full);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("C-42", result.Results["customerId"]);
        Assert.Equal(SideEffectState.Started, result.SideEffect);
        Assert.True(result.ExecutionFenced);
        var events = h.Log.Events;
        Assert.True(h.Log.IndexOf("navigate:https://crm.example.test/Login") < h.Log.IndexOf("fill:operation-id"));
        Assert.True(h.Log.IndexOf("checkpoint:submit") >= 0 && h.Log.IndexOf("checkpoint:submit") < h.Log.IndexOf("click:save"), string.Join(", ", events));
        Assert.True(h.Log.IndexOf("dispose") < h.Log.IndexOf("complete:"), "the session is disposed (barrier) before the journal completion");
        Assert.Equal(["login", "op", "submit", "read-id", "done"], result.Steps.Select(s => s.StepId));
        Assert.Equal(TimeSpan.FromMilliseconds(15_000), h.Session.Options[2]!.Timeout); // session calls: navigate, fill, click, text
    }

    [Fact]
    public async Task Each_action_calls_its_session_operation_with_the_step_deadline()
    {
        var (h, result) = await RunAsync(Linear());

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        var ops = h.Log.Events.Where(e => e.Contains(':', StringComparison.Ordinal) && !e.StartsWith("checkpoint", StringComparison.Ordinal) && !e.StartsWith("complete", StringComparison.Ordinal)).ToList();
        Assert.Equal(["navigate:https://crm.example.test/Login", "fill:operation-id", "click:save", "navigate:https://crm.example.test/Customers/Confirmation", "text:customer-id"], ops);
        Assert.All(h.Session.Options, o => Assert.NotNull(o?.Timeout));
        Assert.Equal(TimeSpan.FromSeconds(15), h.Session.Options[2]!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), h.Session.Options[3]!.Timeout); // navigate default
    }

    [Fact]
    public async Task J9_a_reconcile_attempt_observes_first_and_never_runs_main_steps()
    {
        var operation = Guid.NewGuid();
        var (h, result) = await RunAsync(Full, ExecutionIntent.Reconcile, s =>
        {
            s.Counts["tr.customer"] = 1;
            s.Rows = new RowSnapshot(1, [[operation.ToString("D")]]);
        }, operation: operation);

        Assert.Equal(WorkflowOutcome.ReconciledFound, result.Outcome);
        Assert.True(h.Log.IndexOf("observe") < h.Log.IndexOf("count:tr.customer"));
        Assert.Equal(-1, h.Log.IndexOf("click"));
        Assert.Equal(-1, h.Log.IndexOf("checkpoint"));
        Assert.Equal(ReconciliationFinding.Found, result.Reconciliation!.Finding);
        Assert.Equal(ExecutorHarness.T0, result.Reconciliation.ObservedAt); // the journal clock
    }

    [Fact]
    public async Task J10_result_timestamps_come_from_the_journal_clock()
    {
        var (h, result) = await RunAsync(Full);

        Assert.Equal(ExecutorHarness.T0, result.StartedAt);
        Assert.Equal(ExecutorHarness.T0, result.FinishedAt);
        Assert.Equal(h.Clock.GetUtcNow(), result.FinishedAt);
    }

    [Fact]
    public async Task OnError_continue_records_the_failure_and_later_steps_can_read_failed()
    {
        var json = With(Linear("""{"id":"done","type":"captureResult","config":{"fields":{"readFailed":"{{steps.read-id.failed}}"},"outcome":{"status":"succeeded"}}}"""),
            r => r["steps"]![2]!["onError"] = "continue");

        var (_, result) = await RunAsync(json, page: s => s.Texts.Remove("customer-id"));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("true", result.Results["readFailed"]);
        Assert.Equal(StepStatus.FailedContinued, result.Steps.Single(s => s.StepId == "read-id").Status);
    }

    // ----- Classification after a side effect (R8: X1–X10) -----

    [Theory]
    [InlineData("text", "ElementNotUnique")]
    [InlineData("navigate:https://crm.example.test/Customers/Confirmation", "NavigationBlocked")]
    [InlineData("navigate:https://crm.example.test/Customers/Confirmation", "Timeout")]
    public async Task X1_X3_any_error_after_the_side_effect_is_Indeterminate_keeping_its_code(string operation, string code)
    {
        var browserCode = Enum.Parse<BrowserErrorCode>(code);
        var (_, result) = await RunAsync(Linear(), page: s => s.Failures[operation] = () => new BrowserOperationException(browserCode, "x", browserCode == BrowserErrorCode.Timeout));

        Assert.Equal(WorkflowOutcome.Indeterminate, result.Outcome);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(SideEffectState.Started, result.SideEffect);
    }

    [Fact]
    public async Task X4_an_http_error_after_the_side_effect_is_Indeterminate()
    {
        var (_, result) = await RunAsync(Linear(), page: s => s.NavigateStatuses["https://crm.example.test/Customers/Confirmation"] = 500);

        Assert.Equal((WorkflowOutcome.Indeterminate, "HttpError"), (result.Outcome, result.Error!.Code));
    }

    [Fact]
    public async Task X2_X5_X6_engine_errors_after_the_side_effect_are_Indeterminate()
    {
        var tooLong = await RunAsync(Linear(extractMax: 3));
        Assert.Equal((WorkflowOutcome.Indeterminate, ExecutionErrorCodes.ExtractTooLong), (tooLong.Result.Outcome, tooLong.Result.Error!.Code));

        var tooLarge = await RunAsync(Linear(extractMax: 10_000), page: s => s.Texts["customer-id"] = new string('x', 2_500));
        Assert.Equal((WorkflowOutcome.Indeterminate, ExecutionErrorCodes.ResultTooLarge), (tooLarge.Result.Outcome, tooLarge.Result.Error!.Code));

        var unresolved = await RunAsync(With(Linear(), r => r["steps"]![2]!["onError"] = "continue"), page: s => s.Texts.Remove("customer-id"));
        Assert.Equal((WorkflowOutcome.Indeterminate, "VariableUnresolved"), (unresolved.Result.Outcome, unresolved.Result.Error!.Code));
    }

    [Fact]
    public async Task X7_X8_only_a_declared_permanent_business_failure_is_Failed_after_the_side_effect()
    {
        var permanent = await RunAsync(Linear("""{"id":"done","type":"captureResult","config":{"outcome":{"status":"failed","code":"DuplicateCustomer"}}}"""));
        Assert.Equal((WorkflowOutcome.Failed, "DuplicateCustomer", ErrorCategory.Business, false),
            (permanent.Result.Outcome, permanent.Result.Error!.Code, permanent.Result.Error.Category, permanent.Result.Error.IsTransient));

        var transient = await RunAsync(Linear("""{"id":"done","type":"captureResult","config":{"outcome":{"status":"failed","code":"Busy","transient":true}}}"""));
        Assert.Equal((WorkflowOutcome.Indeterminate, "Busy"), (transient.Result.Outcome, transient.Result.Error!.Code));

        var when = await RunAsync(Linear("""{"id":"done","type":"captureResult","config":{"outcome":{"status":"failed","code":"Busy","transientWhen":{"elementVisible":{"testId":"retry"}}}}}"""),
            page: s => s.Visible.Add("retry"));
        Assert.Equal(WorkflowOutcome.Indeterminate, when.Result.Outcome);
    }

    [Fact]
    public async Task X9_cancellation_after_the_side_effect_is_Indeterminate_and_X10_before_it_is_Cancelled()
    {
        using var after = new CancellationTokenSource();
        var harness = await ExecutorHarness.CreateAsync();
        harness.ConfigureSession = s =>
        {
            DefaultPage(s);
            s.OnOperation = (op, _) =>
            {
                if (op == "navigate" && s.CurrentUrl.EndsWith("/Login", StringComparison.Ordinal))
                {
                    after.Cancel();
                }

                return Task.CompletedTask;
            };
        };
        var afterResult = await harness.Executor.ExecuteAsync(Request(Valid(Linear()), Guid.NewGuid()), after.Token);
        Assert.Equal(WorkflowOutcome.Indeterminate, afterResult.Outcome);

        using var before = new CancellationTokenSource();
        harness.ConfigureSession = s => s.OnOperation = (op, _) =>
        {
            if (op == "fill")
            {
                before.Cancel();
            }

            return Task.CompletedTask;
        };
        var beforeResult = await harness.Executor.ExecuteAsync(Request(Valid(Linear()), Guid.NewGuid()), before.Token);
        Assert.Equal((WorkflowOutcome.Cancelled, SideEffectState.NotStarted), (beforeResult.Outcome, beforeResult.SideEffect));
    }

    [Fact]
    public async Task X10_the_same_errors_before_the_checkpoint_are_Failed()
    {
        var (_, result) = await RunAsync(Linear(), page: s => s.Failures["fill"] = () => new BrowserOperationException(BrowserErrorCode.ElementNotUnique, "x", false));

        Assert.Equal((WorkflowOutcome.Failed, "ElementNotUnique", SideEffectState.NotStarted), (result.Outcome, result.Error!.Code, result.SideEffect));
    }

    [Fact]
    public async Task T2_a_side_effect_click_timeout_is_Indeterminate_and_T3_a_navigate_timeout_ends_the_attempt()
    {
        var click = await RunAsync(Linear(), page: s => s.Failures["click"] = () => new BrowserOperationException(BrowserErrorCode.Timeout, "x", true));
        Assert.Equal((WorkflowOutcome.Indeterminate, "Timeout"), (click.Result.Outcome, click.Result.Error!.Code));

        var navigate = await RunAsync(Full, page: s => s.Failures["navigate"] = () => new BrowserOperationException(BrowserErrorCode.Timeout, "x", true));
        Assert.Equal((WorkflowOutcome.Failed, "Timeout", true), (navigate.Result.Outcome, navigate.Result.Error!.Code, navigate.Result.Error.IsTransient));
        Assert.Equal(-1, navigate.Harness.Log.IndexOf("fill"));
    }

    // ----- Journal and checkpoint (J2–J8, J11) -----

    [Fact]
    public async Task J2_a_failed_checkpoint_never_invokes_the_handler()
    {
        var harness = await ExecutorHarness.CreateAsync((clock, _) => new CheckpointThrowsJournal(clock, TimeSpan.FromMinutes(5)));

        var (_, result) = await RunAsync(Full, harness: harness);

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.CheckpointFailed, SideEffectState.NotStarted), (result.Outcome, result.Error!.Code, result.SideEffect));
        Assert.Equal(-1, harness.Log.IndexOf("click"));
    }

    [Fact]
    public async Task J3_a_lost_checkpoint_acknowledgement_leaves_an_ambiguous_attempt_in_the_journal()
    {
        var harness = await ExecutorHarness.CreateAsync((clock, _) => new CheckpointAckLostJournal(clock, TimeSpan.FromMinutes(5)));
        var operation = Guid.NewGuid();

        var (_, result) = await RunAsync(NoAttestation, harness: harness, operation: operation);

        Assert.Equal((WorkflowOutcome.Failed, SideEffectState.NotStarted), (result.Outcome, result.SideEffect));
        Assert.Equal(-1, harness.Log.IndexOf("click"));
        var history = harness.Journal.History(Org, operation);
        Assert.Equal(SideEffectState.Started, Assert.Single(history).SideEffect);
        var decision = RepeatSafetyEvaluator.Evaluate(Valid(NoAttestation), operation, history, harness.Clock.GetUtcNow(), RepeatSafetyPolicy.Default);
        Assert.NotEqual(RepeatDecisionKind.Allowed, decision.Kind);
    }

    [Fact]
    public async Task J4_a_checkpoint_after_lease_loss_is_fenced_and_nothing_is_dispatched()
    {
        var harness = await ExecutorHarness.CreateAsync((clock, log) => new RecordingJournal(clock, log, TimeSpan.FromSeconds(60)));
        harness.Executor.Hooks = new ExecutorTestHooks { BeforeCheckpoint = _ => { harness.Clock.Advance(TimeSpan.FromSeconds(61)); return Task.CompletedTask; } };

        var (_, result) = await RunAsync(Full, harness: harness);

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.CheckpointFailed), (result.Outcome, result.Error!.Code));
        Assert.Equal(-1, harness.Log.IndexOf("click"));
    }

    [Fact]
    public async Task J5_a_failed_completion_is_only_a_diagnostic()
    {
        var harness = await ExecutorHarness.CreateAsync((clock, _) => new CompleteThrowsJournal(clock, TimeSpan.FromMinutes(5)));

        var (_, result) = await RunAsync(Full, harness: harness);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Contains("JournalCompletionFailed", result.Diagnostics);
    }

    [Fact]
    public async Task J6_J7_concurrent_attempts_for_one_operation_admit_exactly_one()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConfigureSession = s =>
        {
            DefaultPage(s);
            s.OnOperation = async (op, _) =>
            {
                if (op == "navigate")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            };
        };
        var workflow = Valid(Full);
        var operation = Guid.NewGuid();

        var first = harness.Executor.ExecuteAsync(Request(workflow, operation), default);
        await entered.Task;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => harness.Executor.ExecuteAsync(Request(workflow, operation), default)));
        release.SetResult();

        Assert.All(concurrent, r => Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.AttemptInFlight), (r.Outcome, r.Error!.Code)));
        Assert.Equal(WorkflowOutcome.Succeeded, (await first).Outcome);
        Assert.Single(harness.Factory.Sessions);
    }

    [Fact]
    public async Task J8_refusals_are_never_journaled_and_do_not_change_the_next_decision()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var operation = Guid.NewGuid();
        var workflow = Valid(Full);

        var invalidRecord = await harness.Executor.ExecuteAsync(Request(workflow, operation, record: new Dictionary<string, string?>()), default);
        var tampered = await harness.Executor.ExecuteAsync(Request(workflow, operation, hash: Valid(Minimal).DefinitionHash), default);

        Assert.Equal((WorkflowOutcome.Refused, EngineErrorCodes.RecordInvalid), (invalidRecord.Outcome, invalidRecord.Error!.Code));
        Assert.Equal((WorkflowOutcome.Refused, EngineErrorCodes.WorkflowTampered), (tampered.Outcome, tampered.Error!.Code));
        Assert.Empty(harness.Journal.History(Org, operation));
        Assert.Empty(harness.Factory.Sessions);
        Assert.Equal(RepeatDecisionKind.Allowed, RepeatSafetyEvaluator.Evaluate(workflow, operation, harness.Journal.History(Org, operation), harness.Clock.GetUtcNow(), RepeatSafetyPolicy.Default).Kind);
    }

    [Fact]
    public async Task Intent_guard_refuses_an_unauthorized_repeat_before_any_session_exists()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var operation = Guid.NewGuid();
        var (_, ambiguous) = await RunAsync(NoAttestation, page: s => s.Failures["text"] = () => new BrowserOperationException(BrowserErrorCode.ElementNotUnique, "x", false), harness: harness, operation: operation);
        Assert.Equal(WorkflowOutcome.Indeterminate, ambiguous.Outcome);
        var sessions = harness.Factory.Sessions.Count;

        var repeat = await harness.Executor.ExecuteAsync(Request(Valid(NoAttestation), operation), default);

        Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.RepeatNotAuthorized), (repeat.Outcome, repeat.Error!.Code));
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, repeat.Refusal!.Kind);
        Assert.Equal(sessions, harness.Factory.Sessions.Count);
        Assert.Single(harness.Journal.History(Org, operation));
    }

    [Fact]
    public async Task Intent_guard_admits_a_repeat_backed_by_target_enforced_idempotency()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var operation = Guid.NewGuid();
        await RunAsync(Full, page: s => s.Failures["text"] = () => new BrowserOperationException(BrowserErrorCode.ElementNotUnique, "x", false), harness: harness, operation: operation);

        var repeat = await RunAsync(Full, harness: harness, operation: operation);

        Assert.Equal(WorkflowOutcome.Succeeded, repeat.Result.Outcome);
        Assert.Equal(2, repeat.Result.AttemptNumber);
    }

    [Fact]
    public async Task A_reconcile_request_without_a_reconcile_section_is_refused()
    {
        var harness = await ExecutorHarness.CreateAsync();

        var result = await harness.Executor.ExecuteAsync(Request(Valid(Minimal), Guid.NewGuid(), ExecutionIntent.Reconcile, record: new Dictionary<string, string?>()), default);

        Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.ReconcileNotAvailable), (result.Outcome, result.Error!.Code));
        Assert.Empty(harness.Factory.Sessions);
    }

    [Fact]
    public async Task J11_cancelled_reconciles_are_Cancelled_and_count_toward_the_reconcile_budget()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var operation = Guid.NewGuid();
        await RunAsync(NoAttestation, page: s => s.Failures["text"] = () => new BrowserOperationException(BrowserErrorCode.ElementNotUnique, "x", false), harness: harness, operation: operation);

        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource();
            harness.ConfigureSession = s => s.OnOperation = (op, _) =>
            {
                if (op == "count")
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            };
            var reconcile = await harness.Executor.ExecuteAsync(Request(Valid(NoAttestation), operation, ExecutionIntent.Reconcile), cts.Token);
            Assert.Equal(WorkflowOutcome.Cancelled, reconcile.Outcome);
        }

        var decision = RepeatSafetyEvaluator.Evaluate(Valid(NoAttestation), operation, harness.Journal.History(Org, operation), harness.Clock.GetUtcNow(), RepeatSafetyPolicy.Default);
        Assert.Equal((RepeatDecisionKind.NeedsReview, RepeatReason.ReconcileBudgetExhausted), (decision.Kind, decision.Reason));
    }

    [Fact]
    public async Task An_unconfirmed_revocation_leaves_the_attempt_unfenced_and_blocks_the_operation()
    {
        var harness = await ExecutorHarness.CreateAsync();
        var operation = Guid.NewGuid();

        var (_, result) = await RunAsync(Full, page: s => s.RevokeOnDispose = false, harness: harness, operation: operation);

        Assert.False(result.ExecutionFenced);
        var next = await harness.Executor.ExecuteAsync(Request(Valid(Full), operation, ExecutionIntent.Reconcile), default);
        Assert.Equal((WorkflowOutcome.Refused, ExecutionErrorCodes.AwaitingFence), (next.Outcome, next.Error!.Code));
    }

    // ----- Binding, tamper, disposal, logging -----

    [Fact]
    public async Task Binding_refuses_a_workflow_timeout_above_the_server_session_limit_before_the_browser_starts()
    {
        var options = new BrowserEngineOptions { Mode = ExecutionMode.Development };
        options.Timeouts.SessionSeconds = 60;
        var harness = await ExecutorHarness.CreateAsync(options: options);
        var operation = Guid.NewGuid();

        var result = await harness.Executor.ExecuteAsync(Request(Valid(Full), operation), default);

        Assert.Equal((WorkflowOutcome.Refused, EngineErrorCodes.WorkflowTimeoutExceedsServerLimit), (result.Outcome, result.Error!.Code));
        Assert.Empty(harness.Factory.Sessions);
        Assert.Empty(harness.Journal.History(Org, operation)); // released, never an attempt
    }

    [Fact]
    public async Task Server_options_map_into_binding_and_private_origins_only_in_Development()
    {
        var development = new BrowserEngineOptions { Mode = ExecutionMode.Development, DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:5080"] };
        development.Uploads.MaxBytes = 2_000_000;
        var (binding, uploads) = WorkflowExecutor.MapServerOptions(development);
        Assert.True(binding.IsDevelopment);
        Assert.Equal(["http://127.0.0.1:5080"], binding.DevelopmentPrivateNetworkOrigins);
        Assert.Equal((2_000_000L, 300), (binding.MaxUploadBytes, binding.MaxSessionSeconds));
        Assert.Equal(2_000_000L, uploads.MaxBytes);

        var hosted = WorkflowExecutor.MapServerOptions(new BrowserEngineOptions { Mode = ExecutionMode.SelfHosted, DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:5080"] }).Binding;
        Assert.False(hosted.IsDevelopment);
        Assert.Empty(hosted.DevelopmentPrivateNetworkOrigins);

        var harness = await ExecutorHarness.CreateAsync();
        harness.ConfigureSession = DefaultPage;
        var result = await harness.Executor.ExecuteAsync(Request(Valid(Full), Guid.NewGuid()) with { Parameters = new Dictionary<string, string> { ["crmBaseUrl"] = "http://127.0.0.1:5080", ["crmUser"] = "a" } }, default);
        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.False(harness.Factory.Requests[0].Policy.Rules.Single().AllowPrivateNetwork); // not in the trusted list of this harness
    }

    [Fact]
    public async Task The_session_is_always_disposed_and_no_values_are_ever_logged()
    {
        var (h, result) = await RunAsync(Linear(), page: s => s.Failures["text"] = () => new BrowserOperationException(BrowserErrorCode.NetworkError, "x", true));

        Assert.Equal(WorkflowOutcome.Indeterminate, result.Outcome);
        Assert.True(h.Session.Disposed);
        var logs = string.Join("\n", h.Logger.Messages);
        Assert.NotEmpty(h.Logger.Messages);
        foreach (var value in new[] { "ada.lovelace", "TX-998877", "pw-S3cret-value", "C-42", "agent" })
        {
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Secrets_are_filled_as_sensitive_values_and_mask_selectors_are_registered()
    {
        var json = With(Full, r => ((JsonArray)r["setup"]!).Add(JsonNode.Parse("""{"id":"pw","type":"fill","config":{"selector":{"label":"Password"},"value":"{{secret.crmPassword}}"}}""")));

        var (h, result) = await RunAsync(json);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Contains(h.Session.Filled, f => f.IsSensitive && f.Value == "pw-S3cret-value");
        Assert.Equal(2, h.Session.Sensitive.Count(s => s.Value.Length == 0 && s.Selector is not null)); // the two maskSelectors
    }

    [Fact]
    public async Task A_missing_secret_ends_the_attempt_before_any_browser_session()
    {
        var harness = await ExecutorHarness.CreateAsync();
        harness.Secrets.Values.Clear();

        var (_, result) = await RunAsync(Full, harness: harness);

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.SecretUnavailable, true), (result.Outcome, result.Error!.Code, result.Error.IsTransient));
        Assert.Empty(harness.Factory.Sessions);
    }

    // ----- Uploads (§5.4): checks before the checkpoint -----

    private static string UploadWorkflow => With(Full, r => r["steps"] = new JsonArray(
        JsonNode.Parse("""{"id":"attach","type":"upload","config":{"selector":{"testId":"file"},"file":"{{record.contract}}","allowedExtensions":[".pdf"]},"sideEffect":true}"""),
        JsonNode.Parse("""{"id":"done","type":"captureResult","config":{"fields":{"name":"{{steps.attach.fileName}}"},"outcome":{"status":"succeeded"}}}""")));

    private static async Task<(ExecutorHarness, WorkflowExecutionResult)> UploadAsync(string fileName, string contentType, byte[] bytes, long? declared = null)
    {
        var harness = await ExecutorHarness.CreateAsync();
        harness.Files.Files["ref-1"] = (fileName, contentType, bytes, declared);
        harness.ConfigureSession = DefaultPage;
        var request = Request(Valid(UploadWorkflow), Guid.NewGuid(), record: new Dictionary<string, string?> { ["email"] = "a@example.test", ["contract"] = "ref-1" });
        return (harness, await harness.Executor.ExecuteAsync(request, default));
    }

    [Fact]
    public async Task A_valid_upload_is_checked_then_checkpointed_then_dispatched_as_content()
    {
        var (h, result) = await UploadAsync("../../contract.pdf", "application/pdf", [.. "%PDF-1.7 fictional"u8]);

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("contract.pdf", result.Results["name"]);
        Assert.True(h.Log.IndexOf("checkpoint:attach") < h.Log.IndexOf("upload:file"));
        Assert.Equal("application/pdf", h.Session.Uploaded.Single().ContentType);
    }

    [Theory]
    [InlineData("report.pdf", "application/pdf", "MZ\u0090")]          // renamed executable: signature mismatch
    [InlineData("report.exe", "application/pdf", "%PDF-1.7")]               // extension not allowed
    [InlineData("report.pdf", "text/plain", "%PDF-1.7")]                     // content type mismatch
    public async Task Rejected_uploads_never_reach_the_checkpoint(string name, string type, string content)
    {
        var (h, result) = await UploadAsync(name, type, System.Text.Encoding.Latin1.GetBytes(content));

        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.UploadRejected, SideEffectState.NotStarted), (result.Outcome, result.Error!.Code, result.SideEffect));
        Assert.Equal(-1, h.Log.IndexOf("checkpoint"));
        Assert.Empty(h.Session.Uploaded);
    }

    [Fact]
    public async Task Oversized_uploads_are_rejected_by_declared_and_actual_size()
    {
        var big = new byte[11 * 1024 * 1024];
        "%PDF-"u8.CopyTo(big);
        Assert.Equal(ExecutionErrorCodes.UploadRejected, (await UploadAsync("a.pdf", "application/pdf", big)).Item2.Error!.Code);
        Assert.Equal(ExecutionErrorCodes.UploadRejected, (await UploadAsync("a.pdf", "application/pdf", [.. "%PDF-1"u8], declared: 50L * 1024 * 1024)).Item2.Error!.Code);
    }

    // ----- reconcileMatch M1–M10 -----

    private static string BusinessKey(string key = """{"text":{"css":"td.email"}}""", bool ignoreCase = false, int maxRows = 20) => With(Full, r =>
    {
        r["reconciliation"]!["matchKey"] = "businessKey";
        r["reconcile"]![0]!["config"] = JsonNode.Parse(
            "{\"rows\":{\"css\":\"tr.customer\"},\"key\":" + key + ",\"equals\":\"{{record.email}}\",\"ignoreCase\":" + (ignoreCase ? "true" : "false")
            + ",\"emptyIndicator\":{\"testId\":\"no-results\"},\"maxRows\":" + maxRows + ",\"fields\":{\"customerId\":{\"attribute\":\"data-id\"}}}");
    });

    private static Task<(ExecutorHarness, WorkflowExecutionResult)> ReconcileAsync(string json, int count, IReadOnlyList<IReadOnlyList<string?>> rows, bool indicator = false) =>
        RunAsync(json, ExecutionIntent.Reconcile, s =>
        {
            s.Counts["tr.customer"] = count;
            s.Rows = new RowSnapshot(count, rows);
            if (indicator)
            {
                s.Visible.Add("no-results");
            }
        });

    [Fact]
    public async Task M1_M2_zero_rows_are_NotFound_only_with_the_empty_indicator()
    {
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await ReconcileAsync(BusinessKey(), 0, [], indicator: true)).Item2.Outcome);
        var inconclusive = (await ReconcileAsync(BusinessKey(), 0, [])).Item2;
        Assert.Equal((WorkflowOutcome.Failed, ExecutionErrorCodes.LookupInconclusive, true), (inconclusive.Outcome, inconclusive.Error!.Code, inconclusive.Error.IsTransient));
        Assert.Null(inconclusive.Reconciliation);
    }

    [Fact]
    public async Task M3_an_exact_match_among_substring_rows_is_Found_with_fields_from_the_right_row()
    {
        var (_, result) = await ReconcileAsync(BusinessKey(), 2, [["joann.lovelace@example.test", "C-1"], ["ada.lovelace@example.test", "C-2"]]);

        Assert.Equal(WorkflowOutcome.ReconciledFound, result.Outcome);
        Assert.Equal("C-2", result.Results["customerId"]);
    }

    [Fact]
    public async Task M4_M5_M6_ambiguity_too_many_rows_and_no_exact_match()
    {
        var two = (await ReconcileAsync(BusinessKey(), 2, [["ada.lovelace@example.test", "1"], ["ada.lovelace@example.test", "2"]])).Item2;
        Assert.Equal((WorkflowOutcome.NeedsReview, ExecutionErrorCodes.AmbiguousMatch), (two.Outcome, two.Error!.Code));

        var many = (await ReconcileAsync(BusinessKey(maxRows: 2), 3, [])).Item2;
        Assert.Equal((WorkflowOutcome.NeedsReview, ExecutionErrorCodes.TooManyRows), (many.Outcome, many.Error!.Code));

        var none = (await ReconcileAsync(BusinessKey(), 1, [["someone.else@example.test", "9"]])).Item2;
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, none.Outcome);
        Assert.Equal(ReconciliationFinding.NotFound, none.Reconciliation!.Finding);
    }

    [Fact]
    public async Task M7_M8_ignore_case_and_trimming()
    {
        Assert.Equal(WorkflowOutcome.ReconciledNotFound, (await ReconcileAsync(BusinessKey(), 1, [["ADA.LOVELACE@example.test", "1"]])).Item2.Outcome);
        Assert.Equal(WorkflowOutcome.ReconciledFound, (await ReconcileAsync(BusinessKey(ignoreCase: true), 1, [["ADA.LOVELACE@example.test", "1"]])).Item2.Outcome);
        Assert.Equal(WorkflowOutcome.ReconciledFound, (await ReconcileAsync(BusinessKey(), 1, [["  ada.lovelace@example.test \n", "1"]])).Item2.Outcome);
    }

    [Fact]
    public async Task M9_an_empty_reconcile_key_is_unresolved_and_M10_no_key_values_are_logged()
    {
        var json = With(BusinessKey(), r =>
        {
            r["inputs"] = new JsonArray(JsonNode.Parse("""{"name":"email","type":"string","required":true}"""), JsonNode.Parse("""{"name":"taxId","type":"string","sensitive":true}"""));
        });
        var harness = await ExecutorHarness.CreateAsync();
        harness.ConfigureSession = s =>
        {
            s.Counts["tr.customer"] = 1;
            s.Rows = new RowSnapshot(1, [["x", "1"]]);
        };

        var result = await harness.Executor.ExecuteAsync(Request(Valid(json), Guid.NewGuid(), ExecutionIntent.Reconcile, record: new Dictionary<string, string?> { ["email"] = "   " }), default);

        Assert.Equal((WorkflowOutcome.Failed, EngineErrorCodes.VariableUnresolved), (result.Outcome, result.Error!.Code));
        var found = await ReconcileAsync(BusinessKey(), 1, [["ada.lovelace@example.test", "C-77"]]);
        Assert.DoesNotContain(found.Item1.Logger.Messages, m => m.Contains("ada.lovelace", StringComparison.Ordinal) || m.Contains("C-77", StringComparison.Ordinal));
    }

    // ----- Registry and DI -----

    private sealed class MismatchedHandler : IActionHandler
    {
        public ActionType Type { get; init; } = ActionType.Click;

        public Type ConfigType { get; init; } = typeof(FillConfig);

        public Task<StepOutcome> ExecuteAsync(StepContext context, ActionConfig config, CancellationToken cancellationToken) => Task.FromResult(StepOutcome.Empty);
    }

    [Fact]
    public void The_registry_requires_exactly_one_matching_handler_per_catalog_action()
    {
        var all = ActionRegistry.CreateDefaultHandlers();
        Assert.NotNull(new ActionRegistry(all));
        Assert.Equal(13, all.Count);

        Assert.Contains("no handler for 'reconcileMatch'", Assert.Throws<InvalidOperationException>(() => new ActionRegistry(all.Where(h => h.Type != ActionType.ReconcileMatch))).Message, StringComparison.Ordinal);
        Assert.Contains("duplicate handlers", Assert.Throws<InvalidOperationException>(() => new ActionRegistry([.. all, new ClickHandler()])).Message, StringComparison.Ordinal);
        Assert.Contains("unknown action type", Assert.Throws<InvalidOperationException>(() => new ActionRegistry([.. all, new MismatchedHandler { Type = (ActionType)99 }])).Message, StringComparison.Ordinal);
        Assert.Contains("expects FillConfig", Assert.Throws<InvalidOperationException>(() => new ActionRegistry([.. all.Where(h => h.Type != ActionType.Click), new MismatchedHandler()])).Message, StringComparison.Ordinal);
    }
}
