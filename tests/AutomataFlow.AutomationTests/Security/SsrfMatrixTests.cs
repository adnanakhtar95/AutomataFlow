using System.Net;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Security;

/// <summary>
/// SSRF attack matrix against real Chromium (user requirement C). The policy deliberately ALLOWS the attacking
/// host names (and even the internal IP literals), so every block below must come from the IP layer, the
/// redirect inspection or the egress proxy, not merely from a missing allow rule.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class SsrfMatrixTests(BrowserFixture fixture)
{
    private EngineHarness Engine => fixture.Engine;

    private FixtureServers Servers => fixture.Servers;

    private void AssertNoInternalAccess(int internalHitsBefore)
    {
        Assert.Equal(internalHitsBefore, Servers.InternalHits.Count);
        Assert.DoesNotContain(Engine.Connector.Attempts, e => IPAddress.IsLoopback(e.Address) && e.Port == Servers.InternalPort);
        Assert.DoesNotContain(Engine.Connector.Attempts, e => e.Address.Equals(IPAddress.Parse("10.0.0.5")) || e.Address.Equals(IPAddress.Parse("169.254.169.254")));
    }

    public static TheoryData<string> DirectTargets => new()
    {
        "http://127.0.0.1:{internal}/",
        "http://localhost:{internal}/",
        "http://[::1]:{internal}/",
        "http://[::ffff:127.0.0.1]:{internal}/",
        "http://2130706433:{internal}/",
        "http://0x7f.1:{internal}/",
        "http://10.0.0.5/",
        "http://169.254.169.254/latest/meta-data/",
        "http://[fd00::1]/",
        "http://private.test/",
        "http://loop.test/",
        "http://metadata.test/latest/meta-data/",
        "http://mixed.test/",
        "http://v6loop.test/",
        "http://v6ula.test/",
        "http://v6ll.test/",
        "http://mapped.test/",
        "http://nat64.test/",
        "http://unknown-host.test/",
    };

    [Theory]
    [MemberData(nameof(DirectTargets))]
    public async Task Direct_navigation_to_internal_destinations_is_blocked(string template)
    {
        var url = new Uri(template.Replace("{internal}", Servers.InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var before = Servers.InternalHits.Count;
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(url, CancellationToken.None));

        Assert.Equal(BrowserErrorCode.NavigationBlocked, error.Code);
        Assert.True(session.BlockedRequestCount > 0);
        AssertNoInternalAccess(before);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>fetch('http://169.254.169.254/')</script>")]
    [InlineData("chrome://version")]
    [InlineData("view-source:http://public.test/")]
    [InlineData("ftp://public.test/")]
    public async Task Non_web_schemes_are_blocked_before_the_browser_is_touched(string url)
    {
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri(url), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.NavigationBlocked, error.Code);
    }

    public static TheoryData<string> RedirectTargets => new()
    {
        "http://127.0.0.1:{internal}/landing",
        "https://127.0.0.1:{internal}/landing",
        "http://[::1]:{internal}/landing",
        "http://localhost:{internal}/landing",
        "http://169.254.169.254/latest/meta-data/iam/",
        "http://private.test/",
        "http://metadata.test/",
        "http://10.0.0.5/",
    };

    [Theory]
    [MemberData(nameof(RedirectTargets))]
    public async Task Public_pages_redirecting_to_internal_destinations_are_blocked(string template)
    {
        var target = template.Replace("{internal}", Servers.InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var before = Servers.InternalHits.Count;
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() =>
            session.NavigateAsync(new Uri("http://public.test/redirect?to=" + Uri.EscapeDataString(target)), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.NavigationBlocked, error.Code);
        AssertNoInternalAccess(before);
        Assert.Contains(Engine.Sink.For(session.Id), e => e.EventType == "RequestBlocked");
    }

    [Fact]
    public async Task Iframes_images_fetch_and_popups_towards_internal_services_are_blocked()
    {
        var before = Servers.InternalHits.Count;
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var page = await session.NavigateAsync(new Uri("http://public.test/embeds?base=" + Uri.EscapeDataString(Servers.InternalBase)), CancellationToken.None);
        await session.ClickAsync(ElementSelector.TestId("popup-link"), CancellationToken.None);
        await Wait.UntilAsync(() => session.BlockedRequestCount >= 4, TimeSpan.FromSeconds(10));

        Assert.Equal(200, page.Status);
        Assert.True(session.BlockedRequestCount >= 4, $"Expected iframe, image, fetch and popup to be blocked; blocked {session.BlockedRequestCount}.");
        AssertNoInternalAccess(before);
    }

    [Fact]
    public async Task Websockets_towards_internal_services_are_blocked()
    {
        var before = Servers.InternalHits.Count;
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        await session.NavigateAsync(new Uri("http://public.test/ws?url=" + Uri.EscapeDataString($"ws://127.0.0.1:{Servers.InternalPort}/socket")), CancellationToken.None);
        var blocked = await Wait.UntilAsync(() => Engine.Sink.For(session.Id).Any(e => e.EventType == "RequestBlocked" && e.Properties["layer"] == "websocket"), TimeSpan.FromSeconds(10));

        Assert.True(blocked);
        AssertNoInternalAccess(before);
    }

    [Fact]
    public async Task Dns_rebinding_after_the_first_load_never_reaches_the_rebound_address()
    {
        var before = Servers.InternalHits.Count;
        Engine.Dns.Reset("rebind.test");
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var page = await session.NavigateAsync(new Uri("http://rebind.test/rebind"), CancellationToken.None);
        var denied = await Wait.UntilAsync(
            () => Engine.Sink.For(session.Id).Any(e => e.EventType == "RequestBlocked" && e.Properties["reason"] == "ipv4-loopback"),
            TimeSpan.FromSeconds(10));

        Assert.Equal(200, page.Status);
        Assert.True(denied, "The second (rebound) resolution must be denied.");
        Assert.True(Engine.Dns.LookupCount("rebind.test") >= 2);
        Assert.DoesNotContain(Engine.Connector.Attempts, e => IPAddress.IsLoopback(e.Address) && e.Port == 80);
        AssertNoInternalAccess(before);
    }

    [Fact]
    public async Task Pages_cannot_use_the_egress_proxy_itself_as_a_target()
    {
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);
        var proxyPort = Engine.Proxy.Endpoint!.Port;
        var attemptsBefore = Engine.Connector.Attempts.Count(e => e.Port == proxyPort);

        await session.NavigateAsync(new Uri($"http://public.test/self?port={proxyPort}"), CancellationToken.None);
        var blocked = await Wait.UntilAsync(() => session.BlockedRequestCount > 0, TimeSpan.FromSeconds(10));

        Assert.True(blocked);
        Assert.Equal(attemptsBefore, Engine.Connector.Attempts.Count(e => e.Port == proxyPort));
    }

    [Fact]
    public async Task WebRtc_cannot_send_udp_around_the_proxy()
    {
        var before = Servers.UdpPackets;
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        await session.NavigateAsync(new Uri($"http://public.test/webrtc?ip={Servers.LanAddress}&port={Servers.UdpPort}"), CancellationToken.None);
        await Task.Delay(3000);

        Assert.Equal(before, Servers.UdpPackets);
    }

    [Fact]
    public async Task The_local_democrm_exception_is_port_precise()
    {
        var before = Servers.InternalHits.Count;
        await using var session = await Engine.CreateSessionAsync(fixture.DemoCrmPolicy);

        var allowed = await session.NavigateAsync(new Uri(fixture.DemoCrm.BaseUrl + "/Login"), CancellationToken.None);
        var otherPort = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri(Servers.InternalBase + "/"), CancellationToken.None));
        var otherHost = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri($"http://localhost:{fixture.DemoCrm.Port}/Login"), CancellationToken.None));

        Assert.Equal(200, allowed.Status);
        Assert.Equal(BrowserErrorCode.NavigationBlocked, otherPort.Code);
        Assert.Equal(BrowserErrorCode.NavigationBlocked, otherHost.Code);
        AssertNoInternalAccess(before);
    }

    [Fact]
    public async Task Allowed_public_destinations_still_work()
    {
        await using var session = await Engine.CreateSessionAsync(fixture.HostilePolicy);

        var result = await session.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);

        Assert.Equal(200, result.Status);
        Assert.Equal("public", await session.GetTextAsync(ElementSelector.TestId("ok"), CancellationToken.None));
    }
}
