using System.Net;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

public sealed class AuthenticationTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("/")]
    [InlineData("/Customers")]
    [InlineData("/Customers/Search")]
    [InlineData("/Customers/Register")]
    [InlineData("/Customers/Details/1")]
    [InlineData("/Admin")]
    public async Task Anonymous_requests_are_redirected_to_the_login_page(string path)
    {
        var response = await _app.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", DemoCrmApp.RedirectPath(response));
    }

    [Fact]
    public async Task Login_page_is_public_and_shows_the_form()
    {
        var html = await _app.GetHtmlAsync("/Login");

        Assert.True(DemoCrmApp.Has(html, "login-form"));
        Assert.True(DemoCrmApp.Has(html, "login-email"));
        Assert.True(DemoCrmApp.Has(html, "login-password"));
    }

    [Fact]
    public async Task Valid_credentials_sign_in_and_unlock_the_portal()
    {
        var login = await _app.LoginAsync();

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);
        var html = await _app.GetHtmlAsync("/");
        Assert.Equal("Demo Agent", DemoCrmApp.Text(html, "current-user"));
        Assert.Equal("24", DemoCrmApp.Text(html, "customer-count"));
    }

    [Theory]
    [InlineData("agent@democrm.test", "wrong-password")]
    [InlineData("nobody@democrm.test", "DemoPass-2026!")]
    [InlineData("", "")]
    public async Task Invalid_credentials_are_rejected_with_a_generic_message_and_no_session(string email, string password)
    {
        var response = await _app.LoginAsync(email, password);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid email or password.", DemoCrmApp.Text(html, "login-error"));
        Assert.DoesNotContain(password.Length > 0 ? password : "\u0001", html, StringComparison.Ordinal);
        var protectedResponse = await _app.GetAsync("/Customers");
        Assert.Equal(HttpStatusCode.Redirect, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task Email_is_matched_case_insensitively()
    {
        var response = await _app.LoginAsync("AGENT@DemoCrm.Test");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Local_return_urls_are_followed()
    {
        var response = await _app.LoginAsync(returnUrl: "/Customers/Search");

        Assert.Equal("/Customers/Search", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example")]
    public async Task Off_site_return_urls_are_ignored(string returnUrl)
    {
        var response = await _app.LoginAsync(returnUrl: returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await _app.LoginSuccessfullyAsync();

        var logout = await _app.PostFormAsync("/Logout", new Dictionary<string, string>(), tokenFromPath: "/Customers");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var after = await _app.GetAsync("/Customers");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("/Login", DemoCrmApp.RedirectPath(after));
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_are_rejected()
    {
        var response = await _app.PostFormWithoutTokenAsync(
            "/Login",
            new Dictionary<string, string> { ["Email"] = DemoCrmApp.DemoEmail, ["Password"] = DemoCrmApp.DemoPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registration_posts_without_an_antiforgery_token_are_rejected_and_create_nothing()
    {
        await _app.LoginSuccessfullyAsync();

        var response = await _app.PostFormWithoutTokenAsync("/Customers/Register", new RegistrationForm().ToFields());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(24, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Responses_carry_basic_security_headers()
    {
        var response = await _app.GetAsync("/Login");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}
