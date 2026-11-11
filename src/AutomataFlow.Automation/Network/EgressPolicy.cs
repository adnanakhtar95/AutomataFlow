using System.Globalization;

namespace AutomataFlow.Automation.Network;

/// <summary>
/// One authorized destination: scheme, host (exact, or <c>*.domain</c> for subdomains only) and port.
/// <see cref="AllowPrivateNetwork"/> permits private/loopback destinations for this exact host:port and is accepted
/// only in Development mode (e.g. the local DemoCrm); hosted modes reject it.
/// </summary>
public sealed record AllowedHostRule
{
    private AllowedHostRule(string scheme, string host, int port, bool allowPrivateNetwork)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    /// <summary><c>https</c> or <c>http</c>.</summary>
    public string Scheme { get; }

    /// <summary>Canonical host, or <c>*.example.com</c>.</summary>
    public string Host { get; }

    public int Port { get; }

    public bool AllowPrivateNetwork { get; }

    public bool IsWildcard => Host.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>Parses <c>https://portal.example.com</c>, <c>https://*.example.com:8443</c> or <c>http://127.0.0.1:5080</c>.</summary>
    public static AllowedHostRule Parse(string value, bool allowPrivateNetwork = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new FormatException("Allowed host rules must include a scheme, e.g. https://portal.example.com.");
        }

        var scheme = value[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("https" or "http"))
        {
            throw new FormatException("Allowed host rules support only the https and http schemes.");
        }

        var authority = value[(schemeEnd + 3)..].TrimEnd('/');
        if (authority.Length == 0 || authority.Contains('/', StringComparison.Ordinal) || authority.Contains('@', StringComparison.Ordinal))
        {
            throw new FormatException("Allowed host rules must be scheme://host[:port] without path or credentials.");
        }

        var port = scheme == "https" ? 443 : 80;
        var host = authority;
        var portSeparator = authority.StartsWith('[') ? authority.IndexOf("]:", StringComparison.Ordinal) + 1 : authority.LastIndexOf(':');
        if (portSeparator > 0)
        {
            host = authority[..portSeparator];
            if (!int.TryParse(authority[(portSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                throw new FormatException("Allowed host rule has an invalid port.");
            }
        }

        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        if (!HostNameNormalizer.TryNormalize(wildcard ? host[2..] : host, out var normalized, out var error))
        {
            throw new FormatException($"Allowed host rule has an invalid host ({error}).");
        }

        if (wildcard && normalized!.IsIpLiteral)
        {
            throw new FormatException("Wildcards are only allowed for domain names.");
        }

        return new AllowedHostRule(scheme, wildcard ? "*." + normalized!.Value : normalized!.Value, port, allowPrivateNetwork);
    }

    /// <summary>Exact match, or subdomain match for wildcards (never the bare domain or a look-alike suffix).</summary>
    public bool MatchesHost(string canonicalHost)
    {
        if (!IsWildcard)
        {
            return string.Equals(Host, canonicalHost, StringComparison.Ordinal);
        }

        var suffix = Host[1..]; // ".example.com"
        return canonicalHost.Length > suffix.Length && canonicalHost.EndsWith(suffix, StringComparison.Ordinal);
    }

    public override string ToString() => $"{Scheme}://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The set of destinations one browser session may reach. Anything not matched is denied.</summary>
public sealed class EgressPolicy
{
    public EgressPolicy(IEnumerable<AllowedHostRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules.ToList();
    }

    public IReadOnlyList<AllowedHostRule> Rules { get; }

    public static EgressPolicy Create(params string[] rules) => new(rules.Select(r => AllowedHostRule.Parse(r)));

    /// <summary>
    /// Finds the rule for a canonical host and port. <paramref name="scheme"/> is <c>null</c> for CONNECT tunnels,
    /// where only host and port are visible (https, wss and ws all arrive as CONNECT).
    /// </summary>
    public AllowedHostRule? FindRule(string canonicalHost, int port, string? scheme)
    {
        foreach (var rule in Rules)
        {
            if (rule.Port == port
                && rule.MatchesHost(canonicalHost)
                && (scheme is null || string.Equals(rule.Scheme, scheme, StringComparison.Ordinal)))
            {
                return rule;
            }
        }

        return null;
    }
}
