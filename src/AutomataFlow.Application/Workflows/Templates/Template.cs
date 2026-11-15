using System.Collections.Immutable;

namespace AutomataFlow.Application.Workflows.Templates;

/// <summary>The five template namespaces (docs/PHASE4_PLAN.md §4).</summary>
public enum TemplateNamespace
{
    Record,
    Steps,
    Operation,
    Params,
    Secret,
}

/// <summary>A literal part or a placeholder of a compiled <see cref="Template"/>.</summary>
public abstract class TemplateSegment
{
    private protected TemplateSegment()
    {
    }
}

public sealed class LiteralSegment : TemplateSegment
{
    internal LiteralSegment(string text) => Text = text;

    public string Text { get; }
}

/// <summary>
/// <c>{{record.x}}</c>, <c>{{params.x}}</c>, <c>{{secret.x}}</c>, <c>{{operation.id}}</c> (<see cref="Name"/> = <c>id</c>) or
/// <c>{{steps.&lt;id&gt;.&lt;output&gt;}}</c> (<see cref="Name"/> = step id, <see cref="Output"/> = output name).
/// </summary>
public sealed class PlaceholderSegment : TemplateSegment
{
    internal PlaceholderSegment(TemplateNamespace ns, string name, string? output)
    {
        Namespace = ns;
        Name = name;
        Output = output;
    }

    public TemplateNamespace Namespace { get; }

    public string Name { get; }

    public string? Output { get; }

    public override string ToString() => Output is null ? $"{{{{{NamespaceName(Namespace)}.{Name}}}}}" : $"{{{{steps.{Name}.{Output}}}}}";

    internal static string NamespaceName(TemplateNamespace ns) => ns switch
    {
        TemplateNamespace.Record => "record",
        TemplateNamespace.Steps => "steps",
        TemplateNamespace.Operation => "operation",
        TemplateNamespace.Params => "params",
        _ => "secret",
    };
}

/// <summary>
/// A compiled template (§4): a sequence of literal and placeholder segments. Syntax is <c>{{namespace.path}}</c> only,
/// with no expressions, filters, functions, arithmetic or loops; a literal <c>{{</c> is an error in schemaVersion 1.
/// Compiled once at validation time, so every reference is checked statically.
/// </summary>
public sealed class Template
{
    private Template(string source, ImmutableArray<TemplateSegment> segments)
    {
        Source = source;
        Segments = segments;
    }

    public string Source { get; }

    public ImmutableArray<TemplateSegment> Segments { get; }

    public IEnumerable<PlaceholderSegment> Placeholders => Segments.OfType<PlaceholderSegment>();

    internal static Template ForPlaceholder(PlaceholderSegment placeholder) => new(placeholder.ToString(), [placeholder]);

    public bool IsLiteral => Segments.All(s => s is LiteralSegment);

    /// <summary>True when the template is exactly one placeholder and nothing else.</summary>
    public bool IsSinglePlaceholder(out PlaceholderSegment placeholder)
    {
        placeholder = null!;
        if (Segments.Length == 1 && Segments[0] is PlaceholderSegment only)
        {
            placeholder = only;
            return true;
        }

        return false;
    }

    /// <summary>Compiles <paramref name="text"/>; on failure returns null with a stable error code and a safe message.</summary>
    public static Template? Parse(string text, out string? errorCode, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(text);
        errorCode = null;
        errorMessage = null;
        var segments = ImmutableArray.CreateBuilder<TemplateSegment>();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                segments.Add(new LiteralSegment(text[position..]));
                break;
            }

            if (open > position)
            {
                segments.Add(new LiteralSegment(text[position..open]));
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                errorCode = TemplateErrorCodes.Unterminated;
                errorMessage = "A placeholder is not terminated with '}}' (a literal '{{' is not allowed).";
                return null;
            }

            var inner = text[(open + 2)..close];
            if (inner.Length == 0)
            {
                errorCode = TemplateErrorCodes.Empty;
                errorMessage = "A placeholder is empty.";
                return null;
            }

            var placeholder = ParsePlaceholder(inner, out errorCode, out errorMessage);
            if (placeholder is null)
            {
                return null;
            }

            segments.Add(placeholder);
            position = close + 2;
        }

        return new Template(text, segments.ToImmutable());
    }

    private static PlaceholderSegment? ParsePlaceholder(string inner, out string? errorCode, out string? errorMessage)
    {
        errorCode = TemplateErrorCodes.InvalidReference;
        errorMessage = "A placeholder must be {{namespace.name}} with no spaces, expressions or nested braces.";
        if (inner.Contains('{', StringComparison.Ordinal) || inner.Contains('}', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = inner.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        TemplateNamespace? ns = parts[0] switch
        {
            "record" => TemplateNamespace.Record,
            "steps" => TemplateNamespace.Steps,
            "operation" => TemplateNamespace.Operation,
            "params" => TemplateNamespace.Params,
            "secret" => TemplateNamespace.Secret,
            _ => null,
        };

        if (ns is null)
        {
            errorCode = TemplateErrorCodes.UnknownNamespace;
            errorMessage = "Unknown placeholder namespace; use record, steps, operation, params or secret.";
            return null;
        }

        if (ns == TemplateNamespace.Steps)
        {
            if (parts.Length != 3 || !IsStepId(parts[1]) || !IsOutputName(parts[2]))
            {
                errorMessage = "A step reference must be {{steps.<step-id>.<output>}}.";
                return null;
            }

            errorCode = null;
            errorMessage = null;
            return new PlaceholderSegment(TemplateNamespace.Steps, parts[1], parts[2]);
        }

        if (parts.Length != 2 || !IsDeclaredName(parts[1]))
        {
            return null;
        }

        if (ns == TemplateNamespace.Operation && parts[1] != "id")
        {
            errorCode = TemplateErrorCodes.UnknownReference;
            errorMessage = "The only operation key is {{operation.id}}.";
            return null;
        }

        errorCode = null;
        errorMessage = null;
        return new PlaceholderSegment(ns.Value, parts[1], null);
    }

    private static bool IsDeclaredName(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsStepId(string value) =>
        value.Length is > 0 and <= 40 && char.IsAsciiLetterLower(value[0]) && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static bool IsOutputName(string value) =>
        value.Length is > 0 and <= 40 && char.IsAsciiLetterLower(value[0]) && value.All(char.IsAsciiLetterOrDigit);
}

/// <summary>Stable error codes for templates, sinks and references.</summary>
public static class TemplateErrorCodes
{
    public const string Unterminated = "template-unterminated";
    public const string Empty = "template-empty";
    public const string InvalidReference = "template-invalid-reference";
    public const string UnknownNamespace = "template-unknown-namespace";
    public const string UnknownReference = "template-unknown-reference";
    public const string UndeclaredInput = "template-undeclared-input";
    public const string UndeclaredParameter = "template-undeclared-parameter";
    public const string UndeclaredSecret = "template-undeclared-secret";
    public const string UnknownStep = "template-unknown-step";
    public const string StepNotDominating = "template-step-not-dominating";
    public const string UndeclaredOutput = "template-undeclared-output";
    public const string ParameterMisuse = "template-parameter-misuse";
    public const string PlaceholderNotAllowed = "template-not-allowed";
    public const string SecretNotAllowed = "sink-secret-not-allowed";
    public const string SensitiveNotAllowed = "sink-sensitive-not-allowed";
}
