using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

public sealed class ValidationTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    public static TheoryData<string, string, string> InvalidSubmissions => new()
    {
        { "FirstName", "", "First name is required." },
        { "LastName", "12345", "Last name contains invalid characters." },
        { "Email", "", "Email is required." },
        { "Email", "not-an-email", "Enter a valid email address." },
        { "Phone", "abc", "Enter a valid phone number (7-15 digits)." },
        { "Phone", "12345", "Enter a valid phone number (7-15 digits)." },
        { "Country", "Atlantis", "Select a country from the list." },
        { "CustomerType", "", "Customer type is required." },
        { "OperationId", "not-a-guid", "Operation reference must be a valid GUID." },
    };

    private static RegistrationForm With(string field, string value)
    {
        var form = new RegistrationForm();
        return field switch
        {
            "FirstName" => form with { FirstName = value },
            "LastName" => form with { LastName = value },
            "Email" => form with { Email = value },
            "Phone" => form with { Phone = value },
            "Country" => form with { Country = value },
            "CustomerType" => form with { CustomerType = value },
            "OperationId" => form with { OperationId = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
    }

    private static string ErrorTestId(string field) => field switch
    {
        "FirstName" => "error-first-name",
        "LastName" => "error-last-name",
        "Email" => "error-email",
        "Phone" => "error-phone",
        "Country" => "error-country",
        "CustomerType" => "error-customer-type",
        "OperationId" => "error-operation-id",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    [Theory]
    [MemberData(nameof(InvalidSubmissions))]
    public async Task Invalid_field_returns_422_with_a_clear_message_and_stores_nothing(string field, string value, string expectedMessage)
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(With(field, value));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation", DemoCrmApp.Attribute(html, "form-error", "data-error-kind"));
        Assert.Equal("Please correct the highlighted fields.", DemoCrmApp.Text(html, "form-error"));
        Assert.Equal(expectedMessage, DemoCrmApp.Text(html, ErrorTestId(field)));
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Missing_terms_confirmation_is_rejected()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { AcceptTerms = false });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("You must confirm that the data is accurate.", DemoCrmApp.Text(html, "error-accept-terms"));
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Empty_submission_reports_every_required_field_at_once()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.PostFormAsync("/Customers/Register", new Dictionary<string, string>());
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        foreach (var id in new[] { "error-first-name", "error-last-name", "error-email", "error-phone", "error-country", "error-customer-type", "error-accept-terms" })
        {
            Assert.NotEqual(string.Empty, DemoCrmApp.Text(html, id));
        }

        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Form_keeps_the_values_the_user_typed_after_a_validation_error()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { FirstName = "Keep", Email = "bad", Company = "Kept Co", Country = "Germany", CustomerType = "Premium" };

        var html = await (await _app.RegisterAsync(form)).Content.ReadAsStringAsync();

        Assert.Equal("Keep", DemoCrmApp.Attribute(html, "input-first-name", "value"));
        Assert.Equal("bad", DemoCrmApp.Attribute(html, "input-email", "value"));
        Assert.Equal("Kept Co", DemoCrmApp.Attribute(html, "input-company", "value"));
        Assert.Contains("<option value=\"Germany\" selected=\"selected\">", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Premium\" selected=\"selected\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submitted_markup_is_html_encoded_when_the_form_is_shown_again()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { FirstName = "<script>alert(1)</script>", Company = "\"><img src=x onerror=alert(2)>" };

        var response = await _app.RegisterAsync(form);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("<script>alert(1)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_company_is_rejected()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Company = new string('c', 81) });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Company must be at most 80 characters.", DemoCrmApp.Text(html, "error-company"));
    }
}
