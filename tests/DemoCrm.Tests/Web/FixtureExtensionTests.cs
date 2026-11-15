using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

/// <summary>
/// The Phase 4 test-fixture extensions (docs/PHASE4_PLAN.md §12.3, ADR-054): exact operation-id lookup, the registration
/// POST counter and the late-commit scenario. DemoCrm stays an independent fixture (ADR-027).
/// </summary>
public sealed class FixtureExtensionTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<string> RegisterWithOperationAsync(Guid operation, string email)
    {
        var response = await _app.RegisterAsync(new RegistrationForm { Email = email, OperationId = operation.ToString("D") });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private Task<string> SearchAsync(string field, string term) => _app.GetHtmlAsync($"/Customers/Search?Field={field}&Term={Uri.EscapeDataString(term)}");

    // ----- Exact operation-id lookup -----

    [Fact]
    public async Task OperationId_search_finds_exactly_the_customer_and_exposes_the_row_attribute()
    {
        await _app.LoginSuccessfullyAsync();
        var operation = Guid.NewGuid();
        await RegisterWithOperationAsync(operation, "op.lookup@example.test");

        var html = await SearchAsync("OperationId", operation.ToString("D"));

        Assert.Equal(1, DemoCrmApp.Count(html, "search-result-row"));
        Assert.Equal(operation.ToString("D"), DemoCrmApp.Attribute(html, "search-result-row", "data-operation-id"));
    }

    [Fact]
    public async Task OperationId_search_normalizes_case_and_braces_but_never_matches_a_prefix_or_an_invalid_guid()
    {
        await _app.LoginSuccessfullyAsync();
        var operation = Guid.NewGuid();
        await RegisterWithOperationAsync(operation, "op.variants@example.test");

        Assert.Equal(1, DemoCrmApp.Count(await SearchAsync("OperationId", operation.ToString("D").ToUpperInvariant()), "search-result-row"));
        Assert.Equal(1, DemoCrmApp.Count(await SearchAsync("OperationId", operation.ToString("B")), "search-result-row"));
        Assert.True(DemoCrmApp.Has(await SearchAsync("OperationId", operation.ToString("D")[..8]), "search-no-results"));
        Assert.True(DemoCrmApp.Has(await SearchAsync("OperationId", "not-a-guid"), "search-no-results"));
        Assert.True(DemoCrmApp.Has(await SearchAsync("OperationId", Guid.NewGuid().ToString("D")), "search-no-results"));
    }

    [Fact]
    public async Task Existing_search_fields_are_unchanged_and_rows_without_an_operation_have_an_empty_attribute()
    {
        await _app.LoginSuccessfullyAsync();

        var html = await SearchAsync("Email", "@example");

        Assert.True(DemoCrmApp.Count(html, "search-result-row") > 1);
        Assert.Equal(string.Empty, DemoCrmApp.Attribute(html, "search-result-row", "data-operation-id"));
    }

    // ----- Registration POST counter -----

    [Fact]
    public async Task Every_registration_post_counts_once_whatever_its_outcome_and_reset_zeroes_it()
    {
        await _app.LoginSuccessfullyAsync();
        Assert.Equal(0, (await _app.GetStateAsync()).RegistrationSubmissions);

        await _app.RegisterAsync(new RegistrationForm());                                  // created
        await _app.RegisterAsync(new RegistrationForm { Email = "not-an-email" });         // validation
        await _app.RegisterAsync(new RegistrationForm { Email = "count.me+fail-1@example.test" }); // temporary
        var seededEmail = new RegistrationForm { Email = "dup.count@example.test" };
        await _app.RegisterAsync(seededEmail);
        await _app.RegisterAsync(seededEmail with { Phone = "+1 555 9999" });             // duplicate

        Assert.Equal(5, (await _app.GetStateAsync()).RegistrationSubmissions);

        Assert.Equal(HttpStatusCode.NoContent, (await _app.ControlResetAsync(DemoCrmApp.ControlToken)).StatusCode);
        Assert.Equal(0, (await _app.GetStateAsync()).RegistrationSubmissions);
    }

    [Fact]
    public async Task The_state_endpoint_stays_protected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.ControlResetAsync("wrong")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo/state");
        request.Headers.Add(DemoCrm.Demo.DemoControlEndpoints.TokenHeader, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client.SendAsync(request)).StatusCode);
    }

    // ----- Late commit -----

    [Fact]
    public async Task Late_commit_stores_the_customer_even_after_the_client_gave_up()
    {
        await _app.LoginSuccessfullyAsync();
        var before = (await _app.GetStateAsync()).CustomerCount;
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var token = await AntiforgeryTokenAsync();
        var fields = new RegistrationForm { Email = "late.one+late-commit@example.test" }.ToFields();
        fields["__RequestVerificationToken"] = token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _app.Client.PostAsync("/Customers/Register", new FormUrlEncodedContent(fields), giveUp.Token));
        await Task.Delay(1_000); // the server-side delay (400 ms in tests) elapses after the client aborted

        var state = await _app.GetStateAsync();
        Assert.Equal(before + 1, state.CustomerCount);
        Assert.Equal(1, state.RegistrationSubmissions);
    }

    [Fact]
    public async Task Late_commit_never_commits_an_invalid_registration()
    {
        await _app.LoginSuccessfullyAsync();
        var before = (await _app.GetStateAsync()).CustomerCount;

        var response = await _app.RegisterAsync(new RegistrationForm { Email = "bad+late-commit@example.test", Phone = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, (await _app.GetStateAsync()).CustomerCount);
    }

    private async Task<string> AntiforgeryTokenAsync()
    {
        var html = await _app.GetHtmlAsync("/Customers/Register");
        var match = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
