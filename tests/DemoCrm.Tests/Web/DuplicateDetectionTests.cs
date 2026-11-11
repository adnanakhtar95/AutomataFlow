using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

public sealed class DuplicateDetectionTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Registering_a_seeded_email_returns_409_naming_the_existing_customer()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Email = "alice.morgan@example.test" });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate", DemoCrmApp.Attribute(html, "form-error", "data-error-kind"));
        Assert.Equal("C-000001", DemoCrmApp.Attribute(html, "form-error", "data-existing-customer-id"));
        Assert.Equal("Duplicate customer: this email is already registered as C-000001.", DemoCrmApp.Text(html, "form-error"));
        Assert.Equal("A customer with this email already exists (C-000001).", DemoCrmApp.Text(html, "error-email"));
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Duplicate_email_is_detected_regardless_of_letter_case()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Email = "ALICE.MORGAN@Example.Test" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_phone_is_detected_even_when_formatted_differently()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Phone = "(1) 555-0100" });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate", DemoCrmApp.Attribute(html, "form-error", "data-error-kind"));
        Assert.Equal("C-000001", DemoCrmApp.Attribute(html, "form-error", "data-existing-customer-id"));
        Assert.Equal("A customer with this phone number already exists (C-000001).", DemoCrmApp.Text(html, "error-phone"));
    }

    [Fact]
    public async Task Registering_the_same_new_customer_twice_creates_only_one()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm();

        var first = await _app.RegisterAsync(form);
        var second = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task A_resubmitted_form_with_the_same_operation_reference_is_reported_as_already_processed()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { OperationId = Guid.NewGuid().ToString("D") };

        var first = await _app.RegisterAsync(form);
        var second = await _app.RegisterAsync(form);

        Assert.Equal("/Customers/Confirmation/25?result=created", first.Headers.Location!.OriginalString);
        Assert.Equal("/Customers/Confirmation/25?result=replayed", second.Headers.Location!.OriginalString);
        var html = await _app.GetHtmlAsync(second.Headers.Location.OriginalString);
        Assert.Equal("replayed", DemoCrmApp.Attribute(html, "confirmation-message", "data-result"));
        Assert.StartsWith("Registration already processed.", DemoCrmApp.Text(html, "confirmation-message"), StringComparison.Ordinal);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Reusing_an_operation_reference_for_different_data_is_an_operation_conflict()
    {
        await _app.LoginSuccessfullyAsync();
        var operation = Guid.NewGuid().ToString("D");
        await _app.RegisterAsync(new RegistrationForm { OperationId = operation });

        var response = await _app.RegisterAsync(new RegistrationForm { OperationId = operation });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("operation-conflict", DemoCrmApp.Attribute(html, "form-error", "data-error-kind"));
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Idempotency_key_header_works_like_the_form_field()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm();
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("D") };

        var first = await _app.RegisterAsync(form, headers);
        var second = await _app.RegisterAsync(form, headers);

        Assert.EndsWith("result=created", first.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.EndsWith("result=replayed", second.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Invalid_idempotency_key_header_is_rejected()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm(), new Dictionary<string, string> { ["Idempotency-Key"] = "nope" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }
}
