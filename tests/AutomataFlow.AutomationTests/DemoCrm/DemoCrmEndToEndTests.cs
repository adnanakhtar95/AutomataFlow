using System.Net;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.DemoCrm;

/// <summary>
/// Real Chromium driving the separately running DemoCrm through the engine's operations only (as Phase 4
/// workflows will), through the egress proxy, with the Chromium sandbox on.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class DemoCrmEndToEndTests(BrowserFixture fixture) : IAsyncLifetime
{
    private static readonly ElementSelector FormError = ElementSelector.TestId("form-error");

    private EngineHarness Engine => fixture.Engine;

    private string Url(string path) => fixture.DemoCrm.BaseUrl + path;

    public Task InitializeAsync() => fixture.DemoCrm.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<IBrowserSession> SignedInSessionAsync()
    {
        var session = await Engine.CreateSessionAsync(fixture.DemoCrmPolicy);
        await session.NavigateAsync(new Uri(Url("/Login")), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("login-email"), InputValue.Plain("agent@democrm.test"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("login-password"), InputValue.Sensitive("DemoPass-2026!"), CancellationToken.None);
        await session.SubmitAsync(ElementSelector.TestId("login-submit"), CancellationToken.None);
        return session;
    }

    private static async Task FillRegistrationAsync(IBrowserSession session, string email, string phone, string? operationId = null)
    {
        await session.FillAsync(ElementSelector.TestId("input-first-name"), InputValue.Plain("Grace"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("input-last-name"), InputValue.Plain("Hopper"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("input-email"), InputValue.Plain(email), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("input-phone"), InputValue.Plain(phone), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("input-company"), InputValue.Plain("Fictional Compilers"), CancellationToken.None);
        await session.SelectAsync(ElementSelector.TestId("input-country"), SelectChoice.ByLabel("Canada"), CancellationToken.None);
        await session.SelectAsync(ElementSelector.TestId("input-customer-type"), SelectChoice.ByValue("Premium"), CancellationToken.None);
        if (operationId is not null)
        {
            await session.FillAsync(ElementSelector.TestId("input-operation-id"), InputValue.Plain(operationId), CancellationToken.None);
        }

        await session.ClickAsync(ElementSelector.TestId("input-accept-terms"), CancellationToken.None);
    }

    [Fact]
    public async Task Login_and_register_a_customer_capturing_the_confirmation_id()
    {
        await using var session = await SignedInSessionAsync();
        Assert.Equal("24", await session.GetTextAsync(ElementSelector.TestId("customer-count"), CancellationToken.None));

        await session.NavigateAsync(new Uri(Url("/Customers/Register")), CancellationToken.None);
        await FillRegistrationAsync(session, "grace.hopper@example.test", "+1 555 7001");
        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);

        await session.WaitForAsync(WaitCondition.Url("**/Customers/Confirmation/**"), CancellationToken.None);
        Assert.Equal("C-000025", await session.GetTextAsync(ElementSelector.TestId("confirmation-customer-id"), CancellationToken.None));
        Assert.Equal("created", await session.GetAttributeAsync(ElementSelector.TestId("confirmation-message"), "data-result", CancellationToken.None));
        Assert.Equal(25, await fixture.DemoCrm.CustomerCountAsync());

        // Only DemoCrm was ever contacted through the proxy (no background or unexpected destinations).
        Assert.All(Engine.Connector.Attempts.Where(e => e.Port == fixture.DemoCrm.Port), e => Assert.Equal(IPAddress.Loopback, e.Address));
    }

    [Fact]
    public async Task Server_side_validation_errors_are_visible_and_nothing_is_stored()
    {
        await using var session = await SignedInSessionAsync();
        await session.NavigateAsync(new Uri(Url("/Customers/Register")), CancellationToken.None);
        await FillRegistrationAsync(session, "not-an-email", "12");

        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);

        Assert.Equal("validation", await session.GetAttributeAsync(FormError, "data-error-kind", CancellationToken.None));
        Assert.Equal("Enter a valid email address.", await session.GetTextAsync(ElementSelector.TestId("error-email"), CancellationToken.None));
        Assert.Equal("Enter a valid phone number (7-15 digits).", await session.GetTextAsync(ElementSelector.TestId("error-phone"), CancellationToken.None));
        Assert.Equal(24, await fixture.DemoCrm.CustomerCountAsync());
    }

    [Fact]
    public async Task Duplicate_customers_are_reported_with_the_existing_id()
    {
        await using var session = await SignedInSessionAsync();
        await session.NavigateAsync(new Uri(Url("/Customers/Register")), CancellationToken.None);
        await FillRegistrationAsync(session, "alice.morgan@example.test", "+1 555 7002");

        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);

        Assert.Equal("duplicate", await session.GetAttributeAsync(FormError, "data-error-kind", CancellationToken.None));
        Assert.Equal("C-000001", await session.GetAttributeAsync(FormError, "data-existing-customer-id", CancellationToken.None));
        Assert.Equal(24, await fixture.DemoCrm.CustomerCountAsync());
    }

    [Fact]
    public async Task Temporary_failures_are_visible_and_a_resubmission_succeeds()
    {
        await using var session = await SignedInSessionAsync();
        await session.NavigateAsync(new Uri(Url("/Customers/Register")), CancellationToken.None);
        await FillRegistrationAsync(session, "retry.me+fail-2@example.test", "+1 555 7003", Guid.NewGuid().ToString("D"));

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);
            Assert.Equal("temporary", await session.GetAttributeAsync(FormError, "data-error-kind", CancellationToken.None));
            Assert.Equal(24, await fixture.DemoCrm.CustomerCountAsync());
        }

        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);
        await session.WaitForAsync(WaitCondition.Url("**/Customers/Confirmation/**"), CancellationToken.None);
        Assert.Equal(25, await fixture.DemoCrm.CustomerCountAsync());
    }

    [Fact]
    public async Task Lost_response_retried_with_the_same_operation_id_creates_exactly_one_customer()
    {
        await using var session = await SignedInSessionAsync();
        await session.NavigateAsync(new Uri(Url("/Customers/Register")), CancellationToken.None);
        await FillRegistrationAsync(session, "lost.response+commit-fail@example.test", "+1 555 7004", Guid.NewGuid().ToString("D"));

        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);
        Assert.Equal("temporary", await session.GetAttributeAsync(FormError, "data-error-kind", CancellationToken.None));
        Assert.Equal(25, await fixture.DemoCrm.CustomerCountAsync()); // committed despite the error

        await session.SubmitAsync(ElementSelector.TestId("register-submit"), CancellationToken.None);
        await session.WaitForAsync(WaitCondition.Url("**/Customers/Confirmation/**"), CancellationToken.None);

        Assert.Equal("replayed", await session.GetAttributeAsync(ElementSelector.TestId("confirmation-message"), "data-result", CancellationToken.None));
        Assert.Equal(25, await fixture.DemoCrm.CustomerCountAsync());
    }

    [Fact]
    public async Task Search_open_details_and_sign_out()
    {
        await using var session = await SignedInSessionAsync();

        await session.NavigateAsync(new Uri(Url("/Customers/Search?Field=Email&Term=alice.morgan%40example.test")), CancellationToken.None);
        Assert.Equal("1 customer(s) found.", await session.GetTextAsync(ElementSelector.TestId("search-summary"), CancellationToken.None));

        await session.SubmitAsync(ElementSelector.TestId("customer-link"), CancellationToken.None);
        Assert.Equal("alice.morgan@example.test", await session.GetTextAsync(ElementSelector.TestId("detail-email"), CancellationToken.None));
        Assert.Equal("C-000001", await session.GetTextAsync(ElementSelector.TestId("detail-id"), CancellationToken.None));

        await session.SubmitAsync(ElementSelector.TestId("logout-button"), CancellationToken.None);
        await session.WaitForAsync(WaitCondition.Url("**/Login"), CancellationToken.None);
    }

    [Fact]
    public async Task Each_record_attempt_gets_a_fresh_signed_out_session()
    {
        await using (await SignedInSessionAsync())
        {
        }

        await using var fresh = await Engine.CreateSessionAsync(fixture.DemoCrmPolicy);
        await fresh.NavigateAsync(new Uri(Url("/Customers")), CancellationToken.None);

        await fresh.WaitForAsync(WaitCondition.Url("**/Login?ReturnUrl=*"), CancellationToken.None);
    }
}
