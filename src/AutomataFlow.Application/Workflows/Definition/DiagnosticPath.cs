using System.Globalization;
using System.Text;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Builds the JSON paths and value displays used in validation errors (ADR-061). Property names come from untrusted
/// input, so a name that is not a plain identifier is written in bracket form with every character outside printable
/// ASCII, and every quote and backslash, escaped as <c>\uXXXX</c>. Paths therefore never contain control characters
/// (no log forging) and are unambiguous (<c>$['a.b']</c> is distinguishable from <c>$.a.b</c>).
/// </summary>
public static class DiagnosticPath
{
    public const string Root = "$";

    public static string Property(string parent, string name) => parent + Segment(name);

    public static string Index(string parent, int index) => parent + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>A path segment for a property name: <c>.name</c> for identifiers, otherwise <c>['escaped']</c>.</summary>
    public static string Segment(string name) => IsIdentifier(name) ? "." + name : "['" + Escape(name) + "']";

    /// <summary>Escapes untrusted text for display in a diagnostic message; at most <paramref name="maxChars"/> source characters are shown.</summary>
    public static string Escape(string value, int maxChars = 64)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxChars) + 8);
        var length = Math.Min(value.Length, maxChars);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            if (c is >= ' ' and <= '~' && c is not '\'' and not '\\' and not '"')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        if (value.Length > maxChars)
        {
            builder.Append("...");
        }

        return builder.ToString();
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
