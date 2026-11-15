using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.Application.Workflows.Urls;

/// <summary>
/// The <c>urlMatches</c> / <c>wait.url</c> glob (docs/PHASE4_PLAN.md §8.2, ADR-046, clarified by ADR-062), implemented
/// without regular expressions. It reproduces Playwright's glob for the supported subset, so a condition and a wait
/// with the same pattern cannot disagree:
/// <list type="bullet">
/// <item><c>*</c> matches zero or more characters except <c>/</c>.</item>
/// <item><c>**</c> must be a whole path element (preceded by the start or <c>/</c>, followed by <c>/</c> or the end).
/// <c>**/</c> matches zero or more whole segments (empty, or anything ending in <c>/</c>); a final <c>**</c> matches
/// the rest of the URL. Any other <c>**</c> is rejected, because Playwright would silently treat it as <c>*</c>.</item>
/// <item>Every other character is literal; <c>? { } [ ] \</c>, whitespace, control characters and runs of three or more
/// <c>*</c> are rejected. Comparison is ordinal; the subject is the full URL including query and fragment.</item>
/// <item>A pattern is absolute (<c>http://</c> / <c>https://</c>, lowercase scheme and host, no default port) or starts
/// with <c>**/</c>.</item>
/// </list>
/// Matching is a dynamic program over (token, position): O(tokens × URL length), bounded by the length limits.
/// </summary>
public sealed class UrlGlob
{
    private enum Kind
    {
        Literal,
        Star,
        DeepSegments,
        DeepRest,
    }

    private readonly ImmutableArray<(Kind Kind, string Text)> _tokens;

    private UrlGlob(string pattern, ImmutableArray<(Kind, string)> tokens)
    {
        Pattern = pattern;
        _tokens = tokens;
    }

    public string Pattern { get; }

    public static UrlGlob? Parse(string pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        error = null;
        if (pattern.Length == 0 || pattern.Length > WorkflowLimits.MaxUrlPatternChars)
        {
            error = "glob-length";
            return null;
        }

        foreach (var c in pattern)
        {
            if (c is '?' or '{' or '}' or '[' or ']' or '\\' || char.IsControl(c) || char.IsWhiteSpace(c) || c > '~')
            {
                error = "glob-invalid-character";
                return null;
            }
        }

        if (pattern.StartsWith("http://", StringComparison.Ordinal) || pattern.StartsWith("https://", StringComparison.Ordinal))
        {
            var authorityStart = pattern.IndexOf("://", StringComparison.Ordinal) + 3;
            var authorityEnd = pattern.IndexOf('/', authorityStart);
            var authority = authorityEnd < 0 ? pattern[authorityStart..] : pattern[authorityStart..authorityEnd];
            if (authority.Any(char.IsAsciiLetterUpper))
            {
                error = "glob-uppercase-host";
                return null;
            }

            var defaultPort = pattern.StartsWith("https", StringComparison.Ordinal) ? ":443" : ":80";
            if (authority.EndsWith(defaultPort, StringComparison.Ordinal))
            {
                error = "glob-default-port";
                return null;
            }
        }
        else if (!pattern.StartsWith("**/", StringComparison.Ordinal))
        {
            error = "glob-not-anchored";
            return null;
        }

        var tokens = ImmutableArray.CreateBuilder<(Kind, string)>();
        var literal = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '*')
            {
                literal.Append(pattern[i]);
                continue;
            }

            var stars = 1;
            while (i + stars < pattern.Length && pattern[i + stars] == '*')
            {
                stars++;
            }

            if (literal.Length > 0)
            {
                tokens.Add((Kind.Literal, literal.ToString()));
                literal.Clear();
            }

            if (stars == 1)
            {
                tokens.Add((Kind.Star, string.Empty));
                continue;
            }

            if (stars > 2)
            {
                error = "glob-too-many-stars";
                return null;
            }

            var before = i == 0 ? '/' : pattern[i - 1];
            var afterIndex = i + 2;
            if (before != '/' || (afterIndex < pattern.Length && pattern[afterIndex] != '/'))
            {
                error = "glob-deep-not-segment";
                return null;
            }

            if (afterIndex == pattern.Length)
            {
                tokens.Add((Kind.DeepRest, string.Empty));
                i += 1;
            }
            else
            {
                tokens.Add((Kind.DeepSegments, string.Empty)); // consumes the following '/'
                i += 2;
            }
        }

        if (literal.Length > 0)
        {
            tokens.Add((Kind.Literal, literal.ToString()));
        }

        return new UrlGlob(pattern, tokens.ToImmutable());
    }

    public bool IsMatch(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var n = url.Length;
        var current = new bool[n + 1];
        current[0] = true;
        foreach (var (kind, text) in _tokens)
        {
            var next = new bool[n + 1];
            switch (kind)
            {
                case Kind.Literal:
                    for (var j = 0; j + text.Length <= n; j++)
                    {
                        if (current[j] && string.CompareOrdinal(url, j, text, 0, text.Length) == 0)
                        {
                            next[j + text.Length] = true;
                        }
                    }

                    break;
                case Kind.Star:
                    for (var k = 0; k <= n; k++)
                    {
                        next[k] = current[k] || (k > 0 && next[k - 1] && url[k - 1] != '/');
                    }

                    break;
                case Kind.DeepSegments:
                    var anyBefore = false;
                    for (var k = 0; k <= n; k++)
                    {
                        next[k] = current[k] || (anyBefore && (url[k - 1] == '/' || k == n));
                        anyBefore |= current[k];
                    }

                    break;
                case Kind.DeepRest:
                    var any = false;
                    for (var k = 0; k <= n; k++)
                    {
                        any |= current[k];
                        next[k] = any;
                    }

                    break;
            }

            current = next;
        }

        return current[n];
    }
}
