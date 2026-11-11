using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;
using Microsoft.Playwright;

namespace AutomataFlow.AutomationTests.Security;

/// <summary>User requirement A: if the egress proxy is unavailable, browser execution fails; never direct access.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class FailClosedTests(BrowserFixture fixture)
{
    [Fact]
    public async Task No_session_is_created_when_the_proxy_is_unavailable()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        engine.Proxy.Start();
        await engine.Proxy.StopAsync();

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => engine.CreateSessionAsync(BrowserFixture.PublicPolicy));

        Assert.Equal(BrowserErrorCode.EgressUnavailable, error.Code);
        Assert.Equal(0, engine.Host.LaunchCount);
    }

    [Fact]
    public async Task Losing_the_proxy_mid_session_fails_requests_instead_of_going_direct()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using var session = await engine.CreateSessionAsync(fixture.DemoCrmPolicy);
        var first = await session.NavigateAsync(new Uri(fixture.DemoCrm.BaseUrl + "/Login"), CancellationToken.None);
        var attemptsBefore = engine.Connector.Attempts.Count;

        await engine.Proxy.StopAsync();
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri(fixture.DemoCrm.BaseUrl + "/Login?again=1"), CancellationToken.None));

        Assert.Equal(200, first.Status);
        Assert.Equal(BrowserErrorCode.EgressUnavailable, error.Code);
        Assert.Equal(attemptsBefore, engine.Connector.Attempts.Count);
    }

    [Fact]
    public async Task A_context_created_without_session_credentials_cannot_reach_anything()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using (await engine.CreateSessionAsync(BrowserFixture.PublicPolicy))
        {
        }

        var hitsBefore = fixture.Servers.PublicHits.Count;
        var internalBefore = fixture.Servers.InternalHits.Count;

        // Bypass the factory entirely: a raw context on the engine's browser has no per-session credentials.
        var context = await engine.Host.SharedBrowserForTesting!.NewContextAsync();
        var page = await context.NewPageAsync();
        var results = new List<string>();
        foreach (var url in new[] { "http://public.test/", fixture.Servers.InternalBase + "/", fixture.DemoCrm.BaseUrl + "/Login", "https://example.com/" })
        {
            try
            {
                var response = await page.GotoAsync(url, new PageGotoOptions { Timeout = 5000 });
                results.Add($"{url} -> {response?.Status}");
            }
            catch (PlaywrightException ex)
            {
                results.Add($"{url} -> {ex.Message.Split('\n')[0]}");
            }
            catch (TimeoutException)
            {
                // https: CONNECT gets 407 and Chromium waits for credentials that never come. Still fail-closed.
                results.Add($"{url} -> timed out (no access)");
            }
        }

        await context.CloseAsync();

        Assert.Equal(hitsBefore, fixture.Servers.PublicHits.Count);
        Assert.Equal(internalBefore, fixture.Servers.InternalHits.Count);
        Assert.All(results, r => Assert.DoesNotContain("-> 200", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proxy_credentials_are_revoked_when_the_session_ends()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        Assert.Equal(1, engine.Proxy.Sessions.Count);

        await session.DisposeAsync();

        Assert.Equal(0, engine.Proxy.Sessions.Count);
    }
}
