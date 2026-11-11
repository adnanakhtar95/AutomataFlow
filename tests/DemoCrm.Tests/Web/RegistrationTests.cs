using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

public sealed class RegistrationTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Register_page_shows_every_form_field_and_a_prefilled_operation_reference()
    {
        await _app.LoginSuccessfullyAsync();

        var html = await _app.GetHtmlAsync("/Customers/Register");

        foreach (var id in new[] { "input-first-name", "input-last-name", "input-email", "input-phone", "input-company", "input-country", "input-customer-type", "input-operation-id", "input-accept-terms", "register-submit" })
        {
            Assert.True(DemoCrmApp.Has(html, id), $"Missing {id}");
        }

        Assert.True(Guid.TryParse(DemoCrmApp.Attribute(html, "input-operation-id", "value"), out _));
    }

    [Fact]
    public async Task Successful_registration_redirects_to_a_confirmation_with_the_new_customer_id()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { FirstName = "Grace", LastName = "Hopper", OperationId = Guid.NewGuid().ToString("D") };

        var response = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Customers/Confirmation/25?result=created", response.Headers.Location!.OriginalString);
        var html = await _app.GetHtmlAsync(response.Headers.Location.OriginalString);
        Assert.Equal("Customer C-000025 registered successfully.", DemoCrmApp.Text(html, "confirmation-message"));
        Assert.Equal("created", DemoCrmApp.Attribute(html, "confirmation-message", "data-result"));
        Assert.Equal("C-000025", DemoCrmApp.Text(html, "confirmation-customer-id"));
        Assert.Equal(form.Email, DemoCrmApp.Text(html, "confirmation-email"));
    }

    [Fact]
    public async Task Registered_customer_appears_in_details_listing_and_search_with_all_submitted_data()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { FirstName = "Grace", LastName = "Hopper", Company = "Cobol Works", Country = "United States", CustomerType = "Premium" };
        var created = await _app.RegisterAsync(form);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var details = await _app.GetHtmlAsync("/Customers/Details/25");
        Assert.Equal("C-000025", DemoCrmApp.Text(details, "detail-id"));
        Assert.Equal("Grace", DemoCrmApp.Text(details, "detail-first-name"));
        Assert.Equal("Hopper", DemoCrmApp.Text(details, "detail-last-name"));
        Assert.Equal(form.Email, DemoCrmApp.Text(details, "detail-email"));
        Assert.Equal(form.Phone, DemoCrmApp.Text(details, "detail-phone"));
        Assert.Equal("Cobol Works", DemoCrmApp.Text(details, "detail-company"));
        Assert.Equal("United States", DemoCrmApp.Text(details, "detail-country"));
        Assert.Equal("Premium", DemoCrmApp.Text(details, "detail-type"));
        Assert.Equal("Registered", DemoCrmApp.Text(details, "detail-source"));

        var list = await _app.GetHtmlAsync("/Customers?P=3");
        Assert.Contains(form.Email, list, StringComparison.Ordinal);

        var search = await _app.GetHtmlAsync($"/Customers/Search?Field=Email&Term={Uri.EscapeDataString(form.Email)}");
        Assert.Equal(1, DemoCrmApp.Count(search, "search-result-row"));
        Assert.Equal("C-000025", DemoCrmApp.Attribute(search, "search-result-row", "data-customer-id"));
    }

    [Fact]
    public async Task Optional_company_may_be_left_blank()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Company = null });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var details = await _app.GetHtmlAsync("/Customers/Details/25");
        Assert.Equal("-", DemoCrmApp.Text(details, "detail-company"));
    }

    [Fact]
    public async Task Operation_reference_is_stored_and_shown_on_the_details_page()
    {
        await _app.LoginSuccessfullyAsync();
        var operation = Guid.NewGuid().ToString("D");

        await _app.RegisterAsync(new RegistrationForm { OperationId = operation.ToUpperInvariant() });

        var details = await _app.GetHtmlAsync("/Customers/Details/25");
        Assert.Equal(operation, DemoCrmApp.Text(details, "detail-operation-id"));
    }

    [Fact]
    public async Task Anonymous_registration_attempts_are_rejected_and_create_nothing()
    {
        var response = await _app.PostFormWithoutTokenAsync("/Customers/Register", new RegistrationForm().ToFields());

        // Rejected either by authentication (redirect to login) or by antiforgery validation (400); never accepted.
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest, $"Unexpected status {response.StatusCode}");
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }
}
