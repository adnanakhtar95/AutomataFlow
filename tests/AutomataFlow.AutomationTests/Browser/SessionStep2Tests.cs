using System.Diagnostics;
using System.Security.Cryptography;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Network;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Browser;

/// <summary>Phase 4 step 2 session extensions in real Chromium (docs/PHASE4_PLAN.md §0, ADR-038, ADR-047, ADR-053, ADR-060).</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class SessionStep2Tests(BrowserFixture fixture)
{
    private EngineHarness Engine => fixture.Engine;

    private static Uri Public(string path) => new($"http://public.test{path}");

    // ---------- OperationOptions: one total deadline (T4) ----------

    [Fact]
    public async Task An_explicit_submit_timeout_is_one_total_deadline_for_click_and_load()
    {
        // Defaults would allow ActionSeconds (10) + NavigationSeconds (30) = 40 s; the step budget is 2 s in total.
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/hang-form"), CancellationToken.None);
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() =>
            session.SubmitAsync(ElementSelector.TestId("go"), OperationOptions.WithTimeout(TimeSpan.FromSeconds(2)), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.Timeout, error.Code);
        Assert.True(error.IsTransient);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));

        // A step timeout never ends the session (ADR-038).
        var after = await session.NavigateAsync(Public("/"), CancellationToken.None);
        Assert.Equal(200, after.Status);
    }

    [Fact]
    public async Task An_explicit_timeout_overrides_the_configured_action_default()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() =>
            session.ClickAsync(ElementSelector.TestId("does-not-exist"), OperationOptions.WithTimeout(TimeSpan.FromMilliseconds(700)), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.ElementNotFound, error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    // ---------- Upload: content only ----------

    [Fact]
    public async Task Upload_sends_an_in_memory_payload_under_its_sanitized_name()
    {
        var content = "id,name\n1,Fictional Person\n"u8.ToArray();
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/upload"), CancellationToken.None);

        await session.UploadAsync(ElementSelector.TestId("file"), new UploadFile("../../etc/report.csv", "text/csv", content), null, CancellationToken.None);
        await session.SubmitAsync(ElementSelector.TestId("send"), CancellationToken.None);

        Assert.Equal("report.csv", await session.GetTextAsync(ElementSelector.TestId("name"), CancellationToken.None));
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), await session.GetTextAsync(ElementSelector.TestId("length"), CancellationToken.None));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), await session.GetTextAsync(ElementSelector.TestId("sha"), CancellationToken.None));
    }

    [Fact]
    public async Task An_upload_above_the_limit_is_rejected_before_the_browser_sees_it()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Uploads.MaxBytes = 1024);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/upload"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() =>
            session.UploadAsync(ElementSelector.TestId("file"), new UploadFile("big.bin", "application/octet-stream", new byte[2048]), null, CancellationToken.None));

        Assert.Equal(BrowserErrorCode.UploadRejected, error.Code);
        Assert.False(error.IsTransient);
        Assert.DoesNotContain(engine.Sink.Entries, e => e.EventType == "OperationStarted" && e.Message.StartsWith("Upload", StringComparison.Ordinal));
    }

    // ---------- Probes ----------

    [Fact]
    public async Task Count_is_immediate_and_zero_for_missing_elements()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/probes"), CancellationToken.None);
        var clock = Stopwatch.StartNew();

        Assert.Equal(0, await session.CountAsync(ElementSelector.TestId("missing"), null, CancellationToken.None));
        Assert.Equal(1, await session.CountAsync(ElementSelector.TestId("shown"), null, CancellationToken.None));
        Assert.Equal(2, await session.CountAsync(ElementSelector.TestId("twin"), null, CancellationToken.None));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "probes must not auto-wait");
    }

    [Fact]
    public async Task IsVisible_is_false_for_missing_or_hidden_true_for_shown_and_strict_for_duplicates()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/probes"), CancellationToken.None);

        Assert.False(await session.IsVisibleAsync(ElementSelector.TestId("missing"), null, CancellationToken.None));
        Assert.False(await session.IsVisibleAsync(ElementSelector.TestId("hidden"), null, CancellationToken.None));
        Assert.True(await session.IsVisibleAsync(ElementSelector.TestId("shown"), null, CancellationToken.None));
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.IsVisibleAsync(ElementSelector.TestId("twin"), null, CancellationToken.None));
        Assert.Equal(BrowserErrorCode.ElementNotUnique, error.Code);
    }

    [Fact]
    public async Task CurrentUrl_is_raw_for_conditions_but_only_ever_logged_redacted()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/query?token=raw-secret-123"), CancellationToken.None);

        var url = session.CurrentUrl;

        Assert.Equal("http://public.test/query?token=raw-secret-123", url);
        var events = Engine.Sink.Entries.Where(e => e.SessionId == session.Id).ToList();
        Assert.Contains(events, e => e.EventType == "ProbeCurrentUrl" && e.Properties["url"] == "http://public.test/query?token=***");
        Assert.DoesNotContain(events, e => e.Message.Contains("raw-secret-123", StringComparison.Ordinal) || e.Properties.Values.Any(v => v?.Contains("raw-secret-123", StringComparison.Ordinal) == true));
    }

    // ---------- QueryRows ----------

    [Fact]
    public async Task QueryRows_reads_row_attributes_and_child_text_in_document_order()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/rows"), CancellationToken.None);

        var snapshot = await session.QueryRowsAsync(
            ElementSelector.TestId("row"),
            [RowValueSpec.OfAttribute("data-key"), RowValueSpec.OfAttribute("data-id"), RowValueSpec.OfChildText(ElementSelector.TestId("email"))],
            maxRows: 5,
            null,
            CancellationToken.None);

        Assert.Equal(3, snapshot.Count);
        Assert.False(snapshot.Truncated);
        Assert.Equal(["op-1", "C-1", "ann@example.test"], snapshot.Values[0]);
        Assert.Equal(["op-2", "C-2", "joann@example.test"], snapshot.Values[1]);
        Assert.Equal([null, "C-3", null], snapshot.Values[2]);
    }

    [Fact]
    public async Task QueryRows_returns_only_the_count_when_more_rows_match_than_allowed()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/many-rows"), CancellationToken.None);

        var snapshot = await session.QueryRowsAsync(ElementSelector.TestId("item"), [RowValueSpec.OfAttribute("data-n")], maxRows: 50, null, CancellationToken.None);

        Assert.Equal(60, snapshot.Count);
        Assert.True(snapshot.Truncated);
        Assert.Empty(snapshot.Values);
    }

    [Fact]
    public async Task QueryRows_fails_strictly_when_a_row_child_is_not_unique()
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/rows"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() =>
            session.QueryRowsAsync(ElementSelector.TestId("bad-row"), [RowValueSpec.OfChildText(ElementSelector.TestId("email"))], 5, null, CancellationToken.None));

        Assert.Equal(BrowserErrorCode.ElementNotUnique, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task QueryRows_bounds_the_row_limit(int maxRows)
    {
        await using var session = await Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.QueryRowsAsync(ElementSelector.TestId("row"), [RowValueSpec.OfAttribute("data-key")], maxRows, null, CancellationToken.None));
    }

    // ---------- Sensitive registration ----------

    [Fact]
    public async Task A_registered_sensitive_value_is_redacted_from_later_messages_and_events()
    {
        const string Secret = "Extracted-Secret-4242";
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.ActionSeconds = 1);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);

        session.RegisterSensitive(Secret, null);
        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.GetTextAsync(ElementSelector.TestId(Secret), CancellationToken.None));

        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(engine.Sink.Entries, e => e.Message.Contains(Secret, StringComparison.Ordinal)
            || e.Properties.Values.Any(v => v?.Contains(Secret, StringComparison.Ordinal) == true));
    }

    // ---------- Private network only from trusted configuration (ADR-047) ----------

    [Fact]
    public async Task A_private_network_rule_not_in_the_trusted_configuration_is_refused()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:1"]);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => engine.CreateSessionAsync(fixture.DemoCrmPolicy));

        Assert.Equal(BrowserErrorCode.PolicyViolation, error.Code);
        Assert.Equal(0, engine.Host.LaunchCount);
    }

    [Fact]
    public async Task A_trusted_origin_without_the_request_flag_grants_no_private_access()
    {
        // The DemoCrm origin is trusted, but this request does not ask for private-network access: the proxy denies it.
        await using var session = await Engine.CreateSessionAsync(new EgressPolicy([AllowedHostRule.Parse(fixture.DemoCrm.BaseUrl)]));

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.NavigateAsync(new Uri(fixture.DemoCrm.BaseUrl + "/Login"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.NavigationBlocked, error.Code);
    }

    // ---------- Barrier wiring in the session (V9) ----------

    [Fact]
    public async Task Dispose_runs_the_revocation_barrier_before_closing_the_context_and_reports_EgressRevoked()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);
        var egress = ((BrowserSession)session).EgressForTesting;
        Assert.False(session.EgressRevoked);

        await session.DisposeAsync();

        Assert.True(session.EgressRevoked);
        Assert.True(egress.IsRevoked);
        Assert.Equal(0, egress.ActiveConnections);
        Assert.Null(engine.Proxy.Sessions.Authenticate(egress.Username, egress.Password));
        var order = engine.Sink.Entries.Where(e => e.SessionId == session.Id).Select(e => e.EventType).ToList();
        Assert.True(order.IndexOf("EgressRevocation") >= 0 && order.IndexOf("EgressRevocation") < order.IndexOf("ContextClosed"), string.Join(",", order));
        Assert.Contains(engine.Sink.Entries, e => e.EventType == "EgressRevocation" && e.Properties["result"] == "Completed");
    }
}
