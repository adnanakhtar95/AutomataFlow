using System.Collections.Immutable;
using AutomataFlow.Application.Network;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>Static properties of a validated workflow, recorded in every attempt snapshot (§10.1).</summary>
public sealed class WorkflowSafetyProfile
{
    internal WorkflowSafetyProfile(bool hasSideEffects, bool targetEnforced, bool operationIdSent, bool requiresDownloads)
    {
        HasSideEffects = hasSideEffects;
        TargetEnforced = targetEnforced;
        OperationIdSent = operationIdSent;
        RequiresDownloads = requiresDownloads;
    }

    /// <summary>At least one <c>sideEffect: true</c> step exists.</summary>
    public bool HasSideEffects { get; }

    /// <summary>At least one side-effect step, and <b>every</b> side-effect step attests <c>targetEnforced</c> idempotency.</summary>
    public bool TargetEnforced { get; }

    /// <summary>At least one side-effect step, and <b>every</b> one is dominated by a fill/select of exactly <c>{{operation.id}}</c>.</summary>
    public bool OperationIdSent { get; }

    /// <summary>The workflow contains a <c>download</c> step, so the session accepts downloads.</summary>
    public bool RequiresDownloads { get; }
}

/// <summary>The section rules, dominance, sinks, side effects, finalization and reconciliation (§2, §4, §5, §9, §10, §16 D3/D4; ADR-063).</summary>
internal sealed partial class WorkflowSemanticValidator
{
    private enum Section
    {
        Setup,
        Reconcile,
        Steps,
    }

    /// <summary>What dominates the current point: visible step outputs, result fields already written, op-id sent.</summary>
    private sealed record PathState(ImmutableDictionary<string, StepOutputs> Visible, ImmutableHashSet<string> Fields, bool OperationIdSent);

    private readonly List<(WorkflowStep Step, bool OperationIdSent)> _sideEffects = [];
    private readonly HashSet<string> _resultFieldNames = new(StringComparer.Ordinal);
    private TemplateScope _scope = null!;
    private bool _requiresDownloads;
    private bool _hasSensitiveExtract;

