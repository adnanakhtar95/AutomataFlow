using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

public sealed class BrowsingAndResetTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    // ---- listing, search, details ----

    [Fact]
    public async Task Customer_list_shows_the_first_page_of_seeded_customers_with_paging_links()
    {
        await _app.LoginSuccessfullyAsync();

        var html = await _app.GetHtmlAsync("/Customers");

        Assert.Equal(10, DemoCrmApp.Count(html, "customer-row"));
        Assert.Equal("Showing 10 of 24 customers (page 1 of 3)", DemoCrmApp.Text(html, "result-summary"));
        Assert.True(DemoCrmApp.Has(html, "pager-next"));
        Assert.False(DemoCrmApp.Has(html, "pager-prev"));
    }

    [Fact]
    public async Task Customer_list_filter_narrows_results_and_shows_an_empty_state()
    {
        await _app.LoginSuccessfullyAsync();

        var one = await _app.GetHtmlAsync("/Customers?Q=morgan");
        var none = await _app.GetHtmlAsync("/Customers?Q=zzz-no-such");

        Assert.Equal(1, DemoCrmApp.Count(one, "customer-row"));
        Assert.True(DemoCrmApp.Has(none, "no-customers"));
        Assert.Equal(0, DemoCrmApp.Count(none, "customer-row"));
    }

    [Theory]
    [InlineData("Email", "alice.morgan@example.test", "C-000001")]
    [InlineData("Name", "Brian Fletcher", "C-000002")]
    [InlineData("Phone", "555 0102", "C-000003")]
    [InlineData("Id", "C-000004", "C-000004")]
    [InlineData("Any", "Okafor", "C-000004")]
    public async Task Search_finds_a_seeded_customer_by_each_field(string field, string term, string expectedId)
    {
        await _app.LoginSuccessfullyAsync();

        var html = await _app.GetHtmlAsync($"/Customers/Search?Field={field}&Term={Uri.EscapeDataString(term)}");

        Assert.Equal(1, DemoCrmApp.Count(html, "search-result-row"));
        Assert.Equal(expectedId, DemoCrmApp.Attribute(html, "search-result-row", "data-customer-id"));
    }

    [Fact]
    public async Task Search_without_matches_says_so_and_search_without_a_term_shows_nothing()
    {
        await _app.LoginSuccessfullyAsync();

        var none = await _app.GetHtmlAsync("/Customers/Search?Field=Email&Term=nobody%40example.test");
        var empty = await _app.GetHtmlAsync("/Customers/Search");

        Assert.Equal("No customers found.", DemoCrmApp.Text(none, "search-no-results"));
        Assert.False(DemoCrmApp.Has(empty, "search-summary"));
    }

    [Fact]
    public async Task Details_page_shows_a_seeded_customer()
    {
        await _app.LoginSuccessfullyAsync();

        var html = await _app.GetHtmlAsync("/Customers/Details/1");

        Assert.Equal("alice.morgan@example.test", DemoCrmApp.Text(html, "detail-email"));
        Assert.Equal("Seed data", DemoCrmApp.Text(html, "detail-source"));
    }

    [Theory]
    [InlineData("/Customers/Details/9999")]
    [InlineData("/Customers/Confirmation/9999")]
    [InlineData("/no/such/page")]
    public async Task Unknown_customers_and_pages_show_a_friendly_not_found_page(string path)
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Not found", DemoCrmApp.Text(html, "page-title"));
    }

    // ---- reset ----

    [Fact]
    public async Task Admin_reset_restores_the_seed_and_clears_simulation_counters()
    {
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm { Email = "gone+fail-1@example.test" });
        await _app.RegisterAsync(new RegistrationForm());
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);

        var reset = await _app.PostFormAsync("/Admin?handler=Reset", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var admin = await _app.GetHtmlAsync("/Admin");
        Assert.Equal("Demo data has been reset to the original seed.", DemoCrmApp.Text(admin, "reset-message"));
        var state = await _app.GetStateAsync();
        Assert.Equal(24, state.CustomerCount);
        Assert.Equal(0, state.SimulatedAddresses);

        // The scenario counter restarted: the first attempt fails again.
        var again = await _app.RegisterAsync(new RegistrationForm { Email = "gone+fail-1@example.test" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, again.StatusCode);
    }

    [Fact]
    public async Task Admin_page_lists_the_failure_scenarios()
    {
        await _app.LoginSuccessfullyAsync();

        var html = await _app.GetHtmlAsync("/Admin");

        Assert.Equal("24", DemoCrmApp.Text(html, "admin-customer-count"));
        Assert.Equal("Enabled", DemoCrmApp.Text(html, "admin-simulation-state"));
        Assert.True(DemoCrmApp.Has(html, "scenario-table"));
    }

    [Fact]
    public async Task Control_api_reset_requires_the_correct_token()
    {
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.ControlResetAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.ControlResetAsync("wrong-token")).StatusCode);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);

        var ok = await _app.ControlResetAsync(DemoCrmApp.ControlToken);

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Control_api_is_disabled_when_no_token_is_configured()
    {
        using var locked = new DemoCrmApp(controlToken: string.Empty);

        var reset = await locked.ControlResetAsync("anything");
        var state = await locked.GetAsync("/demo/state");

        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
    }

    [Fact]
    public async Task Control_api_state_does_not_expose_customer_data()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo/state");
        request.Headers.Add("X-Demo-Control-Token", DemoCrmApp.ControlToken);

        var body = await (await _app.Client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("example.test", body, StringComparison.Ordinal);
        Assert.Contains("customerCount", body, StringComparison.Ordinal);
    }
}
