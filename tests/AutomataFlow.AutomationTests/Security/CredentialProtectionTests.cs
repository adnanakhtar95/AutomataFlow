using System.Text.RegularExpressions;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Security;

/// <summary>Credentials and sensitive values never reach logs, exceptions or execution events (ADR-032).</summary>
[Collection(BrowserTestGroup.Name)]
public sealed partial class CredentialProtectionTests(BrowserFixture fixture)
{
    private const string Password = "DemoPass-2026!";
    private const string OtherSecret = "S3cr3t-Value-For-Leak-Test!";

    [Fact]
    public async Task Sensitive_values_never_appear_in_logs_events_or_error_messages()
    {
        await fixture.DemoCrm.ResetAsync();
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.ActionSeconds = 1);
        BrowserOperationException? failure;
        await using (var session = await engine.CreateSessionAsync(fixture.DemoCrmPolicy))
        {
            await session.NavigateAsync(new Uri(fixture.DemoCrm.BaseUrl + "/Login"), CancellationToken.None);
            await session.FillAsync(ElementSelector.TestId("login-email"), InputValue.Plain("agent@democrm.test"), CancellationToken.None);
            await session.FillAsync(ElementSelector.TestId("login-password"), InputValue.Sensitive(Password), CancellationToken.None);
            await session.SubmitAsync(ElementSelector.TestId("login-submit"), CancellationToken.None);

            // A failing fill with a sensitive value: the error must not echo it.
            failure = await Assert.ThrowsAsync<BrowserOperationException>(() =>
                session.FillAsync(ElementSelector.TestId("no-such-field"), InputValue.Sensitive(OtherSecret), CancellationToken.None));
        }

        var everything = engine.Sink.AllText().Concat(engine.Logs.Lines).Append(failure.Message).Append(failure.ToString()).ToList();
        Assert.NotEmpty(engine.Sink.Entries);
        Assert.DoesNotContain(everything, line => line.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(everything, line => line.Contains(OtherSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Url_query_values_are_redacted_in_events_and_logs()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using (var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy))
        {
            await session.NavigateAsync(new Uri("http://public.test/query?token=abc123secretvalue&page=2"), CancellationToken.None);
        }

        var everything = engine.Sink.AllText().Concat(engine.Logs.Lines).ToList();
        Assert.Contains(everything, line => line.Contains("token=***", StringComparison.Ordinal));
        Assert.DoesNotContain(everything, line => line.Contains("abc123secretvalue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Per_session_proxy_passwords_never_appear_in_logs()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using (var session = await engine.CreateSessionAsync(fixture.HostileSafePolicy()))
        {
            await session.NavigateAsync(new Uri("http://public.test/"), CancellationToken.None);
            await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri("http://private.test/"), CancellationToken.None));
        }

        // Proxy passwords are 32 lowercase hex characters; nothing else the engine logs has that shape.
        var everything = engine.Sink.AllText().Concat(engine.Logs.Lines).ToList();
        Assert.DoesNotContain(everything, line => ProxyPasswordShape().IsMatch(line));
    }

    [GeneratedRegex(@"(?<![0-9a-f-])[0-9a-f]{32}(?![0-9a-f-])")]
    private static partial Regex ProxyPasswordShape();
}

internal static class PolicyExtensions
{
    public static AutomataFlow.Automation.Network.EgressPolicy HostileSafePolicy(this BrowserFixture fixture) =>
        AutomataFlow.Automation.Network.EgressPolicy.Create("http://public.test", "http://private.test");
}