    private WorkflowSafetyProfile ValidateSteps()
    {
        var allIds = Flatten(_definition.Setup).Concat(Flatten(_definition.Reconcile)).Concat(Flatten(_definition.Steps)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _scope = new TemplateScope(_definition, allIds, ImmutableDictionary<string, StepOutputs>.Empty);

        var start = new PathState(ImmutableDictionary.Create<string, StepOutputs>(StringComparer.Ordinal), ImmutableHashSet.Create<string>(StringComparer.Ordinal), false);
        var afterSetup = WalkSequence(_definition.Setup, Section.Setup, 0, start, "$.setup");
        WalkSequence(_definition.Reconcile, Section.Reconcile, 0, afterSetup, "$.reconcile");
        WalkSequence(_definition.Steps, Section.Steps, 0, afterSetup, "$.steps");

        CheckUnreachable(_definition.Setup);
        CheckUnreachable(_definition.Reconcile);
        CheckUnreachable(_definition.Steps);
        CheckFinalization(_definition.Steps, "$.steps");
        CheckReconcileSection();

        if (_resultFieldNames.Count > WorkflowLimits.MaxResultFields)
        {
            _diagnostics.Add("$", "too-many-result-fields", $"A workflow can produce at most {WorkflowLimits.MaxResultFields} distinct result fields.");
        }

        var hasSideEffects = _sideEffects.Count > 0;
        var profile = new WorkflowSafetyProfile(
            hasSideEffects,
            hasSideEffects && _sideEffects.All(s => s.Step.Idempotency is { Guarantee: IdempotencyGuarantee.TargetEnforced }),
            hasSideEffects && _sideEffects.All(s => s.OperationIdSent),
            _requiresDownloads);

        CheckReconcileKey(profile);
        return profile;
    }

    private PathState WalkSequence(ImmutableArray<WorkflowStep> steps, Section section, int depth, PathState state, string path)
    {
        foreach (var step in steps)
        {
            state = WalkStep(step, section, depth, state);
        }

        return state;
    }

    private PathState WalkStep(WorkflowStep step, Section section, int depth, PathState state)
    {
        var descriptor = ActionCatalog.Get(step.Type);
        var path = step.Path;
        var scope = _scope.With(state.Visible);

        // ----- Placement, onError, side effects -----
        if (!descriptor.AllowedSections.HasFlag(SectionFlag(section)))
        {
            _diagnostics.Add(path + ".type", "step-section", $"'{descriptor.JsonName}' is not allowed in '{SectionName(section)}'.");
        }

        if (step.OnError == StepErrorMode.Continue && !descriptor.ContinueAllowed)
        {
            _diagnostics.Add(path + ".onError", "on-error-continue-not-allowed",
                $"'onError: continue' is not allowed on '{descriptor.JsonName}' (allowed on wait, extract, screenshot, fill, select and condition).");
        }

        var isSideEffect = false;
        if (descriptor.SideEffectAllowed)
        {
            if (section == Section.Steps && step.SideEffect is null)
            {
                _diagnostics.Add(path + ".sideEffect", "side-effect-required", $"'sideEffect' must be declared (true or false) on every '{descriptor.JsonName}' in 'steps'.");
            }
            else if (section != Section.Steps && step.SideEffect == true)
            {
                _diagnostics.Add(path + ".sideEffect", "side-effect-not-allowed", $"Side effects are not allowed in '{SectionName(section)}'.");
            }

            isSideEffect = section == Section.Steps && step.SideEffect == true;
        }
        else if (step.SideEffect is not null)
        {
            _diagnostics.Add(path + ".sideEffect", "side-effect-forbidden", $"'sideEffect' is not allowed on '{descriptor.JsonName}'.");
        }

        var effectiveTimeoutMs = step.TimeoutMs ?? (int)descriptor.DefaultTimeout.TotalMilliseconds;
        if (isSideEffect)
        {
            if (effectiveTimeoutMs < WorkflowLimits.MinSideEffectStepTimeoutMs)
            {
                _diagnostics.Add(path + ".timeoutMs", "side-effect-timeout", $"Side-effect steps need a timeout of at least {WorkflowLimits.MinSideEffectStepTimeoutMs} ms.");
            }

            _sideEffects.Add((step, state.OperationIdSent));
        }

        if (step.Idempotency is not null)
        {
            if (!isSideEffect)
            {
                _diagnostics.Add(path + ".idempotency", "idempotency-not-allowed", "'idempotency' is only allowed on side-effect steps.");
            }
            else if (!state.OperationIdSent)
            {
                _diagnostics.Add(path + ".idempotency", "idempotency-key-not-sent",
                    "An idempotency attestation needs an earlier fill or select of exactly {{operation.id}} that always runs before this step.");
            }
        }

        // ----- Per-action configuration -----
        var next = state;
        switch (step.Action)
        {
            case NavigateConfig navigate:
                CheckNavigate(navigate, path + ".config", scope);
                break;
            case ClickConfig click:
                CheckSelector(click.Selector, path + ".config.selector", scope);
                if (section == Section.Steps && click.WaitForNavigation && step.SideEffect == false)
                {
                    _diagnostics.Warn(path + ".sideEffect", "navigating-click-without-side-effect", "A click that waits for navigation is likely a form submit; check that 'sideEffect: false' is correct.");
                }

                break;
            case FillConfig fill:
                CheckSelector(fill.Selector, path + ".config.selector", scope);
                CheckTemplate(fill.Value, TemplateSink.FillValue, scope, path + ".config.value");
                next = MarkOperationIdSent(step, fill.Value, next);
                break;
            case SelectConfig select:
                CheckSelector(select.Selector, path + ".config.selector", scope);
                if (select.Text is not null)
                {
                    CheckTemplate(select.Text, TemplateSink.SelectValue, scope, path + ".config." + (select.By == SelectBy.Value ? "value" : "label"));
                    next = MarkOperationIdSent(step, select.Text, next);
                }

                break;
            case WaitConfig wait when wait.Selector is not null:
                CheckSelector(wait.Selector, path + ".config.selector", scope);
                break;
            case ExtractConfig extract:
                CheckSelector(extract.Selector, path + ".config.selector", scope);
                _hasSensitiveExtract |= extract.Sensitive;
                break;
            case ScreenshotConfig screenshot:
                CheckTemplate(screenshot.Label, TemplateSink.ScreenshotLabel, scope, path + ".config.label");
                break;
            case UploadConfig upload:
                CheckSelector(upload.Selector, path + ".config.selector", scope);
                CheckTemplate(upload.File, TemplateSink.UploadFile, scope, path + ".config.file");
                break;
            case DownloadConfig download:
                CheckSelector(download.Trigger, path + ".config.trigger", scope);
                _requiresDownloads = true;
                break;
            case DelayConfig delay when delay.Milliseconds >= effectiveTimeoutMs:
                _diagnostics.Add(path + ".config.milliseconds", "delay-exceeds-timeout", "The delay must be shorter than the step timeout.");
                break;
            case ConditionConfig condition:
                CheckCondition(condition.If, path + ".config.if", scope);
                var afterThen = WalkSequence(condition.Then, section, depth + 1, next, path + ".config.then");
                var afterElse = WalkSequence(condition.Else, section, depth + 1, next, path + ".config.else");

                // Branch outputs are not visible after the condition; fields written on either branch are (no overwrite).
                next = next with { Fields = afterThen.Fields.Union(afterElse.Fields) };
                break;
            case CaptureResultConfig capture:
                if (section == Section.Setup && capture.Outcome is { Status: OutcomeStatus.Succeeded })
                {
                    _diagnostics.Add(path + ".config.outcome.status", "setup-outcome", "A captureResult in 'setup' can only end the attempt as 'failed'.");
                }

                foreach (var (name, template) in capture.Fields)
                {
                    CheckTemplate(template, TemplateSink.ResultField, scope, DiagnosticPath.Property(path + ".config.fields", name));
                }

                if (capture.Outcome?.TransientWhen is { } transientWhen)
                {
                    CheckCondition(transientWhen, path + ".config.outcome.transientWhen", scope);
                }

                next = WriteFields(capture.Fields.Select(f => f.Key), path + ".config.fields", next);
                break;
            case ReconcileMatchConfig match:
                if (depth > 0)
                {
                    _diagnostics.Add(path + ".type", "reconcile-match-position", "'reconcileMatch' must be a top-level step of 'reconcile'.");
                }

                CheckSelector(match.Rows, path + ".config.rows", scope);
                CheckSelector(match.EmptyIndicator, path + ".config.emptyIndicator", scope);
                if (match.Key.Text is not null)
                {
                    CheckSelector(match.Key.Text, path + ".config.key.text", scope);
                }

                CheckTemplate(match.EqualsTemplate, TemplateSink.ReconcileEquals, scope, path + ".config.equals");
                next = WriteFields(match.Fields.Select(f => f.Key), path + ".config.fields", next);
                break;
        }

        // ----- Outputs visible to dominated steps -----
        var outputs = new StepOutputs(
            [.. descriptor.Outputs, ActionCatalog.FailedOutput],
            step.Action is ExtractConfig { Sensitive: true } ? ["value"] : null);
        return next with { Visible = next.Visible.SetItem(step.Id, outputs) };
    }

    private static PathState MarkOperationIdSent(WorkflowStep step, Template value, PathState state) =>
        step.OnError == StepErrorMode.Fail && value.IsSinglePlaceholder(out var placeholder) && placeholder.Namespace == TemplateNamespace.Operation
            ? state with { OperationIdSent = true }
            : state;

    private PathState WriteFields(IEnumerable<string> names, string path, PathState state)
    {
        var fields = state.Fields;
        foreach (var name in names)
        {
            if (fields.Contains(name))
            {
                _diagnostics.Add(DiagnosticPath.Property(path, name), "result-overwrite", $"Result field '{name}' is already written earlier on this path.");
            }

            fields = fields.Add(name);
            _resultFieldNames.Add(name);
        }

        return state with { Fields = fields };
    }

    private void CheckNavigate(NavigateConfig navigate, string path, TemplateScope scope)
    {
        var url = navigate.Url;
        if (url.OriginParameter is { } parameter)
        {
            if (!scope.Parameters.TryGetValue(parameter, out var type))
            {
                _diagnostics.Add(path + ".url", TemplateErrorCodes.UndeclaredParameter, $"Parameter '{parameter}' is not declared.");
            }
            else if (type != WorkflowParameterType.Url)
            {
                _diagnostics.Add(path + ".url", TemplateErrorCodes.ParameterMisuse, $"Parameter '{parameter}' must be of type 'url' to be a URL origin.");
            }
            else if (!_definition.AllowedHosts.Any(h => AllowedHosts.TryGetParameterName(h, out var name) && name == parameter))
            {
                _diagnostics.Add(path + ".url", UrlErrorCodes.OriginNotAllowed, $"The url parameter '{parameter}' must also be listed in allowedHosts.");
            }
        }
        else if (!LiteralOriginAllowed(url.LiteralOrigin!))
        {
            _diagnostics.Add(path + ".url", UrlErrorCodes.OriginNotAllowed, "The URL's origin does not match any literal allowedHosts entry.");
        }

        for (var i = 0; i < url.PathSegments.Length; i++)
        {
            if (!url.PathSegments[i].IsLiteral)
            {
                CheckTemplate(url.PathSegments[i], TemplateSink.UrlPathSegment, scope, path + ".url");
            }
        }

        foreach (var pair in url.Query)
        {
            CheckTemplate(pair.Value, TemplateSink.UrlQueryValue, scope, path + ".url");
        }
    }

    private bool LiteralOriginAllowed(OriginParts origin)
    {
        foreach (var entry in _definition.AllowedHosts)
        {
            if (AllowedHosts.TryGetParameterName(entry, out _))
            {
                continue;
            }

            try
            {
                if (new EgressPolicy([AllowedHostRule.Parse(entry)]).FindRule(origin.Host, origin.Port, origin.Scheme) is not null)
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Invalid entries were reported by the allowedHosts check.
            }
        }

        return false;
    }

    private void CheckCondition(ConditionExpr expression, string path, TemplateScope scope)
    {
        switch (expression)
        {
            case CombinedCondition combined:
                foreach (var child in combined.Children)
                {
                    CheckCondition(child, path, scope);
                }

                break;
            case NotCondition not:
                CheckCondition(not.Child, path, scope);
                break;
            case ElementCondition element:
                CheckSelector(element.Selector, path, scope);
                break;
            case TextCondition text:
                CheckSelector(text.Selector, path, scope);
                CheckTemplate(text.Value, TemplateSink.ConditionOperand, scope, path);
                break;
            case AttributeCondition attribute:
                CheckSelector(attribute.Selector, path, scope);
                CheckTemplate(attribute.Value, TemplateSink.ConditionOperand, scope, path);
                break;
            case ValueCondition value:
                CheckTemplate(value.Left, TemplateSink.ConditionOperand, scope, path);
                CheckTemplate(value.Right, TemplateSink.ConditionOperand, scope, path);
                break;
            case IsEmptyCondition empty:
                CheckTemplate(empty.Value, TemplateSink.ConditionOperand, scope, path);
                break;
        }
    }

    private void CheckTemplate(Template template, TemplateSink sink, TemplateScope scope, string path)
    {
        foreach (var error in TemplateChecker.Check(template, sink, scope, path).Errors)
        {
            _diagnostics.Add(error.Path, error.Code, error.Message);
        }
    }

    // ----- Structure: unreachable steps, explicit finalization, reconcile section -----

    private static bool Terminates(WorkflowStep step) => step.Action switch
    {
        CaptureResultConfig { IsTerminal: true } => true,
        ConditionConfig condition => Terminates(condition.Then) && Terminates(condition.Else),
        _ => false,
    };

    private static bool Terminates(ImmutableArray<WorkflowStep> steps) => !steps.IsEmpty && Terminates(steps[^1]);

    /// <summary>A terminal step must be the last of its sequence (in every section and branch).</summary>
    private void CheckUnreachable(ImmutableArray<WorkflowStep> steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            if (i < steps.Length - 1 && Terminates(steps[i]))
            {
                _diagnostics.Add(steps[i + 1].Path, "unreachable-step", "This step can never run: an earlier step always ends the attempt.");
            }

            if (steps[i].Action is ConditionConfig condition)
            {
                CheckUnreachable(condition.Then);
                CheckUnreachable(condition.Else);
            }
        }
    }

