using System.Globalization;
using System.Net;
using AutomataFlow.Application.Network;

namespace AutomataFlow.Application.Workflows.Urls;

/// <summary>A canonical origin: lowercase scheme, normalized host (A-label, no trailing dot), port.</summary>
public sealed class OriginParts
{
    internal OriginParts(string scheme, string host, int port, IPAddress? literal, bool isWildcard)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        Literal = literal;
        IsWildcard = isWildcard;
    }

    /// <summary><c>https</c> or <c>http</c>.</summary>
    public string Scheme { get; }

    /// <summary>Canonical host (IPv6 without brackets); <c>*.domain</c> for a wildcard.</summary>
    public string Host { get; }

    public int Port { get; }

    public IPAddress? Literal { get; }

    public bool IsWildcard { get; }

    public bool IsDefaultPort => Port == CanonicalOrigin.DefaultPort(Scheme);

    /// <summary><c>scheme://host[:port]</c> with the default port omitted and IPv6 in brackets.</summary>
    public override string ToString()
    {
        var host = Literal is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetworkV6 } ? "[" + Host + "]" : Host;
        return IsDefaultPort ? $"{Scheme}://{host}" : $"{Scheme}://{host}:{Port.ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// Origin canonicalization (docs/PHASE4_PLAN.md §6.1, §6.2; ADR-043, ADR-046). Literal origins in a definition must
/// already be canonical (the platform never silently rewrites a definition); <c>url</c> parameter values are
/// canonicalized at bind time. Hosts go through the shared <see cref="HostNameNormalizer"/>.
/// </summary>
public static class CanonicalOrigin
{
    public const int MaxOriginChars = 300;

    public static int DefaultPort(string scheme) => scheme == "https" ? 443 : 80;

    /// <summary>
    /// Parses <c>scheme://host[:port]</c> with at most one trailing <c>/</c> and canonicalizes it. Rejects paths, queries,
    /// fragments, userinfo, backslashes, whitespace, control characters, schemes other than http/https, invalid or
    /// non-canonical ports (leading zeros), and hosts the normalizer rejects. Wildcards only when
    /// <paramref name="allowWildcard"/> (domains only, never IP literals).
    /// </summary>
    public static OriginParts? TryCanonicalize(string value, bool allowWildcard, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxOriginChars)
        {
            error = "origin-length";
            return null;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '?' or '#' or '@')
            {
                error = "origin-invalid-character";
                return null;
            }
        }

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            error = "origin-missing-scheme";
            return null;
        }

        var scheme = value[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("https" or "http"))
        {
            error = "origin-unsupported-scheme";
            return null;
        }

        var authority = value[(schemeEnd + 3)..];
        if (authority.EndsWith('/'))
        {
            authority = authority[..^1];
        }

        if (authority.Length == 0 || authority.Contains('/', StringComparison.Ordinal))
        {
            error = "origin-has-path";
            return null;
        }

        var host = authority;
        var port = DefaultPort(scheme);
        var portSeparator = authority.StartsWith('[') ? authority.IndexOf("]:", StringComparison.Ordinal) + 1 : authority.LastIndexOf(':');
        if (portSeparator > 0 && (!authority.StartsWith('[') || authority[portSeparator] == ':'))
        {
            var portText = authority[(portSeparator + 1)..];
            if (portText.Length is 0 or > 5 || !portText.All(char.IsAsciiDigit) || (portText.Length > 1 && portText[0] == '0')
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                error = "origin-invalid-port";
                return null;
            }

            host = authority[..portSeparator];
        }

        if ((!host.StartsWith('[') && host.Contains(':', StringComparison.Ordinal)) || (host.StartsWith('[') && !host.EndsWith(']')))
        {
            error = "origin-invalid-host";
            return null;
        }

        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard && !allowWildcard)
        {
            error = "origin-wildcard-not-allowed";
            return null;
        }

        if (!HostNameNormalizer.TryNormalize(wildcard ? host[2..] : host, out var normalized, out var hostError))
        {
            error = "origin-invalid-host:" + hostError;
            return null;
        }

        if (wildcard && normalized!.IsIpLiteral)
        {
            error = "origin-wildcard-ip";
            return null;
        }

        return new OriginParts(scheme, wildcard ? "*." + normalized!.Value : normalized!.Value, port, normalized.Literal, wildcard);
    }

    /// <summary>True when <paramref name="value"/> is already in canonical form (no trailing slash, nothing rewritten).</summary>
    public static OriginParts? TryParseCanonical(string value, bool allowWildcard, out string? error)
    {
        var parts = TryCanonicalize(value, allowWildcard, out error);
        if (parts is null)
        {
            return null;
        }

        if (!string.Equals(parts.ToString(), value, StringComparison.Ordinal))
        {
            error = "origin-not-canonical";
            return null;
        }

        return parts;
    }
}
