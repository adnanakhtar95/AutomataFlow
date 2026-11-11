using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DemoCrm.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DemoCrm.Tests.Support;

/// <summary>Values for the registration form; defaults are valid and unique per instance.</summary>
internal sealed record RegistrationForm
{
    private static int _counter;

    public string FirstName { get; init; } = "Testa";

    public string LastName { get; init; } = "Person";

    public string Email { get; init; } = $"testa.person{Interlocked.Increment(ref _counter)}@example.test";

    public string Phone { get; init; } = $"+1 555 {2000 + Interlocked.Increment(ref _counter)}";

    public string? Company { get; init; } = "Test Company";

    public string Country { get; init; } = "Canada";

    public string CustomerType { get; init; } = "Standard";

    public bool AcceptTerms { get; init; } = true;

    public string? OperationId { get; init; }

    public Dictionary<string, string> ToFields()
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.FirstName"] = FirstName,
            ["Input.LastName"] = LastName,
            ["Input.Email"] = Email,
            ["Input.Phone"] = Phone,
            ["Input.Company"] = Company ?? string.Empty,
            ["Input.Country"] = Country,
            ["Input.CustomerType"] = CustomerType,
        };
        if (AcceptTerms)
        {
            fields["Input.AcceptTerms"] = "true";
        }

        if (OperationId is not null)
        {
            fields["Input.OperationId"] = OperationId;
        }

        return fields;
    }
}

/// <summary>Hosts the real DemoCrm in memory and drives it like a browser would (cookies, antiforgery tokens, forms).</summary>
internal sealed partial class DemoCrmApp : IDisposable
{
    public const string ControlToken = "test-control-token";
    public const string DemoEmail = "agent@democrm.test";
    public const string DemoPassword = "DemoPass-2026!";

    private readonly WebApplicationFactory<Program> _factory;

    public DemoCrmApp(string controlToken = ControlToken, bool simulationEnabled = true, int slowDelayMs = 25)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DemoCrm:ControlApi:Token", controlToken);
            builder.UseSetting("DemoCrm:Simulation:Enabled", simulationEnabled ? "true" : "false");
            builder.UseSetting("DemoCrm:Simulation:SlowDelayMs", slowDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost"),
        });
    }

    public HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }

    public async Task<HttpResponseMessage> LoginAsync(string email = DemoEmail, string password = DemoPassword, string? returnUrl = null)
    {
        var fields = new Dictionary<string, string> { ["Email"] = email, ["Password"] = password };
        if (returnUrl is not null)
        {
            fields["ReturnUrl"] = returnUrl;
        }

        return await PostFormAsync("/Login", fields);
    }

    public async Task LoginSuccessfullyAsync()
    {
        var response = await LoginAsync();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public async Task<string> GetHtmlAsync(string path)
    {
        var response = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Loads the page for its antiforgery token (as a browser would), then posts the form.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(
        string path,
        IDictionary<string, string> fields,
        IDictionary<string, string>? headers = null,
        string? tokenFromPath = null)
    {
        var token = await GetAntiforgeryTokenAsync(tokenFromPath ?? path.Split('?')[0]);
        var all = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(all) };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostFormWithoutTokenAsync(string path, IDictionary<string, string> fields)
    {
        return await Client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    public Task<HttpResponseMessage> RegisterAsync(RegistrationForm form, IDictionary<string, string>? headers = null) =>
        PostFormAsync("/Customers/Register", form.ToFields(), headers);

    public async Task<DemoStateDto> GetStateAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo/state");
        request.Headers.Add(DemoControlEndpoints.TokenHeader, ControlToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DemoStateDto>())!;
    }

    public async Task<HttpResponseMessage> ControlResetAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/demo/reset");
        if (token is not null)
        {
            request.Headers.Add(DemoControlEndpoints.TokenHeader, token);
        }

        return await Client.SendAsync(request);
    }

    private async Task<string> GetAntiforgeryTokenAsync(string path)
    {
        var response = await Client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {path} (status {(int)response.StatusCode}).");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Path (without host or query) a redirect points to; the server emits absolute redirect URLs.</summary>
    public static string RedirectPath(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        Assert.NotNull(location);
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    // ---- HTML helpers (the pages expose stable data-testid hooks) ----

    /// <summary>Decoded inner text of the first element with the given data-testid.</summary>
    public static string Text(string html, string testId)
    {
        var match = Regex.Match(html, $"data-testid=\"{Regex.Escape(testId)}\"[^>]*>(.*?)</", RegexOptions.Singleline);
        Assert.True(match.Success, $"Element data-testid=\"{testId}\" not found.");
        return WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }

    public static bool Has(string html, string testId) => html.Contains($"data-testid=\"{testId}\"", StringComparison.Ordinal);

    public static int Count(string html, string testId) => Regex.Count(html, $"data-testid=\"{Regex.Escape(testId)}\"");

    /// <summary>Value of an attribute on the opening tag of the element with the given data-testid.</summary>
    public static string Attribute(string html, string testId, string attribute)
    {
        var tag = Regex.Match(html, $"<[^>]*data-testid=\"{Regex.Escape(testId)}\"[^>]*>", RegexOptions.Singleline);
        Assert.True(tag.Success, $"Element data-testid=\"{testId}\" not found.");
        var attr = Regex.Match(tag.Value, $"{Regex.Escape(attribute)}=\"([^\"]*)\"");
        Assert.True(attr.Success, $"Attribute {attribute} not found on data-testid=\"{testId}\".");
        return WebUtility.HtmlDecode(attr.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
