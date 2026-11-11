using System.Diagnostics;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Browser;

[Collection(BrowserTestGroup.Name)]
public sealed class BrowserLifecycleTests(BrowserFixture fixture)
{
    private static int ChromiumProcessCount() =>
        Process.GetProcesses().Count(p =>
        {
            try
            {
                return p.ProcessName.StartsWith("chrome", StringComparison.Ordinal) || p.ProcessName.StartsWith("headless", StringComparison.Ordinal);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

    [Fact]
    public async Task Shared_mode_reuses_one_browser_with_a_fresh_context_per_session_and_records_launch_timing()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);

        for (var i = 0; i < 3; i++)
        {
            await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
            await session.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);
        }

        Assert.Equal(1, engine.Host.LaunchCount);
        Assert.NotNull(engine.Host.LastLaunchDuration);
        Assert.True(engine.Host.LastLaunchDuration > TimeSpan.Zero);
        Assert.Equal(3, engine.Sink.Entries.Count(e => e.EventType == "SessionStarted"));
        Assert.All(engine.Sink.Entries.Where(e => e.EventType == "SessionEnded"), e => Assert.NotNull(e.Properties["durationMs"]));
    }

    [Fact]
    public async Task Shared_browser_is_recycled_after_the_configured_number_of_sessions()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.RecycleAfterSessions = 2);

        for (var i = 0; i < 3; i++)
        {
            await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        }

        Assert.Equal(2, engine.Host.LaunchCount);
    }

    [Fact]
    public async Task Browser_per_session_mode_launches_and_closes_a_dedicated_browser_each_time()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Isolation = BrowserIsolation.BrowserPerSession);

        for (var i = 0; i < 2; i++)
        {
            await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
            await session.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);
        }

        Assert.Equal(2, engine.Host.LaunchCount);
        Assert.Null(engine.Host.SharedBrowserForTesting);
    }

    [Fact]
    public async Task A_disconnected_browser_is_replaced_for_the_next_session()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using (await engine.CreateSessionAsync(BrowserFixture.PublicPolicy))
        {
        }

        await engine.Host.SharedBrowserForTesting!.CloseAsync();
        await using var next = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        var result = await next.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);

        Assert.Equal(200, result.Status);
        Assert.Equal(2, engine.Host.LaunchCount);
    }

    [Fact]
    public async Task Disposing_the_engine_leaves_no_browser_processes_behind()
    {
        var baseline = ChromiumProcessCount();
        var engine = EngineHarness.Create(fixture.Servers);
        await using (var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy))
        {
            await session.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);
            Assert.True(ChromiumProcessCount() > baseline);
        }

        await engine.DisposeAsync();

        Assert.True(await Wait.UntilAsync(() => ChromiumProcessCount() <= baseline, TimeSpan.FromSeconds(15)), $"Chromium processes remain: {ChromiumProcessCount()} vs baseline {baseline}");
    }

    [Fact]
    public async Task Concurrent_sessions_are_limited_and_waiters_proceed_when_a_slot_frees()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.MaxConcurrentSessions = 1);
        var first = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        var second = engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await Task.Delay(500);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var secondSession = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(secondSession);
    }

    [Fact]
    public async Task Hosted_mode_rejects_private_network_and_plain_http_rules()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o =>
        {
            o.Mode = ExecutionMode.HostedMultiTenant;
            o.Isolation = BrowserIsolation.BrowserPerSession;
        });

        var privateRule = await Assert.ThrowsAsync<BrowserOperationException>(() => engine.CreateSessionAsync(fixture.DemoCrmPolicy));
        var plainHttp = await Assert.ThrowsAsync<BrowserOperationException>(() => engine.CreateSessionAsync(BrowserFixture.PublicPolicy));

        Assert.Equal(BrowserErrorCode.PolicyViolation, privateRule.Code);
        Assert.Equal(BrowserErrorCode.PolicyViolation, plainHttp.Code);
        Assert.Equal(0, engine.Host.LaunchCount);
    }
}
