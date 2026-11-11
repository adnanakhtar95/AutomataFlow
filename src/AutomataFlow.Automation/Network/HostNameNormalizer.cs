using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AutomataFlow.Automation.Network;

/// <summary>A canonical host: lower-case ASCII (punycode) name, or a parsed IP literal.</summary>
/// <param name="Value">Canonical text used for rule matching (IPv6 without brackets).</param>
/// <param name="Literal">The address when the host is an IP literal; otherwise <c>null</c>.</param>
public sealed record NormalizedHost(string Value, IPAddress? Literal)
{
    public bool IsIpLiteral => Literal is not null;
}

/// <summary>
/// Canonicalizes hosts before any policy decision and rejects ambiguous forms that different URL parsers interpret
/// differently (decimal/octal/hex IPv4, zone IDs, userinfo, backslashes, empty or oversized labels).
/// </summary>
public static class HostNameNormalizer
{
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    public static bool TryNormalize(string? host, out NormalizedHost? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrEmpty(host))
        {
            error = "empty-host";
            return false;
        }

        // IPv6 literal, possibly in brackets as it appears in URLs and CONNECT targets.
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (candidate.Contains(':', StringComparison.Ordinal))
        {
            if (candidate.Contains('%', StringComparison.Ordinal))
            {
                error = "ipv6-zone-id";
                return false;
            }

            if (!IPAddress.TryParse(candidate, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "invalid-ipv6";
                return false;
            }

            result = new NormalizedHost(v6.ToString(), v6);
            return true;
        }

        foreach (var c in host)
        {
            if (c <= ' ' || c == '\u007f' || c is '\\' or '/' or '@' or '%' or '?' or '#' or '[' or ']')
            {
                error = "invalid-character";
                return false;
            }
        }

        // Only genuinely internationalized names go through IDN mapping; ASCII names are validated by the
        // rules below so the outcome (and reason) does not depend on the platform's IDN implementation.
        var trimmed = host.TrimEnd('.');
        string ascii;
        if (trimmed.All(char.IsAscii))
        {
            ascii = trimmed;
        }
        else
        {
            try
            {
                ascii = Idn.GetAscii(trimmed);
            }
            catch (ArgumentException)
            {
                error = "invalid-idn";
                return false;
            }
        }

        ascii = ascii.ToLowerInvariant();
        if (ascii.Length == 0 || ascii.Length > MaxHostLength)
        {
            error = "invalid-length";
            return false;
        }

        var labels = ascii.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > MaxLabelLength)
            {
                error = "invalid-label";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                error = "invalid-label";
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                {
                    error = "invalid-character";
                    return false;
                }
            }
        }

        // WHATWG URL parsing treats a host whose last label is numeric (or hex) as an IPv4 address and accepts
        // forms such as 2130706433 or 0x7f.1. Only canonical dotted-quad literals are accepted here.
        var last = labels[^1];
        if (IsNumericLike(last))
        {
            if (TryParseCanonicalIPv4(labels, out var v4))
            {
                result = new NormalizedHost(v4.ToString(), v4);
                return true;
            }

            error = "non-canonical-ipv4";
            return false;
        }

        result = new NormalizedHost(ascii, null);
        return true;
    }

    private static bool IsNumericLike(string label) =>
        label.All(char.IsAsciiDigit) || label.StartsWith("0x", StringComparison.Ordinal);

    private static bool TryParseCanonicalIPv4(string[] labels, out IPAddress address)
    {
        address = IPAddress.None;
        if (labels.Length != 4)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            var label = labels[i];
            if (label.Length is 0 or > 3 || !label.All(char.IsAsciiDigit) || (label.Length > 1 && label[0] == '0'))
            {
                return false;
            }

            var value = int.Parse(label, NumberStyles.None, CultureInfo.InvariantCulture);
            if (value > 255)
            {
                return false;
            }

            bytes[i] = (byte)value;
        }

        address = new IPAddress(bytes);
        return true;
    }
}
