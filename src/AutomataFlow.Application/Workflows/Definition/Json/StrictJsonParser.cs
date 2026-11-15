using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AutomataFlow.Application.Validation;

namespace AutomataFlow.Application.Workflows.Definition.Json;

/// <summary>
/// Fail-closed JSON parser for workflow definitions (docs/PHASE4_PLAN.md §1, §16 D5). Rejects, with a precise error:
/// oversized input (checked before parsing), a byte-order mark, invalid UTF-8, comments, trailing commas, trailing
/// content, nesting deeper than <see cref="WorkflowLimits.MaxJsonDepth"/>, duplicate property names (after unescaping),
/// non-integer numbers (fractions, exponents, <c>-0</c>), integers outside 64-bit range, strings that are not valid
/// UTF-16, and over-long strings or property names.
/// </summary>
public static class StrictJsonParser
{
    public static JsonParseResult Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > WorkflowLimits.MaxDefinitionBytes)
        {
            return Fail("$", "json-too-large", $"The definition is {utf8.Length} bytes; the maximum is {WorkflowLimits.MaxDefinitionBytes}.");
        }

        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            return Fail("$", "json-bom", "The definition must be UTF-8 without a byte-order mark.");
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = WorkflowLimits.MaxJsonDepth + 1, // our own depth check below fires first, with a precise path
        });

        try
        {
            if (!reader.Read())
            {
                return Fail("$", "json-malformed", "The definition is empty.");
            }

            var root = ReadValue(ref reader, "$", depth: 0, out var error);
            if (error is not null)
            {
                return new JsonParseResult(null, error);
            }

            if (reader.Read())
            {
                return Fail("$", "json-malformed", "Unexpected content after the root value.");
            }

            return new JsonParseResult(root, null);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1}, byte {ex.BytePositionInLine + 1})" : string.Empty;
            return Fail("$", "json-malformed", "The definition is not valid JSON" + where + ".");
        }
    }

    private static JNode? ReadValue(ref Utf8JsonReader reader, string path, int depth, out ValidationError? error)
    {
        error = null;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, path, depth + 1, out error);
            case JsonTokenType.StartArray:
                return ReadArray(ref reader, path, depth + 1, out error);
            case JsonTokenType.String:
                return ReadString(ref reader, path, WorkflowLimits.MaxJsonStringChars, out var text, out error) ? new JString(text!) : null;
            case JsonTokenType.Number:
                return ReadInteger(ref reader, path, out error);
            case JsonTokenType.True:
                return new JBoolean(true);
            case JsonTokenType.False:
                return new JBoolean(false);
            case JsonTokenType.Null:
                return JNull.Instance;
            default:
                error = new ValidationError(path, "json-malformed", "Unexpected JSON token.");
                return null;
        }
    }

    private static JObject? ReadObject(ref Utf8JsonReader reader, string path, int depth, out ValidationError? error)
    {
        if (depth > WorkflowLimits.MaxJsonDepth)
        {
            error = new ValidationError(path, "json-too-deep", $"Nesting is deeper than {WorkflowLimits.MaxJsonDepth} levels.");
            return null;
        }

        var properties = new SortedDictionary<string, JNode>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                error = null;
                return new JObject([.. properties]);
            }

            if (!ReadString(ref reader, path, WorkflowLimits.MaxJsonPropertyNameChars, out var name, out error))
            {
                error = error! with { Code = error.Code == "json-string-too-long" ? "json-property-name-too-long" : error.Code };
                return null;
            }

            var propertyPath = path + "." + name;
            if (properties.ContainsKey(name!))
            {
                error = new ValidationError(propertyPath, "json-duplicate-property", $"Property '{name}' appears more than once.");
                return null;
            }

            if (!reader.Read())
            {
                break;
            }

            var value = ReadValue(ref reader, propertyPath, depth, out error);
            if (error is not null)
            {
                return null;
            }

            properties[name!] = value!;
        }

        error = new ValidationError(path, "json-malformed", "Unterminated object.");
        return null;
    }

    private static JArray? ReadArray(ref Utf8JsonReader reader, string path, int depth, out ValidationError? error)
    {
        if (depth > WorkflowLimits.MaxJsonDepth)
        {
            error = new ValidationError(path, "json-too-deep", $"Nesting is deeper than {WorkflowLimits.MaxJsonDepth} levels.");
            return null;
        }

        var items = ImmutableArray.CreateBuilder<JNode>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                error = null;
                return new JArray(items.ToImmutable());
            }

            var value = ReadValue(ref reader, path + "[" + items.Count.ToString(CultureInfo.InvariantCulture) + "]", depth, out error);
            if (error is not null)
            {
                return null;
            }

            items.Add(value!);
        }

        error = new ValidationError(path, "json-malformed", "Unterminated array.");
        return null;
    }

    private static bool ReadString(ref Utf8JsonReader reader, string path, int maxChars, out string? value, out ValidationError? error)
    {
        value = null;
        try
        {
            value = reader.GetString();
        }
        catch (InvalidOperationException)
        {
            error = new ValidationError(path, "json-invalid-string", "A string is not valid UTF-16 (lone surrogate).");
            return false;
        }

        if (value is null || !IsValidUtf16(value))
        {
            error = new ValidationError(path, "json-invalid-string", "A string is not valid UTF-16 (lone surrogate).");
            return false;
        }

        if (value.Length > maxChars)
        {
            error = new ValidationError(path, "json-string-too-long", $"A string is longer than {maxChars} characters.");
            return false;
        }

        error = null;
        return true;
    }

    private static JInteger? ReadInteger(ref Utf8JsonReader reader, string path, out ValidationError? error)
    {
        // The input is always a single contiguous span, so the token is never a multi-segment sequence.
        var raw = reader.ValueSpan.ToArray();
        if (Array.IndexOf(raw, (byte)'.') >= 0 || Array.IndexOf(raw, (byte)'e') >= 0 || Array.IndexOf(raw, (byte)'E') >= 0)
        {
            error = new ValidationError(path, "json-non-integer-number", "Only integers in plain decimal notation are allowed.");
            return null;
        }

        if (raw is [(byte)'-', (byte)'0'])
        {
            error = new ValidationError(path, "json-non-integer-number", "-0 is not allowed.");
            return null;
        }

        if (!reader.TryGetInt64(out var number))
        {
            error = new ValidationError(path, "json-integer-out-of-range", "The integer is outside the 64-bit range.");
            return null;
        }

        error = null;
        return new JInteger(number);
    }

    private static bool IsValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonParseResult Fail(string path, string code, string message) => new(null, new ValidationError(path, code, message));
}

/// <summary>Either a parsed root (<see cref="Error"/> is null) or the first JSON-level error.</summary>
public sealed record JsonParseResult(JNode? Root, ValidationError? Error)
{
    public bool IsValid => Error is null;
}
