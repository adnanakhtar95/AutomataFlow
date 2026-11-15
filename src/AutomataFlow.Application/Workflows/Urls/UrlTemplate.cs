using System.Collections.Immutable;
using System.Text;
using AutomataFlow.Application.Network;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;

namespace AutomataFlow.Application.Workflows.Urls;

/// <summary>A literal-keyed query pair of a URL template.</summary>
public sealed class UrlQueryPair
{
    internal UrlQueryPair(string key, Template value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public Template Value { get; }
}

/// <summary>
/// A <c>navigate.url</c> template parsed by the §6.1 grammar (ADR-046):
/// <code>origin-part path-part [ "?" query-part ]</code>
/// The origin is a canonical literal origin or a whole <c>{{params.&lt;url parameter&gt;}}</c>; placeholders are allowed
/// only in path segments and query values; query keys are literal; fragments are forbidden; literals are ASCII from the
/// grammar's character sets. So a record value can never control the scheme, host or port.
/// </summary>
public sealed class UrlTemplate
{
    private UrlTemplate(string source, OriginParts? literalOrigin, string? originParameter, ImmutableArray<Template> pathSegments, bool trailingSlash, ImmutableArray<UrlQueryPair> query)
    {
        Source = source;
        LiteralOrigin = literalOrigin;
        OriginParameter = originParameter;
        PathSegments = pathSegments;
        TrailingSlash = trailingSlash;
        Query = query;
    }

    public string Source { get; }

    /// <summary>The canonical literal origin, or null when the origin is a url parameter.</summary>
    public OriginParts? LiteralOrigin { get; }

    /// <summary>The url parameter name when the origin is <c>{{params.x}}</c>.</summary>
    public string? OriginParameter { get; }

    public ImmutableArray<Template> PathSegments { get; }

    public bool TrailingSlash { get; }

    public ImmutableArray<UrlQueryPair> Query { get; }

    /// <summary>Every placeholder in path segments and query values (never the origin).</summary>
    public IEnumerable<(PlaceholderSegment Placeholder, TemplateSink Sink)> ValuePlaceholders =>
        PathSegments.SelectMany(s => s.Placeholders.Select(p => (p, TemplateSink.UrlPathSegment)))
            .Concat(Query.SelectMany(q => q.Value.Placeholders.Select(p => (p, TemplateSink.UrlQueryValue))));

    public static UrlTemplate? Parse(string text, out string? errorCode, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(text);
        (var template, errorCode, errorMessage) = ParseCore(text);
        return template;
    }

    private static (UrlTemplate? Template, string? Code, string? Message) ParseCore(string text)
    {
        static (UrlTemplate?, string?, string?) Fail(string code, string message) => (null, code, message);

        if (text.Length > WorkflowLimits.MaxUrlTemplateChars)
        {
            return Fail(UrlErrorCodes.TooLong, $"A URL template is limited to {WorkflowLimits.MaxUrlTemplateChars} characters.");
        }

        if (text.Contains('#', StringComparison.Ordinal))
        {
            return Fail(UrlErrorCodes.Fragment, "URL templates cannot contain a fragment.");
        }

        // ----- Origin -----
        OriginParts? literalOrigin = null;
        string? originParameter = null;
        int rest;
        if (text.StartsWith("{{", StringComparison.Ordinal))
        {
            var close = text.IndexOf("}}", StringComparison.Ordinal);
            var origin = close < 0 ? null : Template.Parse(text[..(close + 2)], out _, out _);
            if (origin is null || !origin.IsSinglePlaceholder(out var placeholder) || placeholder.Namespace != TemplateNamespace.Params)
            {
                return Fail(UrlErrorCodes.OriginPlaceholder, "The origin must be a canonical literal origin or exactly {{params.<url parameter>}}.");
            }

            originParameter = placeholder.Name;
            rest = close + 2;
            if (rest < text.Length && text[rest] is not ('/' or '?'))
            {
                return Fail(UrlErrorCodes.OriginPlaceholder, "A url parameter must be the whole origin, followed by '/', '?' or nothing.");
            }
        }
        else
        {
            var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0)
            {
                return Fail(UrlErrorCodes.InvalidOrigin, "A URL template must start with a canonical origin (https://host[:port]) or {{params.<url parameter>}}.");
            }

            var authorityEnd = text.IndexOfAny(['/', '?'], schemeEnd + 3);
            rest = authorityEnd < 0 ? text.Length : authorityEnd;
            var originText = text[..rest];
            if (originText.Contains("{{", StringComparison.Ordinal))
            {
                return Fail(UrlErrorCodes.OriginPlaceholder, "Placeholders are not allowed in the scheme, host or port.");
            }

            literalOrigin = CanonicalOrigin.TryParseCanonical(originText, allowWildcard: false, out var originError);
            if (literalOrigin is null)
            {
                return Fail(UrlErrorCodes.InvalidOrigin, $"The origin is not a canonical origin ({originError}).");
            }
        }

