using System.Collections.Immutable;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Templates;

/// <summary>Where a resolved template value is used. The sink decides which values are allowed (§5.1, ADR-044).</summary>
public enum TemplateSink
{
    /// <summary><c>fill.value</c>: secrets, sensitive and plain values (the only secret sink).</summary>
    FillValue,

    /// <summary><c>select</c> value / label.</summary>
    SelectValue,

    /// <summary>Condition operands (<c>valueEquals</c>, …).</summary>
    ConditionOperand,

    /// <summary>A <c>navigate.url</c> path segment (encoded).</summary>
    UrlPathSegment,

    /// <summary>A <c>navigate.url</c> query value (encoded).</summary>
    UrlQueryValue,

    /// <summary><c>captureResult.fields</c> values.</summary>
    ResultField,

    /// <summary><c>reconcileMatch.equals</c>.</summary>
    ReconcileEquals,

    /// <summary>Selector values (<c>testId</c>, <c>text</c>, <c>label</c>, <c>placeholder</c>, <c>name</c>).</summary>
    SelectorValue,

    /// <summary><c>screenshot.label</c>.</summary>
    ScreenshotLabel,

    /// <summary><c>upload.file</c>: exactly one <c>{{record.x}}</c> of a <c>file</c> input.</summary>
    UploadFile,
}

/// <summary>The sink matrix of §5.1 as data.</summary>
public static class SinkRules
{
    public static bool AllowsSecret(TemplateSink sink) => sink == TemplateSink.FillValue;

    public static bool AllowsSensitive(TemplateSink sink) =>
        sink is TemplateSink.FillValue or TemplateSink.SelectValue or TemplateSink.ConditionOperand;

    /// <summary><c>string</c> parameters are allowed in every sink except the upload file reference.</summary>
    public static bool AllowsStringParameter(TemplateSink sink) => sink != TemplateSink.UploadFile;

    /// <summary>Sinks that never accept an empty resolved value (§4).</summary>
    public static bool RequiresValue(TemplateSink sink) => sink is TemplateSink.UrlPathSegment or TemplateSink.ReconcileEquals or TemplateSink.UploadFile;
}

/// <summary>Taint of a template (§5): sensitive if any segment is sensitive; <see cref="ContainsSecret"/> if any is a secret.</summary>
public readonly record struct TemplateTaint(bool IsSensitive, bool ContainsSecret)
{
    public static TemplateTaint Clean => default;
}

/// <summary>What a step makes visible to later steps: its declared outputs and which of them are sensitive.</summary>
public sealed class StepOutputs
{
    public StepOutputs(IEnumerable<string> outputs, IEnumerable<string>? sensitiveOutputs = null)
    {
        Outputs = [.. outputs];
        SensitiveOutputs = [.. sensitiveOutputs ?? []];
    }

    public ImmutableHashSet<string> Outputs { get; }

    public ImmutableHashSet<string> SensitiveOutputs { get; }
}

/// <summary>
/// The references a template may use at one point of the workflow: the declarations, every step id in the workflow
/// (to tell "unknown" from "not dominating"), and the steps that dominate this point with their outputs (§4).
/// </summary>
public sealed class TemplateScope
{
    public TemplateScope(WorkflowDefinition definition, IReadOnlySet<string> allStepIds, IReadOnlyDictionary<string, StepOutputs> dominatingSteps)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Inputs = definition.Inputs.ToImmutableDictionary(i => i.Name, StringComparer.Ordinal);
        Parameters = definition.Parameters.ToImmutableDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
        Secrets = [.. definition.Secrets];
        AllStepIds = allStepIds;
        DominatingSteps = dominatingSteps;
    }

    public ImmutableDictionary<string, WorkflowInput> Inputs { get; }

    public ImmutableDictionary<string, WorkflowParameterType> Parameters { get; }

    public ImmutableHashSet<string> Secrets { get; }

    public IReadOnlySet<string> AllStepIds { get; }

    public IReadOnlyDictionary<string, StepOutputs> DominatingSteps { get; }

    /// <summary>A scope with declarations only and no visible steps (e.g. <c>maskSelectors</c>, resolved before any step runs).</summary>
    public static TemplateScope DeclarationsOnly(WorkflowDefinition definition, IReadOnlySet<string> allStepIds) =>
        new(definition, allStepIds, new Dictionary<string, StepOutputs>(StringComparer.Ordinal));

    public TemplateScope With(IReadOnlyDictionary<string, StepOutputs> dominatingSteps) => new(this, dominatingSteps);

    private TemplateScope(TemplateScope other, IReadOnlyDictionary<string, StepOutputs> dominatingSteps)
    {
        Inputs = other.Inputs;
        Parameters = other.Parameters;
        Secrets = other.Secrets;
        AllStepIds = other.AllStepIds;
        DominatingSteps = dominatingSteps;
    }
}

