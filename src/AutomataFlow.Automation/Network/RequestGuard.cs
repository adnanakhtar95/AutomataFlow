namespace AutomataFlow.Automation.Network;

/// <param name="Reason">Stable reason code when denied (see <see cref="EgressReasons"/>).</param>
public sealed record RequestDecision(bool Allowed, string? Reason)
{
    public static RequestDecision Allow { get; } = new(true, null);

    public static RequestDecision Deny(string reason) => new(false, reason);
}

/// <summary>Stable reason codes shared by the request guard and the egress proxy.</summary>
public static class EgressReasons
{
    public const string SchemeNotAllowed = "scheme-not-allowed";
    public const string UserInfoNotAllowed = "userinfo-not-allowed";
    public const string HostNotAllowed = "host-not-allowed";
    public const string InvalidHost = "invalid-host";
    public const string MixedDnsAnswer = "mixed-dns-answer";
    public const string DnsFailure = "dns-failure";
    public const string UpstreamUnreachable = "upstream-unreachable";
    public const string Unauthenticated = "proxy-authentication-required";
    public const string TooManyConnections = "too-many-connections";
    public const string QuotaExceeded = "byte-quota-exceeded";
    public const string BadRequest = "bad-request";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string NotImplemented = "not-implemented";
}

/// <summary>
/// URL-level check applied before navigation and to every request the browser lets us intercept (documents,
/// subresources, frames, popups, WebSockets). It never resolves DNS; name resolution and the authoritative
/// IP decision happen in the egress proxy (ADR-036).
/// </summary>
public static class RequestGuard
{
    public static RequestDecision Evaluate(Uri url, EgressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(policy);

        if (!url.IsAbsoluteUri)
        {
            return RequestDecision.Deny(EgressReasons.InvalidHost);
        }

        if (url.Scheme == "about" && string.Equals(url.OriginalString, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return RequestDecision.Allow;
        }

        var ruleScheme = url.Scheme switch
        {
            "https" or "wss" => "https",
            "http" or "ws" => "http",
            _ => null,
        };
        if (ruleScheme is null)
        {
            return RequestDecision.Deny(EgressReasons.SchemeNotAllowed);
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return RequestDecision.Deny(EgressReasons.UserInfoNotAllowed);
        }

        if (!HostNameNormalizer.TryNormalize(url.Host, out var host, out var hostError))
        {
            return RequestDecision.Deny($"{EgressReasons.InvalidHost}:{hostError}");
        }

        var rule = policy.FindRule(host!.Value, url.Port, ruleScheme);
        if (rule is null)
        {
            return RequestDecision.Deny(EgressReasons.HostNotAllowed);
        }

        if (host.Literal is { } literal && IpAddressPolicy.GetDenyReason(literal) is { } ipReason && !rule.AllowPrivateNetwork)
        {
            return RequestDecision.Deny(ipReason);
        }

        return RequestDecision.Allow;
    }
}