        // ----- Path and query -----
        var queryStart = text.IndexOf('?', rest);
        var pathPart = queryStart < 0 ? text[rest..] : text[rest..queryStart];
        var segments = ImmutableArray.CreateBuilder<Template>();
        var trailingSlash = false;
        if (pathPart.Length > 0)
        {
            var parts = pathPart[1..].Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var segmentText = parts[i];
                if (segmentText.Length == 0)
                {
                    if (i == parts.Length - 1)
                    {
                        trailingSlash = true;
                        continue;
                    }

                    return Fail(UrlErrorCodes.EmptySegment, "URL paths cannot contain empty segments ('//').");
                }

                var segment = Template.Parse(segmentText, out var code, out var message);
                if (segment is null)
                {
                    return Fail(code!, message!);
                }

                if (LiteralProblem(segment, UrlCharset.PathChar) is { } literalProblem)
                {
                    return Fail(literalProblem.Code, literalProblem.Message);
                }

                if (segment.IsLiteral && IsDotSegment(DecodeDots(segmentText)))
                {
                    return Fail(UrlErrorCodes.DotSegment, "Literal '.' and '..' segments (including %2e forms) are not allowed.");
                }

                segments.Add(segment);
            }
        }

        var query = ImmutableArray.CreateBuilder<UrlQueryPair>();
        if (queryStart >= 0)
        {
            var queryText = text[(queryStart + 1)..];
            foreach (var pair in queryText.Split('&'))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    return Fail(UrlErrorCodes.InvalidQuery, "Each query pair must be key=value with a literal key.");
                }

                var key = pair[..equals];
                if (!key.All(UrlCharset.IsKeyChar))
                {
                    return Fail(UrlErrorCodes.InvalidQuery, "Query keys must be literal letters, digits, '-', '.', '_' or '~'.");
                }

                var value = Template.Parse(pair[(equals + 1)..], out var code, out var message);
                if (value is null)
                {
                    return Fail(code!, message!);
                }

                if (LiteralProblem(value, UrlCharset.QueryChar) is { } literalProblem)
                {
                    return Fail(literalProblem.Code, literalProblem.Message);
                }