/// <summary>Static checking of template references and sink rules (authoring time, §4, §5.1).</summary>
public static class TemplateChecker
{
    /// <summary>
    /// Parses and checks a template used in <paramref name="sink"/>. Returns the compiled template and its taint, or
    /// null with errors. <paramref name="path"/> is the JSON path of the value (used in errors; values are never echoed).
    /// </summary>
    public static (Template? Template, TemplateTaint Taint, ImmutableArray<ValidationError> Errors) Check(string text, TemplateSink sink, TemplateScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scope);
        var template = Template.Parse(text, out var code, out var message);
        if (template is null)
        {
            return (null, TemplateTaint.Clean, [new ValidationError(path, code!, message!)]);
        }

        var (taint, errors) = Check(template, sink, scope, path);
        return (errors.IsEmpty ? template : null, taint, errors);
    }

    public static (TemplateTaint Taint, ImmutableArray<ValidationError> Errors) Check(Template template, TemplateSink sink, TemplateScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scope);
        var errors = ImmutableArray.CreateBuilder<ValidationError>();
        var sensitive = false;
        var secret = false;

        if (sink == TemplateSink.UploadFile)
        {
            if (!template.IsSinglePlaceholder(out var only) || only.Namespace != TemplateNamespace.Record
                || !scope.Inputs.TryGetValue(only.Name, out var fileInput) || fileInput.Type != WorkflowInputType.File)
            {
                errors.Add(new ValidationError(path, TemplateErrorCodes.PlaceholderNotAllowed, "'file' must be exactly one {{record.<name>}} of a declared 'file' input."));
                return (TemplateTaint.Clean, errors.ToImmutable());
            }
        }

        foreach (var placeholder in template.Placeholders)
        {
            switch (placeholder.Namespace)
            {
                case TemplateNamespace.Record:
                    if (!scope.Inputs.TryGetValue(placeholder.Name, out var input))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.UndeclaredInput, $"Input '{placeholder.Name}' is not declared."));
                    }
                    else
                    {
                        sensitive |= input.Sensitive;
                    }

                    break;
                case TemplateNamespace.Params:
                    if (!scope.Parameters.TryGetValue(placeholder.Name, out var type))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.UndeclaredParameter, $"Parameter '{placeholder.Name}' is not declared."));
                    }
                    else if (type == WorkflowParameterType.Url)
                    {
                        // url parameters are only allowed as the whole origin of a URL template or an allowedHosts entry
                        // (§16 D7); those positions are parsed by UrlTemplate / AllowedHosts, never as ordinary templates.
                        errors.Add(new ValidationError(path, TemplateErrorCodes.ParameterMisuse, $"URL parameter '{placeholder.Name}' can only be the origin of a URL or an allowedHosts entry."));
                    }
                    else if (!SinkRules.AllowsStringParameter(sink))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.ParameterMisuse, $"Parameter '{placeholder.Name}' is not allowed here."));
                    }

                    break;
                case TemplateNamespace.Secret:
                    if (!scope.Secrets.Contains(placeholder.Name))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.UndeclaredSecret, $"Secret '{placeholder.Name}' is not declared."));
                    }

                    secret = true;
                    sensitive = true;
                    break;
                case TemplateNamespace.Steps:
                    if (!scope.AllStepIds.Contains(placeholder.Name))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.UnknownStep, $"Step '{placeholder.Name}' does not exist."));
                    }
                    else if (!scope.DominatingSteps.TryGetValue(placeholder.Name, out var outputs))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.StepNotDominating,
                            $"Step '{placeholder.Name}' does not always run before this point (later step, other branch, or a branch output used after its condition)."));
                    }
                    else if (!outputs.Outputs.Contains(placeholder.Output!))
                    {
                        errors.Add(new ValidationError(path, TemplateErrorCodes.UndeclaredOutput, $"Step '{placeholder.Name}' has no output '{placeholder.Output}'."));
                    }
                    else
                    {
                        sensitive |= outputs.SensitiveOutputs.Contains(placeholder.Output!);
                    }

                    break;
                case TemplateNamespace.Operation:
                    break;
            }
        }

        if (secret && !SinkRules.AllowsSecret(sink))
        {
            errors.Add(new ValidationError(path, TemplateErrorCodes.SecretNotAllowed, "Secrets can only be used in fill.value."));
        }
        else if (sensitive && !SinkRules.AllowsSensitive(sink))
        {
            errors.Add(new ValidationError(path, TemplateErrorCodes.SensitiveNotAllowed, "Sensitive values cannot be used here."));
        }

        return (new TemplateTaint(sensitive, secret), errors.ToImmutable());
    }
}
