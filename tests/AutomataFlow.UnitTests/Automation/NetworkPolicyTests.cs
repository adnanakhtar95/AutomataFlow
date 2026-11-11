using System.Net;
using AutomataFlow.Automation.Network;

namespace AutomataFlow.UnitTests.Automation;

public class IpAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", "ipv4-loopback")]
    [InlineData("127.255.255.254", "ipv4-loopback")]
    [InlineData("10.0.0.5", "ipv4-private")]
    [InlineData("172.16.0.1", "ipv4-private")]
    [InlineData("172.31.255.255", "ipv4-private")]
    [InlineData("192.168.1.10", "ipv4-private")]
    [InlineData("169.254.169.254", "ipv4-link-local-or-metadata")]
    [InlineData("100.64.0.1", "ipv4-shared-cgnat")]
    [InlineData("0.0.0.0", "ipv4-this-network")]
    [InlineData("224.0.0.1", "ipv4-multicast")]
    [InlineData("255.255.255.255", "ipv4-broadcast")]
    [InlineData("192.0.2.1", "ipv4-documentation")]
    [InlineData("198.18.0.1", "ipv4-benchmarking")]
    [InlineData("240.0.0.1", "ipv4-reserved")]
    [InlineData("168.63.129.16", "ipv4-cloud-platform")]
    [InlineData("::1", "ipv6-loopback")]
    [InlineData("::", "ipv6-unspecified")]
    [InlineData("::ffff:127.0.0.1", "ipv4-mapped-ipv4-loopback")]
    [InlineData("::ffff:10.1.2.3", "ipv4-mapped-ipv4-private")]
    [InlineData("::ffff:169.254.169.254", "ipv4-mapped-ipv4-link-local-or-metadata")]
    [InlineData("::127.0.0.1", "ipv6-ipv4-compatible")]
    [InlineData("64:ff9b::7f00:1", "ipv6-nat64-ipv4-loopback")]
    [InlineData("64:ff9b::a9fe:a9fe", "ipv6-nat64-ipv4-link-local-or-metadata")]
    [InlineData("2002:7f00:1::1", "ipv6-6to4-ipv4-loopback")]
    [InlineData("2002:c0a8:0101::1", "ipv6-6to4-ipv4-private")]
    [InlineData("2001:0:4136:e378::1", "ipv6-teredo")]
    [InlineData("fe80::1", "ipv6-link-or-site-local")]
    [InlineData("fec0::1", "ipv6-link-or-site-local")]
    [InlineData("fc00::1", "ipv6-unique-local")]
    [InlineData("fd00:ec2::254", "ipv6-unique-local")]
    [InlineData("ff02::1", "ipv6-multicast")]
    [InlineData("2001:db8::1", "ipv6-documentation")]
    [InlineData("100::1", "ipv6-discard")]
    public void Internal_and_reserved_addresses_are_denied(string address, string expectedReason)
    {
        Assert.Equal(expectedReason, IpAddressPolicy.GetDenyReason(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    public void Public_addresses_are_allowed(string address)
    {
        Assert.Null(IpAddressPolicy.GetDenyReason(IPAddress.Parse(address)));
    }

    [Fact]
    public void Scoped_ipv6_addresses_are_denied()
    {
        var scoped = IPAddress.Parse("2606:4700:4700::1111");
        scoped.ScopeId = 3;

        Assert.Equal("ipv6-scoped", IpAddressPolicy.GetDenyReason(scoped));
    }
}

public class HostNameNormalizerTests
{
    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("sub.example.com", "sub.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "::1")]
    [InlineData("[::ffff:7f00:1]", "::ffff:127.0.0.1")]
    [InlineData("::1", "::1")]
    public void Valid_hosts_are_canonicalized(string input, string expected)
    {
        Assert.True(HostNameNormalizer.TryNormalize(input, out var host, out var error), error);
        Assert.Equal(expected, host!.Value);
    }

    [Theory]
    [InlineData("2130706433", "non-canonical-ipv4")]
    [InlineData("0x7f.1", "non-canonical-ipv4")]
    [InlineData("0177.0.0.1", "non-canonical-ipv4")]
    [InlineData("127.1", "non-canonical-ipv4")]
    [InlineData("127.000.000.001", "non-canonical-ipv4")]
    [InlineData("1.2.3.256", "non-canonical-ipv4")]
    [InlineData("example.123", "non-canonical-ipv4")]
    [InlineData("[fe80::1%eth0]", "ipv6-zone-id")]
    [InlineData("user@evil.test", "invalid-character")]
    [InlineData("evil.test\\@good.test", "invalid-character")]
    [InlineData("a..b", "invalid-label")]
    [InlineData("-bad.test", "invalid-label")]
    [InlineData("under_score.test", "invalid-character")]
    [InlineData("", "empty-host")]
    [InlineData("exa mple.com", "invalid-character")]
    public void Ambiguous_or_malformed_hosts_are_rejected(string input, string expectedError)
    {
        Assert.False(HostNameNormalizer.TryNormalize(input, out _, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Over_long_labels_and_hosts_are_rejected()
    {
        Assert.False(HostNameNormalizer.TryNormalize(new string('a', 64) + ".test", out _, out _));
        Assert.False(HostNameNormalizer.TryNormalize(string.Join('.', Enumerable.Repeat("abcdefghij", 26)), out _, out _));
    }
}

public class EgressPolicyTests
{
    [Fact]
    public void Rules_parse_scheme_host_and_default_or_explicit_ports()
    {
        var https = AllowedHostRule.Parse("https://Portal.Example.com");
        var http = AllowedHostRule.Parse("http://127.0.0.1:5080", allowPrivateNetwork: true);
        var wildcard = AllowedHostRule.Parse("https://*.example.com:8443");

        Assert.Equal(("https", "portal.example.com", 443, false), (https.Scheme, https.Host, https.Port, https.AllowPrivateNetwork));
        Assert.Equal(("http", "127.0.0.1", 5080, true), (http.Scheme, http.Host, http.Port, http.AllowPrivateNetwork));
        Assert.Equal(("*.example.com", 8443), (wildcard.Host, wildcard.Port));
    }

    [Theory]
    [InlineData("portal.example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com:0")]
    [InlineData("https://example.com:70000")]
    [InlineData("https://*.10.0.0.1")]
    [InlineData("https://2130706433")]
    public void Invalid_rules_are_rejected(string rule)
    {
        Assert.ThrowsAny<FormatException>(() => AllowedHostRule.Parse(rule));
    }

    [Fact]
    public void Wildcards_match_subdomains_only_and_never_lookalike_suffixes()
    {
        var policy = EgressPolicy.Create("https://*.example.com");

        Assert.NotNull(policy.FindRule("a.example.com", 443, "https"));
        Assert.NotNull(policy.FindRule("deep.a.example.com", 443, "https"));
        Assert.Null(policy.FindRule("example.com", 443, "https"));
        Assert.Null(policy.FindRule("evilexample.com", 443, "https"));
        Assert.Null(policy.FindRule("example.com.evil.test", 443, "https"));
    }

    [Fact]
    public void Port_and_scheme_must_match_but_connect_tunnels_match_any_scheme()
    {
        var policy = EgressPolicy.Create("https://example.com");

        Assert.Null(policy.FindRule("example.com", 8443, "https"));
        Assert.Null(policy.FindRule("example.com", 443, "http"));
        Assert.NotNull(policy.FindRule("example.com", 443, scheme: null));
    }
}

public class RequestGuardTests
{
    private static readonly EgressPolicy Policy = new(
    [
        AllowedHostRule.Parse("https://portal.example.com"),
        AllowedHostRule.Parse("http://127.0.0.1:5080", allowPrivateNetwork: true),
        AllowedHostRule.Parse("https://10.0.0.5"),
    ]);

    [Theory]
    [InlineData("https://portal.example.com/login?x=1")]
    [InlineData("wss://portal.example.com/socket")]
    [InlineData("http://127.0.0.1:5080/Customers")]
    [InlineData("about:blank")]
    public void Authorized_urls_are_allowed(string url)
    {
        Assert.True(RequestGuard.Evaluate(new Uri(url), Policy).Allowed);
    }

    [Theory]
    [InlineData("https://other.example.com/", "host-not-allowed")]
    [InlineData("http://portal.example.com/", "host-not-allowed")]
    [InlineData("https://portal.example.com:8443/", "host-not-allowed")]
    [InlineData("http://127.0.0.1:5432/", "host-not-allowed")]
    [InlineData("http://127.0.0.1:5081/", "host-not-allowed")]
    [InlineData("https://user:pw@portal.example.com/", "userinfo-not-allowed")]
    [InlineData("file:///etc/passwd", "scheme-not-allowed")]
    [InlineData("ftp://portal.example.com/", "scheme-not-allowed")]
    [InlineData("data:text/html,<script>alert(1)</script>", "scheme-not-allowed")]
    [InlineData("javascript:alert(1)", "scheme-not-allowed")]
    [InlineData("chrome://settings", "scheme-not-allowed")]
    [InlineData("view-source:https://portal.example.com/", "scheme-not-allowed")]
    [InlineData("about:config", "scheme-not-allowed")]
    [InlineData("https://10.0.0.5/", "ipv4-private")]
    public void Unauthorized_urls_are_denied_with_a_reason(string url, string reason)
    {
        var decision = RequestGuard.Evaluate(new Uri(url), Policy);

        Assert.False(decision.Allowed);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void Private_exception_is_port_precise()
    {
        Assert.True(RequestGuard.Evaluate(new Uri("http://127.0.0.1:5080/"), Policy).Allowed);
        Assert.False(RequestGuard.Evaluate(new Uri("http://localhost:5080/"), Policy).Allowed);
        Assert.False(RequestGuard.Evaluate(new Uri("http://[::1]:5080/"), Policy).Allowed);
    }
}
