using System.Diagnostics;
using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

/// <summary>The predictable temporary-failure scenarios that later phases use to prove retry and reconciliation.</summary>
public sealed class SimulatedFailureTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<int> CustomerCountAsync() => (await _app.GetStateAsync()).CustomerCount;

    [Fact]
    public async Task Fail_2_returns_503_twice_before_committing_then_succeeds_on_the_third_attempt()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "retry.me+fail-2@example.test", OperationId = Guid.NewGuid().ToString("D") };

        var first = await _app.RegisterAsync(form);
        var second = await _app.RegisterAsync(form);

        foreach (var response in new[] { first, second })
        {
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("temporary", DemoCrmApp.Attribute(html, "form-error", "data-error-kind"));
            Assert.Equal("Temporary service error. Please try again.", DemoCrmApp.Text(html, "form-error"));
            Assert.Equal("1", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(24, await CustomerCountAsync());

        var third = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);
        Assert.EndsWith("result=created", third.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(25, await CustomerCountAsync());
    }

    [Fact]
    public async Task Failed_attempts_keep_the_entered_data_so_the_form_can_simply_be_resubmitted()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { FirstName = "Keepme", Email = "keep.me+fail-1@example.test" };

        var html = await (await _app.RegisterAsync(form)).Content.ReadAsStringAsync();

        Assert.Equal("Keepme", DemoCrmApp.Attribute(html, "input-first-name", "value"));
        Assert.Equal("keep.me+fail-1@example.test", DemoCrmApp.Attribute(html, "input-email", "value"));
    }

    [Fact]
    public async Task Fail_1_counts_attempts_per_email_so_other_customers_are_unaffected()
    {
        await _app.LoginSuccessfullyAsync();

        var failing = await _app.RegisterAsync(new RegistrationForm { Email = "a+fail-1@example.test" });
        var other = await _app.RegisterAsync(new RegistrationForm());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failing.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Fact]
    public async Task Commit_fail_stores_the_customer_but_reports_a_temporary_error()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "lost.response+commit-fail@example.test", OperationId = Guid.NewGuid().ToString("D") };

        var response = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(25, await CustomerCountAsync());
        var search = await _app.GetHtmlAsync($"/Customers/Search?Field=Email&Term={Uri.EscapeDataString(form.Email)}");
        Assert.Equal(1, DemoCrmApp.Count(search, "search-result-row"));
    }

    [Fact]
    public async Task Commit_fail_retried_with_the_same_operation_reference_is_already_processed_and_creates_no_duplicate()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "lost.response+commit-fail@example.test", OperationId = Guid.NewGuid().ToString("D") };

        var first = await _app.RegisterAsync(form);
        var retry = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal("/Customers/Confirmation/25?result=replayed", retry.Headers.Location!.OriginalString);
        Assert.Equal(25, await CustomerCountAsync());
    }

    [Fact]
    public async Task Commit_fail_retried_without_an_operation_reference_is_rejected_as_a_duplicate()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "lost.response+commit-fail@example.test" };

        var first = await _app.RegisterAsync(form);
        var retry = await _app.RegisterAsync(form);
        var html = await retry.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("C-000025", DemoCrmApp.Attribute(html, "form-error", "data-existing-customer-id"));
        Assert.Equal(25, await CustomerCountAsync());
    }

    [Fact]
    public async Task Commit_fail_retried_with_a_new_operation_reference_is_also_a_duplicate()
    {
        await _app.LoginSuccessfullyAsync();
        var email = "lost.response+commit-fail@example.test";

        await _app.RegisterAsync(new RegistrationForm { Email = email, OperationId = Guid.NewGuid().ToString("D") });
        var retry = await _app.RegisterAsync(new RegistrationForm { Email = email, OperationId = Guid.NewGuid().ToString("D") });

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(25, await CustomerCountAsync());
    }

    [Fact]
    public async Task Commit_fail_only_loses_the_first_response()
    {
        await _app.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "once+commit-fail@example.test", OperationId = Guid.NewGuid().ToString("D") };

        await _app.RegisterAsync(form);
        var second = await _app.RegisterAsync(form);
        var third = await _app.RegisterAsync(form);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);
    }

    [Fact]
    public async Task Slow_scenario_succeeds_but_only_after_the_configured_delay()
    {
        using var slowApp = new DemoCrmApp(slowDelayMs: 300);
        await slowApp.LoginSuccessfullyAsync();
        var form = new RegistrationForm { Email = "sleepy+slow@example.test" };

        var stopwatch = Stopwatch.StartNew();
        var response = await slowApp.RegisterAsync(form);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(stopwatch.ElapsedMilliseconds >= 250, $"Expected a delay of about 300 ms but took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Simulation_can_be_switched_off()
    {
        using var plainApp = new DemoCrmApp(simulationEnabled: false);
        await plainApp.LoginSuccessfullyAsync();

        var response = await plainApp.RegisterAsync(new RegistrationForm { Email = "calm+fail-3@example.test" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Failures_do_not_apply_to_invalid_submissions_which_are_validated_first()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.RegisterAsync(new RegistrationForm { Email = "x+fail-1@example.test", FirstName = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // The validation failure must not have consumed the scenario's first failure.
        var valid = await _app.RegisterAsync(new RegistrationForm { Email = "x+fail-1@example.test" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, valid.StatusCode);
    }
}