    /// <summary>§16 D3: every path through <c>steps</c> ends in a terminal captureResult; there is no implicit success.</summary>
    private void CheckFinalization(ImmutableArray<WorkflowStep> steps, string path)
    {
        if (steps.IsEmpty)
        {
            _diagnostics.Add(path, "missing-finalization", "Every path through 'steps' must end with a terminal captureResult (an 'outcome').");
            return;
        }

        var last = steps[^1];
        switch (last.Action)
        {
            case CaptureResultConfig { IsTerminal: true }:
                return;
            case ConditionConfig condition:
                CheckFinalization(condition.Then, last.Path + ".config.then");
                CheckFinalization(condition.Else, last.Path + ".config.else");
                return;
            default:
                _diagnostics.Add(last.Path, "missing-finalization", "Every path through 'steps' must end with a terminal captureResult (an 'outcome').");
                return;
        }
    }

    /// <summary>§10.3: the reconcile section ends with exactly one top-level reconcileMatch.</summary>
    private void CheckReconcileSection()
    {
        if (!_definition.HasReconcileSection)
        {
            return;
        }

        var reconcile = _definition.Reconcile;
        if (reconcile.IsEmpty || reconcile[^1].Type != ActionType.ReconcileMatch)
        {
            _diagnostics.Add("$.reconcile", "reconcile-match-required", "The 'reconcile' section must end with a reconcileMatch step.");
        }

        for (var i = 0; i < reconcile.Length - 1; i++)
        {
            if (reconcile[i].Type == ActionType.ReconcileMatch)
            {
                _diagnostics.Add(reconcile[i].Path + ".type", "reconcile-match-position", "'reconcileMatch' must be the last step of 'reconcile' and appear only once.");
            }
        }
    }

