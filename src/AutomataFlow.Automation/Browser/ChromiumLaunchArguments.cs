namespace AutomataFlow.Automation.Browser;

/// <summary>
/// Hardened Chromium command-line switches (ADR-030, ADR-036). The egress proxy itself is passed through
/// Playwright's proxy options; these switches make sure nothing can bypass it.
/// </summary>
public static class ChromiumLaunchArguments
{
    /// <summary>The only address Chromium may resolve or reach: the in-process egress proxy (always loopback).</summary>
    public const string ProxyHostAddress = "127.0.0.1";

    /// <summary>Bypass rule that removes Chromium's implicit loopback bypass, so localhost is proxied too.</summary>
    public const string ProxyLoopbackRule = "<-loopback>";

    public static IReadOnlyList<string> Build() =>
    [
        // Chromium cannot resolve any name or reach any IP literal itself; only the proxy address is excluded
        // (spike C2: without EXCLUDE the rule also blocks the proxy). DNS is done by the egress proxy.
        $"--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE {ProxyHostAddress}",

        // Protocols that do not go through an HTTP proxy.
        "--disable-quic",
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",

        // No background traffic, extensions or first-run behaviour.
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-sync",
        "--disable-extensions",
        "--disable-default-apps",
        "--disable-domain-reliability",
        "--disable-breakpad",
        "--no-first-run",
        "--no-default-browser-check",
        "--deny-permission-prompts",
    ];
}
