using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Records;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Automation.Browser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Automation.Workflows;

public interface IWorkflowExecutor
{
    /// <summary>Runs ONE attempt (or refuses it) with a fresh browser session that is always disposed (§7.3).</summary>
    Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Server options of the workflow engine (section <c>Automation:Workflows</c>), validated at startup (§1.6).</summary>
public sealed class WorkflowEngineOptions
{
    public const string SectionName = "Automation:Workflows";

    public int ClockSkewSeconds { get; set; } = 5;

    public int MaxReconcileFailures { get; set; } = 3;

    public int MaxAmbiguousAttempts { get; set; } = 3;

    public RepeatSafetyPolicy ToPolicy() => RepeatSafetyPolicy.Create(ClockSkewSeconds, MaxReconcileFailures, MaxAmbiguousAttempts);
}

public sealed class WorkflowEngineOptionsValidator : IValidateOptions<WorkflowEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkflowEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.ToPolicy();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentOutOfRangeException)
        {
            return ValidateOptionsResult.Fail("Automation:Workflows: ClockSkewSeconds must be 0..60, MaxReconcileFailures 1..10, MaxAmbiguousAttempts 1..10.");
        }
    }
}

/// <summary>Test seam (internal): pause points around the checkpoint, used to simulate frozen Workers (F1, F4).</summary>
internal sealed class ExecutorTestHooks
{
    public Func<string, Task>? BeforeCheckpoint { get; init; }

    public Func<string, Task>? AfterCheckpoint { get; init; }
}

