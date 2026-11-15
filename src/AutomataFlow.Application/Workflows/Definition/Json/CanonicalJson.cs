using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AutomataFlow.Application.Workflows.Definition.Json;

/// <summary>
/// The canonical form of a parsed definition (docs/PHASE4_PLAN.md §16 D5, ADR-041): UTF-8 without BOM; object
/// properties in ordinal UTF-16 order; no insignificant whitespace; minimal string escaping (<c>"</c> and <c>\</c>, the
/// short escapes <c>\b \f \n \r \t</c>, other control characters as <c>\u00xx</c> with lowercase hex; everything else
/// literal); integers in plain decimal; <c>true</c>, <c>false</c>, <c>null</c>. It is written from the same
/// <see cref="JNode"/> tree that the binder validates, and the hash is always computed from these exact bytes.
/// </summary>
public static class CanonicalJson
{
    public const string HashPrefix = "sha256:";

    private static readonly SearchValues<char> LowerHexDigits = SearchValues.Create("0123456789abcdef");

    public static byte[] Serialize(JNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var builder = new StringBuilder();
        Write(node, builder);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(builder.ToString());
    }

    /// <summary><c>sha256:</c> + 64 lowercase hex digits of the SHA-256 of <paramref name="canonicalUtf8"/>.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> canonicalUtf8) => HashPrefix + Convert.ToHexStringLower(SHA256.HashData(canonicalUtf8));

    /// <summary>True for <c>sha256:</c> followed by exactly 64 lowercase hex digits.</summary>
    public static bool IsWellFormedHash(string? hash) =>
        hash is { Length: 71 } && hash.StartsWith(HashPrefix, StringComparison.Ordinal)
        && hash.AsSpan(HashPrefix.Length).IndexOfAnyExcept(LowerHexDigits) < 0;

    private static void Write(JNode node, StringBuilder output)
    {
        switch (node)
        {
            case JObject obj:
                output.Append('{');
                for (var i = 0; i < obj.Properties.Length; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    WriteString(obj.Properties[i].Key, output);
                    output.Append(':');
                    Write(obj.Properties[i].Value, output);
                }

                output.Append('}');
                break;
            case JArray array:
                output.Append('[');
                for (var i = 0; i < array.Items.Length; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    Write(array.Items[i], output);
                }

                output.Append(']');
                break;
            case JString text:
                WriteString(text.Value, output);
                break;
            case JInteger number:
                output.Append(number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case JBoolean boolean:
                output.Append(boolean.Value ? "true" : "false");
                break;
            case JNull:
                output.Append("null");
                break;
            default:
                throw new InvalidOperationException("Unknown JSON node type.");
        }
    }

    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}
