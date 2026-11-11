using System.Diagnostics;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Browser;

[Collection(BrowserTestGroup.Name)]
public sealed class SessionOperationsTests(BrowserFixture fixture)
{
    private EngineHarness Engine => fixture.Engine;

    private static Uri Public(string path) => new($"http://public.test{path}");

    [Fact]
    public async Task Fill_select_wait_and_read_work_with_typed_selectors_and_auto_wait()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/form"), CancellationToken.None);

        await session.FillAsync(ElementSelector.TestId("username"), InputValue.Plain("fictional.user"), CancellationToken.None);
        await session.SelectAsync(ElementSelector.TestId("color"), SelectChoice.ByLabel("Green"), CancellationToken.None);

        // The page echoes the selected option value; filled values are verified server-side in the DemoCrm tests.
        Assert.Equal("g", await session.GetTextAsync(ElementSelector.TestId("echo"), CancellationToken.None));

        await session.NavigateAsync(Public("/late"), CancellationToken.None);
        await session.WaitForAsync(WaitCondition.ElementVisible(ElementSelector.TestId("late")), CancellationToken.None);
        Assert.Equal("arrived", await session.GetTextAsync(ElementSelector.TestId("late"), CancellationToken.None));
    }

    [Fact]
    public async Task Navigation_reports_status_and_redacted_final_url()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        var result = await session.NavigateAsync(Public("/redirect?to=/landing%3Ftoken%3Dabc"), CancellationToken.None);

        Assert.Equal(200, result.Status);
        Assert.Equal("http://public.test/landing?token=***", result.FinalUrl);
        Assert.Equal(["http://public.test/redirect?to=***"], result.RedirectChain);
    }

    [Fact]
    public async Task Ambiguous_selectors_fail_instead_of_clicking_the_wrong_element()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/dup"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.ClickAsync(ElementSelector.TestId("twin"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.ElementNotUnique, error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Missing_elements_fail_with_a_classified_transient_error_after_the_action_timeout()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.ActionSeconds = 1);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.ClickAsync(ElementSelector.TestId("does-not-exist"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.ElementNotFound, error.Code);
        Assert.True(error.IsTransient);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Hung_navigation_times_out_as_a_transient_error()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.NavigationSeconds = 2);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(Public("/hang"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.Timeout, error.Code);
        Assert.True(error.IsTransient);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_aborts_a_hung_operation_quickly_and_closes_the_session()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.NavigateAsync(Public("/hang"), cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
        var afterwards = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(Public("/"), CancellationToken.None));
        Assert.Equal(BrowserErrorCode.SessionClosed, afterwards.Code);
    }

    [Fact]
    public async Task Session_deadline_aborts_long_running_work()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.SessionSeconds = 2);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(Public("/hang"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.SessionTimeout, error.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Javascript_dialogs_are_dismissed_and_recorded_instead_of_hanging()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        var result = await session.NavigateAsync(Public("/alert"), CancellationToken.None);

        Assert.Equal(200, result.Status);
        Assert.Equal("alert", await session.GetTextAsync(ElementSelector.TestId("ok"), CancellationToken.None));
        Assert.Contains(Engine.Sink.For(session.Id), e => e.EventType == "DialogDismissed");
    }

    [Fact]
    public async Task Popups_beyond_the_page_limit_are_closed()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.MaxPagesPerSession = 3);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/popups"), CancellationToken.None);

        for (var i = 1; i <= 6; i++)
        {
            await session.ClickAsync(ElementSelector.TestId($"pop-{i}"), CancellationToken.None);
        }

        Assert.True(await Wait.UntilAsync(() => ((BrowserSession)session).OpenPageCountForTesting <= 3, TimeSpan.FromSeconds(10)));
        Assert.True(await Wait.UntilAsync(() => engine.Sink.For(session.Id).Any(e => e.EventType == "PopupBlocked"), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Sessions_do_not_share_cookies_or_storage()
    {
        await using (var first = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy))
        {
            await first.NavigateAsync(Public("/storage-set"), CancellationToken.None);
            await first.NavigateAsync(Public("/storage-get"), CancellationToken.None);
            Assert.Equal("cookie=[af_test=leak] local=[leak]", await first.GetTextAsync(ElementSelector.TestId("storage"), CancellationToken.None));
        }

        await using var second = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await second.NavigateAsync(Public("/storage-get"), CancellationToken.None);

        Assert.Equal("cookie=[] local=[null]", await second.GetTextAsync(ElementSelector.TestId("storage"), CancellationToken.None));
    }

    [Fact]
    public async Task Service_workers_cannot_be_registered()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/sw"), CancellationToken.None);
        await session.WaitForAsync(WaitCondition.ElementVisible(ElementSelector.TestId("sw")), CancellationToken.None);

        // Give a registration attempt time to settle; the outcome must never be "registered".
        await Task.Delay(1000);
        Assert.NotEqual("registered", await session.GetTextAsync(ElementSelector.TestId("sw"), CancellationToken.None));
    }
}