                query.Add(new UrlQueryPair(key, value));
            }
        }

        var template = new UrlTemplate(text, literalOrigin, originParameter, segments.ToImmutable(), trailingSlash, query.ToImmutable());

        // Authoring-time round trip: with sample values the result must survive .NET URI parsing unchanged, so literal
        // forms that would be normalized (and then rejected at run time) are rejected now.
        var sampleOrigin = literalOrigin?.ToString() ?? "https://example.test";
        var sample = template.Build(sampleOrigin, _ => "x");
        if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || !string.Equals(uri.AbsoluteUri, sample, StringComparison.Ordinal))
        {
            return Fail(UrlErrorCodes.NotCanonical, "The URL template is not in canonical form (it would be rewritten by URL parsing).");
        }

        return (template, null, null);
    }

    /// <summary>
    /// Renders the URL with context-aware encoding (§6.1): each value is encoded exactly once with RFC 3986 encoding
    /// (<see cref="Uri.EscapeDataString(string)"/>). A placeholder-bearing path segment that resolves to empty, <c>.</c>
    /// or <c>..</c> (also via <c>%2e</c>), and any value with control characters or lone surrogates, gives
    /// <c>UnsafeUrlValue</c>. Values are resolved through <see cref="TemplateResolver"/>, so the sink rules are re-checked.
    /// </summary>
    public UrlRenderResult Render(OriginParts boundOrigin, ITemplateValues values, IReadOnlyDictionary<string, WorkflowInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(boundOrigin);
        ArgumentNullException.ThrowIfNull(values);
        if (LiteralOrigin is not null && !string.Equals(LiteralOrigin.ToString(), boundOrigin.ToString(), StringComparison.Ordinal))
        {
            return UrlRenderResult.Fail(EngineErrorCodes.UnsafeUrlValue, "The bound origin differs from the template's literal origin.");
        }

        var raw = new Dictionary<PlaceholderSegment, string>(ReferenceEqualityComparer.Instance);
        foreach (var (placeholder, sink) in ValuePlaceholders)
        {
            var resolved = TemplateResolver.Resolve(Template.ForPlaceholder(placeholder), TemplateSink.UrlQueryValue, values, inputs);
            if (!resolved.Succeeded)
            {
                return UrlRenderResult.Fail(resolved.ErrorCode!, resolved.ErrorMessage!);
            }

            if (resolved.Value!.IsSensitive)
            {
                return UrlRenderResult.Fail(EngineErrorCodes.SinkViolation, "Sensitive values cannot be used in a URL.");
            }

            var value = resolved.Value.Text;
            if (!IsSafeValue(value))
            {
                return UrlRenderResult.Fail(EngineErrorCodes.UnsafeUrlValue, "A URL value contains control characters or invalid UTF-16.");
            }

            raw[placeholder] = value;
        }

        foreach (var segment in PathSegments)
        {
            if (segment.IsLiteral)
            {
                continue;
            }

            var resolvedSegment = new StringBuilder();
            foreach (var part in segment.Segments)
            {
                resolvedSegment.Append(part is LiteralSegment literal ? DecodeDots(literal.Text) : DecodeDots(raw[(PlaceholderSegment)part]));
            }

            if (resolvedSegment.Length == 0 || IsDotSegment(resolvedSegment.ToString()))
            {
                return UrlRenderResult.Fail(EngineErrorCodes.UnsafeUrlValue, "A path segment resolved to an empty, '.' or '..' value.");
            }
        }

        return UrlRenderResult.Ok(Build(boundOrigin.ToString(), p => Uri.EscapeDataString(raw[p])));
    }

    private string Build(string origin, Func<PlaceholderSegment, string> encodedValue)
    {
        var builder = new StringBuilder(origin);
        foreach (var segment in PathSegments)
        {
            builder.Append('/');
            AppendTemplate(builder, segment, encodedValue);
        }

        // An empty path is the root path "/" (that is what URI parsing and browsers produce).
        if (TrailingSlash || PathSegments.IsEmpty)
        {
            builder.Append('/');
        }

        for (var i = 0; i < Query.Length; i++)
        {
            builder.Append(i == 0 ? '?' : '&').Append(Query[i].Key).Append('=');
            AppendTemplate(builder, Query[i].Value, encodedValue);
        }

        return builder.ToString();
    }

    private static void AppendTemplate(StringBuilder builder, Template template, Func<PlaceholderSegment, string> encodedValue)
    {
        foreach (var part in template.Segments)
        {
            builder.Append(part is LiteralSegment literal ? literal.Text : encodedValue((PlaceholderSegment)part));
        }
    }

    private static (string Code, string Message)? LiteralProblem(Template template, Func<char, bool> isAllowed)
    {
        foreach (var literal in template.Segments.OfType<LiteralSegment>())
        {
            var text = literal.Text;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
                    {
                        return (UrlErrorCodes.InvalidPercentEncoding, "'%' must be followed by two hex digits.");
                    }

                    var decoded = (char)Convert.ToInt32(text.Substring(i + 1, 2), 16);
                    if (decoded is '/' or '\\' or '.' || char.IsControl(decoded))
                    {
                        return (UrlErrorCodes.ForbiddenEncoding, "Percent-encoded '/', '\\', '.' and control characters are not allowed.");
                    }

                    i += 2;
                    continue;
                }

                if (!isAllowed(c))
                {
                    return (UrlErrorCodes.InvalidCharacter, "URL literals are limited to the ASCII characters of the URL grammar.");
                }
            }
        }

        return null;
    }

    /// <summary>Replaces <c>%2e</c>/<c>%2E</c> with <c>.</c> so dot segments are recognized in every spelling.</summary>
    private static string DecodeDots(string value) =>
        value.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsDotSegment(string value) => value is "." or "..";

    private static bool IsSafeValue(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return Definition.Json.StrictJsonParser.IsValidUtf16(value);
    }
}