    /// <summary>§10.3 key rules for <c>reconcileMatch.equals</c>.</summary>
    private void CheckReconcileKey(WorkflowSafetyProfile profile)
    {
        if (_definition.Reconciliation is not { } policy || _definition.Reconcile.IsEmpty || _definition.Reconcile[^1].Action is not ReconcileMatchConfig match)
        {
            return;
        }

        var path = _definition.Reconcile[^1].Path + ".config.equals";
        var single = match.EqualsTemplate.IsSinglePlaceholder(out var placeholder);
        if (policy.MatchKey == MatchKeyKind.OperationId)
        {
            if (!single || placeholder.Namespace != TemplateNamespace.Operation)
            {
                _diagnostics.Add(path, "reconcile-key-mismatch", "With matchKey 'operationId', 'equals' must be exactly {{operation.id}}.");
            }
            else if (!profile.OperationIdSent)
            {
                _diagnostics.Add(path, "reconcile-key-not-sent", "With matchKey 'operationId', every side-effect step must be preceded by a fill or select of exactly {{operation.id}}.");
            }

            return;
        }

        var input = single && placeholder.Namespace == TemplateNamespace.Record ? _definition.Inputs.FirstOrDefault(i => i.Name == placeholder.Name) : null;
        if (input is null || !input.Required || input.Sensitive)
        {
            _diagnostics.Add(path, "reconcile-business-key", "With matchKey 'businessKey', 'equals' must be exactly one {{record.<input>}} of a required, non-sensitive input.");
        }
    }

    private static IEnumerable<WorkflowStep> Flatten(IEnumerable<WorkflowStep> steps) =>
        steps.SelectMany(s => s.Action is ConditionConfig condition ? [s, .. Flatten(condition.Then), .. Flatten(condition.Else)] : new[] { s });

    private static WorkflowSections SectionFlag(Section section) => section switch
    {
        Section.Setup => WorkflowSections.Setup,
        Section.Reconcile => WorkflowSections.Reconcile,
        _ => WorkflowSections.Steps,
    };

    private static string SectionName(Section section) => section switch
    {
        Section.Setup => "setup",
        Section.Reconcile => "reconcile",
        _ => "steps",
    };
}