/// <summary>
/// The workflow executor (docs/PHASE4_PLAN.md §7.3). One call = at most one attempt:
/// <list type="number">
/// <item>verify the definition hash against the pinned version and validate the record (refusals, nothing journaled);</item>
/// <item>reserve in the journal, evaluate repeat safety on the journal's history, and refuse unless admitted;</item>
/// <item>bind parameters and the egress policy from server options; start the attempt;</item>
/// <item>run setup, then reconcile (never followed by main steps) or steps; a side-effect handler dispatches only after
/// a durable checkpoint;</item>
/// <item>always dispose the session (revocation barrier), clear secrets and complete the journal entry.</item>
/// </list>
/// Exactly one terminal outcome is set; after a side effect only a declared permanent business failure gives Failed and
/// every other end is Indeterminate (R8). Logs carry ids, types, durations and codes only, never values.
/// </summary>
public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private readonly ActionRegistry _registry;
    private readonly IAttemptJournal _journal;
    private readonly IBrowserSessionFactory _sessions;
    private readonly ISecretProvider _secrets;
    private readonly IExecutionFileProvider? _files;
    private readonly WorkerIncarnation _incarnation;
    private readonly RepeatSafetyPolicy _policy;
    private readonly BindingOptions _binding;
    private readonly UploadPolicy _uploads;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkflowExecutor> _logger;

    public WorkflowExecutor(
        ActionRegistry registry,
        IAttemptJournal journal,
        IBrowserSessionFactory sessions,
        ISecretProvider secrets,
        WorkerIncarnation incarnation,
        IOptions<WorkflowEngineOptions> engineOptions,
        IOptions<BrowserEngineOptions> browserOptions,
        TimeProvider clock,
        ILogger<WorkflowExecutor> logger,
        IExecutionFileProvider? files = null)
    {
        ArgumentNullException.ThrowIfNull(engineOptions);
        ArgumentNullException.ThrowIfNull(browserOptions);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _incarnation = incarnation ?? throw new ArgumentNullException(nameof(incarnation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _files = files;
        _policy = engineOptions.Value.ToPolicy();
        (_binding, _uploads) = MapServerOptions(browserOptions.Value);
    }

    internal ExecutorTestHooks? Hooks { get; set; }

    /// <summary>Server options → binding (§6.2): Development flag, trusted private origins, session and upload limits.</summary>
    public static (BindingOptions Binding, UploadPolicy Uploads) MapServerOptions(BrowserEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var extensions = (options.Uploads?.AllowedExtensions ?? []).ToImmutableArray();
        var maxBytes = options.Uploads?.MaxBytes ?? new UploadLimits().MaxBytes;
        var binding = new BindingOptions
        {
            IsDevelopment = options.Mode == ExecutionMode.Development,
            DevelopmentPrivateNetworkOrigins = options.Mode == ExecutionMode.Development ? [.. options.DevelopmentPrivateNetworkOrigins] : [],
            MaxSessionSeconds = options.Timeouts.SessionSeconds,
            UploadAllowedExtensions = extensions,
            MaxUploadBytes = maxBytes,
        };
        return (binding, new UploadPolicy(maxBytes, extensions));
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workflow = request.Workflow;
        var definition = workflow.Definition;
        var now = _clock.GetUtcNow();

        // ----- Refusals before anything is journaled -----
        if (!workflow.VerifyAgainst(request.RecordedDefinitionHash))
        {
            return Refused(EngineErrorCodes.WorkflowTampered, ErrorCategory.Permanent, "The definition does not match the pinned version's hash.", null, now);
        }

        var (record, recordErrors) = RecordValidator.Validate(definition, request.Record);
        if (record is null)
        {
            return Refused(EngineErrorCodes.RecordInvalid, ErrorCategory.Permanent, $"The record is invalid ({recordErrors.Length} problem(s)).", null, now);
        }

        if (request.Intent == ExecutionIntent.Reconcile && !definition.HasReconcileSection)
        {
            return Refused(ExecutionErrorCodes.ReconcileNotAvailable, ErrorCategory.Policy, "The workflow has no reconcile section.", null, now);
        }

        AttemptReservationResult reservation;
        try
        {
            reservation = await _journal.ReserveAsync(request.Scope.OrganizationId, request.OperationId, request.Intent, _incarnation.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refused(ExecutionErrorCodes.JournalUnavailable, ErrorCategory.Transient, "The attempt journal is unavailable.", null, now);
        }

        now = reservation.Now;
        var current = CurrentWorkflow.From(workflow);
        var decision = RepeatSafetyEvaluator.Evaluate(current, request.OperationId, reservation.History, reservation.Now, _policy);
        switch (reservation.Status)
        {
            case ReservationStatus.InFlight:
                return Refused(ExecutionErrorCodes.AttemptInFlight, ErrorCategory.Policy, "Another attempt for this operation is in flight.", decision, now);
            case ReservationStatus.AwaitingFence:
                return Refused(ExecutionErrorCodes.AwaitingFence, ErrorCategory.Policy, "An earlier attempt could still dispatch; waiting for a fence.", decision, now);
            case ReservationStatus.UnknownIncarnation:
                return Refused(ExecutionErrorCodes.JournalUnavailable, ErrorCategory.Transient, "This Worker incarnation is not registered.", decision, now);
        }

        var slot = reservation.Reservation!;
        if (!RepeatSafetyEvaluator.Admits(request.Intent, decision, definition.HasReconcileSection))
        {
            await ReleaseQuietlyAsync(slot);
            var code = decision.Reason == RepeatReason.HistoryInvalid ? ExecutionErrorCodes.HistoryInvalid : ExecutionErrorCodes.RepeatNotAuthorized;
            return Refused(code, ErrorCategory.Policy, $"Repeat not authorized ({decision.Kind}/{decision.Reason}).", decision, now);
        }

        var (bound, bindErrors) = WorkflowBinder.Bind(definition, request.Parameters, _binding);
        if (bound is null)
        {
            await ReleaseQuietlyAsync(slot);
            var timeout = bindErrors.Any(e => e.Code == EngineErrorCodes.WorkflowTimeoutExceedsServerLimit);
            return Refused(timeout ? EngineErrorCodes.WorkflowTimeoutExceedsServerLimit : EngineErrorCodes.PolicyViolation, ErrorCategory.Policy,
                $"Binding failed ({bindErrors.Length} problem(s)).", decision, now);
        }

        DateTimeOffset startedAt;
        try
        {
            startedAt = await _journal.StartAsync(slot, AttemptSnapshot.From(workflow, request.WorkflowVersion), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReleaseQuietlyAsync(slot);
            return Refused(ExecutionErrorCodes.JournalUnavailable, ErrorCategory.Transient, "The attempt could not be started in the journal.", decision, now);
        }

        // ----- From here on this is an attempt with exactly one terminal outcome -----
        var scope = request.Scope with { AttemptNumber = slot.AttemptNumber };
        var run = new AttemptRun(request, slot, scope, bound, record, definition);
        return await RunAttemptAsync(run, startedAt, cancellationToken);
    }

    private async Task<WorkflowExecutionResult> RunAttemptAsync(AttemptRun run, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var definition = run.Definition;
        _logger.LogInformation("Workflow attempt started {DefinitionHash} {WorkflowVersion} {OperationId} {Intent} {Attempt}",
            run.Request.Workflow.DefinitionHash, run.Request.WorkflowVersion, run.Request.OperationId, run.Request.Intent, run.Slot.AttemptNumber);

        IBrowserSession? session = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        try
        {
            foreach (var name in definition.Secrets)
            {
                try
                {
                    run.Secrets[name] = await _secrets.GetAsync(run.Scope, name, deadline.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.SetError(new ExecutionError(ExecutionErrorCodes.SecretUnavailable, ErrorCategory.Transient, true, null, "A secret could not be resolved."));
                    return await FinishAsync(run, session, startedAt);
                }
            }

            try
            {
                session = await _sessions.CreateAsync(
                    new BrowserSessionRequest(run.Scope, run.Bound.EgressPolicy) { AcceptDownloads = run.Request.Workflow.Safety.RequiresDownloads },
                    deadline.Token);
                foreach (var mask in definition.MaskSelectors)
                {
                    session.RegisterSensitive(string.Empty, Selectors.ToElement(Selectors.Resolve(mask, run.Values, run.Inputs)));
                }
            }
            catch (BrowserOperationException ex)
            {
                run.SetError(ErrorMapping.FromBrowser(ex, null));
                return await FinishAsync(run, session, startedAt);
            }
            catch (StepFailureException ex)
            {
                run.SetError(ex.Error);
                return await FinishAsync(run, session, startedAt);
            }

            await RunSequenceAsync(definition.Setup, Section.Setup, session, run, deadline.Token);
            if (!run.HasTerminal)
            {
                if (run.Request.Intent == ExecutionIntent.Reconcile)
                {
                    run.ObservedAt = await _journal.MarkObservationAsync(run.Slot, CancellationToken.None);
                    await RunSequenceAsync(definition.Reconcile, Section.Reconcile, session, run, deadline.Token);
                }
                else
                {
                    await RunSequenceAsync(definition.Steps, Section.Steps, session, run, deadline.Token);
                }
            }

            if (!run.HasTerminal)
            {
                // Unreachable for a validated workflow (explicit finalization); fail closed rather than succeed implicitly.
                run.SetError(new ExecutionError("MissingFinalization", ErrorCategory.Permanent, false, null, "The attempt ended without a terminal outcome."));
            }
        }
        catch (OperationCanceledException)
        {
            run.SetCancelled();
        }
        catch (AttemptJournalException)
        {
            run.SetError(new ExecutionError(ExecutionErrorCodes.JournalUnavailable, ErrorCategory.Transient, true, null, "The attempt journal failed during the attempt."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: an unexpected failure still ends the attempt (Indeterminate after a side effect), and the
            // session is still disposed and the journal entry completed below. The exception text is never logged.
            _logger.LogError("Workflow attempt failed unexpectedly {OperationId} {Attempt} {ExceptionType}", run.Request.OperationId, run.Slot.AttemptNumber, ex.GetType().Name);
            run.SetError(new ExecutionError("EngineError", ErrorCategory.Permanent, false, null, "The engine failed unexpectedly."));
        }

        return await FinishAsync(run, session, startedAt);
    }

    private async Task<WorkflowExecutionResult> FinishAsync(AttemptRun run, IBrowserSession? session, DateTimeOffset startedAt)
    {
        var fenced = true; // no session = nothing could dispatch
        if (session is not null)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                run.Diagnostics.Add("SessionDisposeFailed");
            }

            fenced = session.EgressRevoked;
        }

        run.Secrets.Clear();
        var outcome = run.Outcome!.Value;
        var sideEffect = run.SideEffectStarted ? SideEffectState.Started : SideEffectState.NotStarted;
        var evidence = run.Finding is { } finding && run.ObservedAt is { } observedAt && ReconcilePolicySnapshot.From(run.Definition.Reconciliation) is { } policy
            ? new ReconciliationEvidence(finding, observedAt, policy)
            : null;

        var finishedAt = startedAt;
        try
        {
            finishedAt = await _journal.CompleteAsync(run.Slot, new AttemptCompletion(outcome, run.Error?.ToSummary(), sideEffect, fenced, evidence), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostic only: the outcome is unchanged; with a checkpoint the entry stays unfenced until fenced (J5).
            run.Diagnostics.Add("JournalCompletionFailed");
        }

        _logger.LogInformation("Workflow attempt completed {OperationId} {Attempt} {Outcome} {ErrorCode} {SideEffect} {ExecutionFenced}",
            run.Request.OperationId, run.Slot.AttemptNumber, outcome, run.Error?.Code, sideEffect, fenced);

        return new WorkflowExecutionResult(outcome, run.Slot.AttemptNumber, run.Error, sideEffect, fenced, null, evidence,
            run.Results.ToImmutableDictionary(StringComparer.Ordinal), [.. run.Steps], [.. run.Artifacts], [.. run.Diagnostics], startedAt, finishedAt);
    }

    private enum Section
    {
        Setup,
        Reconcile,
        Steps,
    }

    private async Task RunSequenceAsync(ImmutableArray<WorkflowStep> steps, Section section, IBrowserSession session, AttemptRun run, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            if (run.HasTerminal)
            {
                return;
            }

            await RunStepAsync(step, section, session, run, cancellationToken);
        }
    }

    private async Task RunStepAsync(WorkflowStep step, Section section, IBrowserSession session, AttemptRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = ActionCatalog.Get(step.Type);
        var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs ?? descriptor.DefaultTimeout.TotalMilliseconds);
        var isSideEffect = section == Section.Steps && step.SideEffect == true && run.Request.Intent == ExecutionIntent.Normal;
        var checkpointed = false;

        async Task BeforeSideEffect(CancellationToken token)
        {
            if (!isSideEffect || checkpointed)
            {
                return;
            }

            if (Hooks?.BeforeCheckpoint is { } before)
            {
                await before(step.Id);
            }

            try
            {
                // Durable and fenced; the handler dispatches only after this returns (J1).
                await _journal.SideEffectStartingAsync(run.Slot, step.Id, timeout, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new CheckpointFailedException("The side-effect checkpoint could not be written.", ex);
            }

            checkpointed = true;
            run.SideEffectStarted = true;
            if (Hooks?.AfterCheckpoint is { } after)
            {
                await after(step.Id);
            }
        }

        var context = new StepContext(session, step, OperationOptions.WithTimeout(timeout), run.Values, run.Inputs, run.Bound, run.Scope, _files, _uploads, BeforeSideEffect);
        var clock = Stopwatch.StartNew();
        ExecutionError? error = null;
        StepOutcome? outcome = null;
        try
        {
            outcome = await _registry.Get(step.Type).ExecuteAsync(context, step.Action, cancellationToken);
            if (isSideEffect && !checkpointed)
            {
                throw new InvalidOperationException($"Handler for '{descriptor.JsonName}' dispatched a side-effect step without a checkpoint.");
            }
        }
        catch (CheckpointFailedException)
        {
            error = new ExecutionError(ExecutionErrorCodes.CheckpointFailed, ErrorCategory.Transient, true, step.Id, "The side-effect checkpoint could not be written; nothing was dispatched.");
        }
        catch (StepFailureException ex)
        {
            error = ex.Error with { StepId = step.Id };
        }
        catch (BrowserOperationException ex) when (ex.Code == BrowserErrorCode.SessionTimeout)
        {
            throw new OperationCanceledException("The session deadline expired.");
        }
        catch (BrowserOperationException ex)
        {
            error = ErrorMapping.FromBrowser(ex, step.Id);
        }

        clock.Stop();
        if (error is not null)
        {
            var continued = step.OnError == StepErrorMode.Continue && descriptor.ContinueAllowed && !isSideEffect;
            run.SetOutputs(step.Id, ImmutableDictionary<string, (string, bool)>.Empty, failed: true);
            run.Steps.Add(new StepRecord(step.Id, step.Type, continued ? StepStatus.FailedContinued : StepStatus.Failed, clock.ElapsedMilliseconds, error.Code));
            _logger.LogInformation("Workflow step {StepId} {StepType} failed {ErrorCode} {Continued} {DurationMs}", step.Id, step.Type, error.Code, continued, clock.ElapsedMilliseconds);
            if (!continued)
            {
                run.SetError(error);
            }

            return;
        }

        run.SetOutputs(step.Id, outcome!.Outputs, failed: false);
        run.Artifacts.AddRange(outcome.Artifacts);
        run.Steps.Add(new StepRecord(step.Id, step.Type, StepStatus.Succeeded, clock.ElapsedMilliseconds, null));
        _logger.LogInformation("Workflow step {StepId} {StepType} succeeded {DurationMs}", step.Id, step.Type, clock.ElapsedMilliseconds);

        switch (step.Action)
        {
            case ConditionConfig condition:
                await RunSequenceAsync(outcome.ConditionResult == true ? condition.Then : condition.Else, section, session, run, cancellationToken);
                break;
            case CaptureResultConfig:
                if (run.WriteResults(outcome.Fields, step.Id) is { } resultError)
                {
                    run.SetError(resultError);
                }
                else if (outcome.Outcome is { } declared)
                {
                    run.SetCapture(declared, outcome.OutcomeTransient, step.Id);
                }

                break;
            case ReconcileMatchConfig:
                if (outcome.Finding == ReconcileFindingKind.Found && run.WriteResults(outcome.Fields, step.Id) is { } fieldError)
                {
                    run.SetError(fieldError);
                }
                else
                {
                    run.SetFinding(outcome.Finding!.Value, step.Id);
                }

                break;
        }
    }

    private async Task ReleaseQuietlyAsync(AttemptReservation slot)
    {
        try
        {
            await _journal.ReleaseAsync(slot, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: an unreleased Reserved entry expires with its lease and was never an attempt.
        }
    }

    private static WorkflowExecutionResult Refused(string code, ErrorCategory category, string message, RepeatDecision? decision, DateTimeOffset at) =>
        new(WorkflowOutcome.Refused, null, new ExecutionError(code, category, category == ErrorCategory.Transient, null, message), SideEffectState.NotStarted, true,
            decision, null, ImmutableDictionary<string, string>.Empty, [], [], [], at, at);

    /// <summary>The state of one attempt; the terminal outcome is set exactly once (TrySetTerminal semantics).</summary>
    private sealed class AttemptRun
    {
        public AttemptRun(WorkflowExecutionRequest request, AttemptReservation slot, ExecutionScope scope, BoundWorkflow bound, ValidatedRecord record, WorkflowDefinition definition)
        {
            Request = request;
            Slot = slot;
            Scope = scope;
            Bound = bound;
            Definition = definition;
            Inputs = TemplateResolver.InputLookup(definition);
            Values = new RunValues(request.OperationId, record, bound, Secrets, Outputs);
        }

        public WorkflowExecutionRequest Request { get; }

        public AttemptReservation Slot { get; }

        public ExecutionScope Scope { get; }

        public BoundWorkflow Bound { get; }

        public WorkflowDefinition Definition { get; }

        public IReadOnlyDictionary<string, WorkflowInput> Inputs { get; }

        public RunValues Values { get; }

        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ImmutableDictionary<string, (string Value, bool Sensitive)>> Outputs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);

        public List<StepRecord> Steps { get; } = [];

        public List<ArtifactDescriptor> Artifacts { get; } = [];

        public List<string> Diagnostics { get; } = [];

        public bool SideEffectStarted { get; set; }

        public DateTimeOffset? ObservedAt { get; set; }

        public ReconciliationFinding? Finding { get; private set; }

        public WorkflowOutcome? Outcome { get; private set; }

        public ExecutionError? Error { get; private set; }

        public bool HasTerminal => Outcome is not null;

        public void SetOutputs(string stepId, ImmutableDictionary<string, (string Value, bool Sensitive)> outputs, bool failed) =>
            Outputs[stepId] = outputs.SetItem(ActionCatalog.FailedOutput, (failed ? "true" : "false", false));

        /// <summary>A terminal error (§9.1/§9.2): after a side effect every error is Indeterminate, keeping its code.</summary>
        public void SetError(ExecutionError error)
        {
            if (Request.Intent == ExecutionIntent.Reconcile)
            {
                TrySet(WorkflowOutcome.Failed, error);
            }
            else
            {
                TrySet(SideEffectStarted ? WorkflowOutcome.Indeterminate : WorkflowOutcome.Failed, error);
            }
        }

        public void SetCancelled()
        {
            var error = new ExecutionError(ExecutionErrorCodes.Cancelled, ErrorCategory.Cancelled, false, null, "The attempt was cancelled or exceeded its deadline.");
            TrySet(Request.Intent == ExecutionIntent.Normal && SideEffectStarted ? WorkflowOutcome.Indeterminate : WorkflowOutcome.Cancelled, error);
        }

        /// <summary>A terminal captureResult: only a declared permanent business failure is Failed after a side effect (R8).</summary>
        public void SetCapture(OutcomeSpec declared, bool transient, string stepId)
        {
            if (declared.Status == OutcomeStatus.Succeeded)
            {
                TrySet(WorkflowOutcome.Succeeded, null);
                return;
            }

            var error = new ExecutionError(declared.Code!, ErrorCategory.Business, transient, stepId, "The workflow declared a failed outcome.");
            TrySet(SideEffectStarted && transient ? WorkflowOutcome.Indeterminate : WorkflowOutcome.Failed, error);
        }

        public void SetFinding(ReconcileFindingKind finding, string stepId)
        {
            switch (finding)
            {
                case ReconcileFindingKind.Found:
                    Finding = ReconciliationFinding.Found;
                    TrySet(WorkflowOutcome.ReconciledFound, null);
                    break;
                case ReconcileFindingKind.NotFound:
                    Finding = ReconciliationFinding.NotFound;
                    TrySet(WorkflowOutcome.ReconciledNotFound, null);
                    break;
                case ReconcileFindingKind.Inconclusive:
                    TrySet(WorkflowOutcome.Failed, new ExecutionError(ExecutionErrorCodes.LookupInconclusive, ErrorCategory.Transient, true, stepId, "No rows and no empty indicator."));
                    break;
                default:
                    Finding = ReconciliationFinding.Ambiguous;
                    var code = finding == ReconcileFindingKind.TooManyRows ? ExecutionErrorCodes.TooManyRows : ExecutionErrorCodes.AmbiguousMatch;
                    TrySet(WorkflowOutcome.NeedsReview, new ExecutionError(code, ErrorCategory.Business, false, stepId, "The lookup was not an unambiguous exact match."));
                    break;
            }
        }

        /// <summary>Writes result fields with the run-time overwrite backstop and the §1.6 size limits.</summary>
        public ExecutionError? WriteResults(ImmutableArray<KeyValuePair<string, string>> fields, string stepId)
        {
            foreach (var (name, value) in fields)
            {
                if (Results.ContainsKey(name))
                {
                    return new ExecutionError(ExecutionErrorCodes.ResultOverwrite, ErrorCategory.Permanent, false, stepId, $"Result field '{name}' was already written.");
                }

                Results[name] = value;
            }

            var total = Results.Sum(r => Encoding.UTF8.GetByteCount(r.Key) + Encoding.UTF8.GetByteCount(r.Value));
            return Results.Count > WorkflowLimits.MaxResultFields || total > WorkflowLimits.MaxResultTotalBytes
                ? new ExecutionError(ExecutionErrorCodes.ResultTooLarge, ErrorCategory.Permanent, false, stepId, "The results exceed the size limits.")
                : null;
        }

        private void TrySet(WorkflowOutcome outcome, ExecutionError? error)
        {
            if (Outcome is null)
            {
                Outcome = outcome;
                Error = error;
            }
        }
    }

    /// <summary>The run-time template values of one attempt.</summary>
    private sealed class RunValues(
        Guid operationId,
        ValidatedRecord record,
        BoundWorkflow bound,
        Dictionary<string, string> secrets,
        Dictionary<string, ImmutableDictionary<string, (string Value, bool Sensitive)>> outputs) : ITemplateValues
    {
        public Guid OperationId => operationId;

        public bool TryGetRecordValue(string name, out string? value)
        {
            var found = record.Values.TryGetValue(name, out var text);
            value = text;
            return found;
        }

        public bool TryGetParameter(string name, out string value)
        {
            var found = bound.TextParameters.TryGetValue(name, out var text);
            value = text ?? string.Empty;
            return found;
        }

        public bool TryGetSecret(string name, out string value)
        {
            var found = secrets.TryGetValue(name, out var text);
            value = text ?? string.Empty;
            return found;
        }

        public bool TryGetStepOutput(string stepId, string output, out string value, out bool isSensitive)
        {
            if (outputs.TryGetValue(stepId, out var values) && values.TryGetValue(output, out var entry))
            {
                (value, isSensitive) = entry;
                return true;
            }

            (value, isSensitive) = (string.Empty, false);
            return false;
        }
    }
}
