using System.Net;
using System.Text.Json;
using DemoCrm.Configuration;
using DemoCrm.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace DemoCrm.Tests.Web;

/// <summary>The reset control token comes only from the environment, and every reset path stays protected.</summary>
public sealed class ControlTokenTests : IDisposable
{
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutomataFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (AutomataFlow.slnx).");
    }

    [Fact]
    public void Environment_variable_double_underscore_maps_to_the_control_token_setting()
    {
        // Same __ -> : convention as the real variable DemoCrm__ControlApi__Token; a unique prefix keeps the
        // test from touching the process-wide variable that other tests and developers may rely on.
        var prefix = $"AFTEST_{Guid.NewGuid():N}_";
        var variable = prefix + "DemoCrm__ControlApi__Token";
        Environment.SetEnvironmentVariable(variable, "token-from-environment");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            Assert.Equal("token-from-environment", configuration["DemoCrm:ControlApi:Token"]);
            var options = configuration.GetSection(DemoCrmOptions.SectionName).Get<DemoCrmOptions>();
            Assert.Equal("token-from-environment", options!.ControlApi.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void No_committed_settings_file_contains_a_control_token()
    {
        var folder = Path.Combine(FindRepositoryRoot(), "src", "DemoCrm");
        var files = Directory.GetFiles(folder, "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var token = document.RootElement.TryGetProperty("DemoCrm", out var demo)
                && demo.TryGetProperty("ControlApi", out var control)
                && control.TryGetProperty("Token", out var value)
                ? value.GetString()
                : null;

            Assert.True(string.IsNullOrEmpty(token), $"{Path.GetFileName(file)} must not contain a control token; use DemoCrm__ControlApi__Token.");
        }
    }

    [Fact]
    public async Task Control_api_stays_disabled_in_development_when_the_token_is_missing()
    {
        using var development = new DemoCrmApp(controlToken: string.Empty, environment: "Development");

        Assert.Equal(HttpStatusCode.NotFound, (await development.ControlResetAsync("anything")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await development.GetAsync("/demo/state")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    [InlineData("TEST-CONTROL-TOKEN")]
    public async Task Missing_or_invalid_tokens_return_401_for_both_control_endpoints_and_change_nothing(string? token)
    {
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm());

        using var state = new HttpRequestMessage(HttpMethod.Get, "/demo/state");
        if (token is not null)
        {
            state.Headers.TryAddWithoutValidation("X-Demo-Control-Token", token);
        }

        var stateResponse = await _app.Client.SendAsync(state);
        var resetResponse = await _app.ControlResetAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, stateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resetResponse.StatusCode);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Admin_reset_without_an_antiforgery_token_is_rejected_and_changes_nothing()
    {
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm());

        var response = await _app.PostFormWithoutTokenAsync("/Admin?handler=Reset", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Admin_reset_with_a_forged_antiforgery_token_is_rejected_and_changes_nothing()
    {
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm());

        var response = await _app.PostFormWithoutTokenAsync(
            "/Admin?handler=Reset",
            new Dictionary<string, string> { ["__RequestVerificationToken"] = "forged-value" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Admin_reset_requires_a_signed_in_user_even_with_a_valid_control_token_header()
    {
        // Sign in only to create data and read state, then sign out and try to reset anonymously.
        await _app.LoginSuccessfullyAsync();
        await _app.RegisterAsync(new RegistrationForm());
        var logout = await _app.PostFormAsync("/Logout", new Dictionary<string, string>(), tokenFromPath: "/Customers");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=Reset")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        request.Headers.Add("X-Demo-Control-Token", DemoCrmApp.ControlToken);
        var response = await _app.Client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest, $"Unexpected status {response.StatusCode}");
        Assert.Equal(25, (await _app.GetStateAsync()).CustomerCount);
    }

    [Fact]
    public async Task Admin_page_get_is_not_available_anonymously()
    {
        var response = await _app.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", DemoCrmApp.RedirectPath(response));
    }
}
