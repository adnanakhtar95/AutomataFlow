using System.Text;

namespace AutomataFlow.Automation.Browser;

/// <summary>
/// Redaction applied to everything the engine logs or reports (ADR-029 decision 6, ADR-032):
/// URL userinfo removed, query values and fragments replaced, known sensitive values masked.
/// </summary>
public static class LogRedactor
{
    public const string Mask = "***";

    /// <summary><c>https://u:p@host/path?token=abc&amp;q=1#x</c> becomes <c>https://host/path?token=***&amp;q=***</c>.</summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "[unparseable-url]";
        }

        if (uri.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            // data:, blob:, javascript: and similar can carry arbitrary content; only the scheme is kept.
            return uri.Scheme + ":[redacted]";
        }

        var builder = new StringBuilder()
            .Append(uri.Scheme).Append("://").Append(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            .Append(uri.AbsolutePath);

        if (uri.Query.Length > 1)
        {
            var pairs = uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)[0] + "=" + Mask);
            builder.Append('?').AppendJoin('&', pairs);
        }

        return builder.ToString();
    }

    /// <summary>Replaces every occurrence of any known sensitive value.</summary>
    public static string RedactValues(string text, IEnumerable<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sensitiveValues);
        foreach (var value in sensitiveValues.Where(v => v.Length > 0).OrderByDescending(v => v.Length))
        {
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        }

        return text;
    }
}

/// <summary>Sanitizes untrusted file names (e.g. suggested download names) for display/metadata only.</summary>
public static class FileNameSanitizer
{
    private const int MaxLength = 120;

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name, string fallback = "file")
    {
        var candidate = name ?? string.Empty;

        // Keep only the last path segment, whatever separator was used.
        var lastSeparator = candidate.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            candidate = candidate[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            builder.Append(char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        var cleaned = builder.ToString();
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim().Trim('.', ' ');
        if (cleaned.Length == 0 || ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(cleaned)))
        {
            cleaned = fallback;
        }

        if (cleaned.Length > MaxLength)
        {
            var extension = Path.GetExtension(cleaned);
            extension = extension.Length is > 0 and <= 16 ? extension : string.Empty;
            cleaned = cleaned[..(MaxLength - extension.Length)] + extension;
        }

        return cleaned;
    }
}
