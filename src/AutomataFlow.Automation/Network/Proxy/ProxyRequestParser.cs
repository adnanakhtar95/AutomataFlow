using System.Globalization;
using System.Text;

namespace AutomataFlow.Automation.Network.Proxy;

/// <summary>A parsed proxy request head (request line plus headers, no body).</summary>
public sealed record ProxyRequest(string Method, string Target, string Version, IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public bool IsConnect => Method == "CONNECT";

    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public int CountHeader(string name) =>
        Headers.Count(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Deliberately strict HTTP/1.x request-head parser for the egress proxy. Anything ambiguous is rejected rather
/// than interpreted, to avoid request smuggling and parser-differential attacks.
/// </summary>
public static class ProxyRequestParser
{
    public const int MaxHeaders = 100;

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "CONNECT", "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS",
    };

    /// <param name="head">The bytes before the terminating CRLF CRLF.</param>
    public static bool TryParse(ReadOnlySpan<byte> head, out ProxyRequest? request, out string? error)
    {
        request = null;
        error = null;

        // Only printable ASCII plus CR, LF and TAB. Browsers never need anything else in a request head, and
        // control or non-ASCII bytes are classic parser-differential and smuggling vectors.
        foreach (var b in head)
        {
            var allowed = b is >= 0x20 and <= 0x7e or (byte)'\r' or (byte)'\n' or (byte)'\t';
            if (!allowed)
            {
                error = "invalid-byte-in-head";
                return false;
            }
        }

        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        foreach (var line in lines)
        {
            if (line.Contains('\r', StringComparison.Ordinal) || line.Contains('\n', StringComparison.Ordinal))
            {
                error = "bare-cr-or-lf";
                return false;
            }
        }

        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
        {
            error = "malformed-request-line";
            return false;
        }

        var (method, target, version) = (parts[0], parts[1], parts[2]);
        if (!AllowedMethods.Contains(method))
        {
            error = "method-not-allowed";
            return false;
        }

        if (version is not ("HTTP/1.1" or "HTTP/1.0"))
        {
            error = "unsupported-version";
            return false;
        }

        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                error = "empty-header-line";
                return false;
            }

            if (line[0] is ' ' or '\t')
            {
                error = "obsolete-line-folding";
                return false;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                error = "malformed-header";
                return false;
            }

            var name = line[..colon];
            if (!name.All(IsTokenChar))
            {
                error = "malformed-header-name";
                return false;
            }

            headers.Add(new(name, line[(colon + 1)..].Trim(' ', '\t')));
            if (headers.Count > MaxHeaders)
            {
                error = "too-many-headers";
                return false;
            }
        }

        request = new ProxyRequest(method, target, version, headers);
        return true;
    }

    /// <summary>Parses an authority-form CONNECT target such as <c>example.com:443</c> or <c>[::1]:443</c>.</summary>
    public static bool TryParseAuthority(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        int separator;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= target.Length || target[close + 1] != ':')
            {
                return false;
            }

            separator = close + 1;
        }
        else
        {
            separator = target.LastIndexOf(':');
            if (separator <= 0 || target.IndexOf(':', StringComparison.Ordinal) != separator)
            {
                return false;
            }
        }

        host = target[..separator];
        var portText = target[(separator + 1)..];
        return portText.Length is > 0 and <= 5
            && portText.All(char.IsAsciiDigit)
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);
}
