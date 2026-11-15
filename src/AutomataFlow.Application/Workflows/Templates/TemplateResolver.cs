using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Templates;

/// <summary>The run-time values a template can read (the executor's implementation lives with the executor).</summary>
public interface ITemplateValues
{
    Guid OperationId { get; }

    /// <summary>False, or a null value, when the record has no value for an input.</summary>
    bool TryGetRecordValue(string name, out string? value);

    bool TryGetParameter(string name, out string value);

    bool TryGetSecret(string name, out string value);

    bool TryGetStepOutput(string stepId, string output, out string value, out bool isSensitive);
}

/// <summary>
/// A resolved template value with its taint (§4, §5). <see cref="ToString"/> never reveals the text, so a value that
/// reaches a log or exception message by mistake is not leaked.
/// </summary>
public sealed class ResolvedValue
{
    internal ResolvedValue(string text, bool isSensitive, bool containsSecret)
    {
        Text = text;
        IsSensitive = isSensitive;
        ContainsSecret = containsSecret;
    }

    public string Text { get; }

    public bool IsSensitive { get; }

    public bool ContainsSecret { get; }

    public override string ToString() => IsSensitive ? "[sensitive value]" : "[value]";
}

/// <summary>Either a resolved value or a stable engine error code (no values in messages).</summary>
public sealed class TemplateResolution
{
    private TemplateResolution(ResolvedValue? value, string? errorCode, string? errorMessage)
    {
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public ResolvedValue? Value { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public bool Succeeded => Value is not null;

    internal static TemplateResolution Ok(ResolvedValue value) => new(value, null, null);

    internal static TemplateResolution Fail(string code, string message) => new(null, code, message);
}

/// <summary>Engine error codes raised while resolving or rendering values (§9).</summary>
public static class EngineErrorCodes
{
    public const string VariableUnresolved = "VariableUnresolved";
    public const string SinkViolation = "SinkViolation";
    public const string UnsafeUrlValue = "UnsafeUrlValue";
    public const string RecordInvalid = "RecordInvalid";
    public const string WorkflowTampered = "WorkflowTampered";
    public const string WorkflowTimeoutExceedsServerLimit = "WorkflowTimeoutExceedsServerLimit";
    public const string PolicyViolation = "PolicyViolation";
}

/// <summary>
/// Run-time resolution (§4). The sink is part of the call, so the run-time sink check cannot be skipped: it repeats the
/// authoring-time rules as a backstop. A missing optional input resolves to the empty string unless the sink requires a
/// value; <c>{{operation.id}}</c> renders as a lowercase hyphenated GUID (<c>"D"</c>).
/// </summary>
public static class TemplateResolver
{
    public static TemplateResolution Resolve(Template template, TemplateSink sink, ITemplateValues values, IReadOnlyDictionary<string, WorkflowInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(inputs);
        var text = new StringBuilder();
        var sensitive = false;
        var secret = false;
        foreach (var segment in template.Segments)
        {
            if (segment is LiteralSegment literal)
            {
                text.Append(literal.Text);
                continue;
            }

            var placeholder = (PlaceholderSegment)segment;
            switch (placeholder.Namespace)
            {
                case TemplateNamespace.Record:
                    if (!inputs.TryGetValue(placeholder.Name, out var input))
                    {
                        return Unresolved(placeholder);
                    }

                    if (!values.TryGetRecordValue(placeholder.Name, out var recordValue) || string.IsNullOrEmpty(recordValue))
                    {
                        if (input.Required || SinkRules.RequiresValue(sink))
                        {
                            return Unresolved(placeholder);
                        }

                        recordValue = string.Empty;
                    }

                    sensitive |= input.Sensitive;
                    text.Append(recordValue);
                    break;
                case TemplateNamespace.Params:
                    if (!values.TryGetParameter(placeholder.Name, out var parameter))
                    {
                        return Unresolved(placeholder);
                    }

                    text.Append(parameter);
                    break;
                case TemplateNamespace.Secret:
                    if (!values.TryGetSecret(placeholder.Name, out var secretValue))
                    {
                        return Unresolved(placeholder);
                    }

                    secret = true;
                    sensitive = true;
                    text.Append(secretValue);
                    break;
                case TemplateNamespace.Steps:
                    if (!values.TryGetStepOutput(placeholder.Name, placeholder.Output!, out var output, out var outputSensitive))
                    {
                        return Unresolved(placeholder);
                    }

                    sensitive |= outputSensitive;
                    text.Append(output);
                    break;
                case TemplateNamespace.Operation:
                    text.Append(values.OperationId.ToString("D", CultureInfo.InvariantCulture));
                    break;
            }
        }

        if ((secret && !SinkRules.AllowsSecret(sink)) || (sensitive && !SinkRules.AllowsSensitive(sink)))
        {
            return TemplateResolution.Fail(EngineErrorCodes.SinkViolation, $"A {(secret ? "secret" : "sensitive")} value cannot be used in {sink}.");
        }

        if (text.Length == 0 && SinkRules.RequiresValue(sink))
        {
            return TemplateResolution.Fail(EngineErrorCodes.VariableUnresolved, $"The value for {sink} is empty.");
        }

        return TemplateResolution.Ok(new ResolvedValue(text.ToString(), sensitive, secret));
    }

    /// <summary>Builds the input lookup used by <see cref="Resolve"/>.</summary>
    public static ImmutableDictionary<string, WorkflowInput> InputLookup(WorkflowDefinition definition) =>
        definition.Inputs.ToImmutableDictionary(i => i.Name, StringComparer.Ordinal);

    private static TemplateResolution Unresolved(PlaceholderSegment placeholder) =>
        TemplateResolution.Fail(EngineErrorCodes.VariableUnresolved, $"{placeholder} could not be resolved.");
}