/// <summary>A rendered URL string, or an engine error code.</summary>
public sealed class UrlRenderResult
{
    private UrlRenderResult(string? url, string? errorCode, string? errorMessage)
    {
        Url = url;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public string? Url { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public bool Succeeded => Url is not null;

    internal static UrlRenderResult Ok(string url) => new(url, null, null);

    internal static UrlRenderResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// The final URL check (§6.1 steps 1–6), run on the exact string Chromium will receive. Any failure is
/// <c>UnsafeUrlValue</c>; the returned <see cref="Uri"/> is what is passed to navigation. Phase 3 enforcement (request
/// guard and egress proxy) still applies afterwards.
/// </summary>
public static class FinalUrlValidator
{
    public static Uri? Validate(string url, UrlTemplate template, OriginParts boundOrigin, EgressPolicy policy, out string? error)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(boundOrigin);
        ArgumentNullException.ThrowIfNull(policy);
        error = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.AbsoluteUri, url, StringComparison.Ordinal))
        {
            error = "round-trip";
            return null;
        }

        if (url.Length > WorkflowLimits.MaxFinalUrlChars)
        {
            error = "too-long";
            return null;
        }

        var origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (!string.Equals(origin, boundOrigin.ToString(), StringComparison.Ordinal) || uri.Port != boundOrigin.Port
            || !string.Equals(uri.Scheme, boundOrigin.Scheme, StringComparison.Ordinal))
        {
            error = "origin-mismatch";
            return null;
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "userinfo-or-fragment";
            return null;
        }

        // Segment-aware literal prefix: leading literal segments must match one by one; the first placeholder-bearing
        // segment must start with its literal prefix.
        var actual = uri.AbsolutePath.Length <= 1 ? [] : uri.AbsolutePath[1..].Split('/');
        for (var i = 0; i < template.PathSegments.Length; i++)
        {
            var segment = template.PathSegments[i];
            if (i >= actual.Length)
            {
                error = "path-prefix";
                return null;
            }

            if (segment.IsLiteral)
            {
                if (!string.Equals(((LiteralSegment)segment.Segments[0]).Text, actual[i], StringComparison.Ordinal))
                {
                    error = "path-prefix";
                    return null;
                }

                continue;
            }

            if (segment.Segments[0] is LiteralSegment prefix && !actual[i].StartsWith(prefix.Text, StringComparison.Ordinal))
            {
                error = "path-prefix";
            }

            break;
        }

        if (error is not null)
        {
            return null;
        }

        var host = boundOrigin.Host;
        if (policy.FindRule(host, uri.Port, uri.Scheme) is null)
        {
            error = "not-allowed-host";
            return null;
        }

        return uri;
    }
}

/// <summary>Character sets of the §6.1 grammar (ASCII only).</summary>
internal static class UrlCharset
{
    public static bool PathChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '!' or '$' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or ':' or '@';

    public static bool QueryChar(char c) => (PathChar(c) || c is '/' or '?') && c is not ('&' or '=' or '+');

    public static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}

/// <summary>Authoring-time URL template error codes.</summary>
public static class UrlErrorCodes
{
    public const string TooLong = "url-too-long";
    public const string Fragment = "url-fragment";
    public const string OriginPlaceholder = "url-origin-placeholder";
    public const string InvalidOrigin = "url-invalid-origin";
    public const string EmptySegment = "url-empty-segment";
    public const string DotSegment = "url-dot-segment";
    public const string InvalidQuery = "url-invalid-query";
    public const string InvalidCharacter = "url-invalid-character";
    public const string InvalidPercentEncoding = "url-invalid-percent-encoding";
    public const string ForbiddenEncoding = "url-forbidden-encoding";
    public const string NotCanonical = "url-not-canonical";
    public const string OriginNotAllowed = "url-origin-not-allowed";
}
