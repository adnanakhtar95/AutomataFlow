using System.Collections.Immutable;
using System.Globalization;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Records;

/// <summary>A record that passed <see cref="RecordValidator"/>; values of sensitive inputs are flagged.</summary>
public sealed class ValidatedRecord
{
    internal ValidatedRecord(ImmutableDictionary<string, string> values, ImmutableHashSet<string> sensitiveInputs)
    {
        Values = values;
        SensitiveInputs = sensitiveInputs;
    }

    /// <summary>Present, non-empty values only (a missing optional input is simply absent).</summary>
    public ImmutableDictionary<string, string> Values { get; }

    public ImmutableHashSet<string> SensitiveInputs { get; }

    public override string ToString() => $"[record with {Values.Count} value(s)]";
}

/// <summary>
/// Validates one record against a workflow's <c>inputs</c> before execution (docs/PHASE4_PLAN.md §4; Phase 5 reuses it
/// per record). Unknown fields, missing required values, values longer than <c>maxLength</c>, values that do not match
/// their type, control characters other than tab and lone surrogates are rejected. Error messages never contain values
/// (records are customer data).
/// </summary>
public static class RecordValidator
{
    public static (ValidatedRecord? Record, ImmutableArray<ValidationError> Errors) Validate(WorkflowDefinition definition, IReadOnlyDictionary<string, string?> record)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(record);
        var errors = ImmutableArray.CreateBuilder<ValidationError>();
        var inputs = definition.Inputs.ToDictionary(i => i.Name, StringComparer.Ordinal);

        foreach (var key in record.Keys)
        {
            if (!inputs.ContainsKey(key))
            {
                errors.Add(new ValidationError(DiagnosticPath.Property("record", key), "record-unknown-field", "The record has a field that is not a declared input."));
            }
        }

        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var input in definition.Inputs)
        {
            var path = "record." + input.Name;
            record.TryGetValue(input.Name, out var value);
            if (string.IsNullOrEmpty(value))
            {
                if (input.Required)
                {
                    errors.Add(new ValidationError(path, "record-required", "A required value is missing."));
                }

                continue;
            }

            if (!StrictJsonParser.IsValidUtf16(value) || value.Any(c => char.IsControl(c) && c != '\t'))
            {
                errors.Add(new ValidationError(path, "record-invalid-characters", "The value contains control characters or invalid UTF-16."));
                continue;
            }

            if (value.Length > input.MaxLength)
            {
                errors.Add(new ValidationError(path, "record-too-long", $"The value is longer than {input.MaxLength} characters."));
                continue;
            }

            if (!MatchesType(input.Type, value))
            {
                errors.Add(new ValidationError(path, "record-invalid-type", $"The value is not a valid {TypeName(input.Type)}."));
                continue;
            }

            values[input.Name] = value;
        }

        if (errors.Count > 0)
        {
            return (null, errors.ToImmutable());
        }

        var sensitive = definition.Inputs.Where(i => i.Sensitive).Select(i => i.Name).ToImmutableHashSet(StringComparer.Ordinal);
        return (new ValidatedRecord(values.ToImmutable(), sensitive), []);
    }

    private static bool MatchesType(WorkflowInputType type, string value) => type switch
    {
        WorkflowInputType.Text => true,
        WorkflowInputType.Email => IsEmail(value),
        WorkflowInputType.WholeNumber => IsInteger(value),
        WorkflowInputType.DecimalNumber => IsDecimal(value),
        WorkflowInputType.Boolean => value is "true" or "false",
        WorkflowInputType.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        // An opaque reference resolved by the organization-scoped file provider; never a path we interpret.
        WorkflowInputType.File => value.Trim().Length == value.Length,
        _ => false,
    };

    private static string TypeName(WorkflowInputType type) => WorkflowJsonNames.InputTypes.First(p => p.Value == type).Key;

    /// <summary>Structural check: one '@', non-empty local part, a dotted domain of letters, digits and hyphens, no whitespace.</summary>
    private static bool IsEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1 || value.Length > 254)
        {
            return false;
        }

        var local = value[..at];
        var domain = value[(at + 1)..];
        if (local.Length > 64 || local.Any(c => char.IsWhiteSpace(c) || c > '~' || c is '"' or ',' or '<' or '>' or '(' or ')' or '[' or ']' or ';' or ':' or '\\')
            || local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = domain.Split('.');
        return labels.Length >= 2 && labels.All(l => l.Length is > 0 and <= 63 && !l.StartsWith('-') && !l.EndsWith('-') && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    /// <summary>Optional '-', then 1..19 digits without leading zeros (fits a 64-bit integer).</summary>
    private static bool IsInteger(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
        && string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
        && value != "-0";

    /// <summary>Optional '-', digits, optional '.' and digits; invariant culture; no exponent, no thousands separators.</summary>
    private static bool IsDecimal(string value)
    {
        var body = value.StartsWith('-') ? value[1..] : value;
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? body : body[..dot];
        var fraction = dot < 0 ? "0" : body[(dot + 1)..];
        return whole.Length is > 0 and <= 28 && fraction.Length is > 0 and <= 28 && whole.All(char.IsAsciiDigit) && fraction.All(char.IsAsciiDigit);
    }
}
