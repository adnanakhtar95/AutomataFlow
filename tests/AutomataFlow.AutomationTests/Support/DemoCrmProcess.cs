using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomataFlow.AutomationTests.Support;

/// <summary>
/// Launches DemoCrm as a real, separate process on 127.0.0.1 (ADR-027/029): a real browser cannot reach
/// WebApplicationFactory's in-memory server. The control token is random per run and never written to disk.
/// </summary>
public sealed partial class DemoCrmProcess(string bindAddress = "127.0.0.1", string publicHost = "127.0.0.1") : IAsyncDisposable
{
    private readonly HttpClient _http = new();
    private Process? _process;

    public int Port { get; private set; }

    /// <summary>The address other processes (and, for the supervision tests, other containers) reach DemoCrm at.</summary>
    public string BaseUrl => $"http://{publicHost}:{Port}";

    public string ControlToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutomataFlow.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root (AutomataFlow.slnx) not found.");
        }
    }

    public async Task StartAsync()
    {
        // .../tests/AutomataFlow.AutomationTests/bin/<Configuration>/net10.0/ -> same configuration for DemoCrm.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var projectDirectory = Path.Combine(RepositoryRoot, "src", "DemoCrm");
        var assembly = Path.Combine(projectDirectory, "bin", configuration, "net10.0", "DemoCrm.dll");
        if (!File.Exists(assembly))
        {
            throw new InvalidOperationException($"DemoCrm build output not found at {assembly}. Build the solution first.");
        }

        var start = new ProcessStartInfo("dotnet", [assembly, "--urls", $"http://{bindAddress}:0"])
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["AllowedHosts"] = $"localhost;127.0.0.1;[::1];{publicHost}"; // the supervision tests reach it as "tests"
        start.Environment["DemoCrm__ControlApi__Token"] = ControlToken;
        start.Environment["DemoCrm__Simulation__SlowDelayMs"] = "200";
        // 9 s keeps the late commit clearly after an "immediate" reconcile (which first signs in) while staying inside the
        // 10 s contract of variants C/D: DemoCrm's bound is max(SlowDelayMs, LateCommitDelayMs) + 1 s = 10 s (docs/DEMO_CRM.md).
        start.Environment["DemoCrm__Simulation__LateCommitDelayMs"] = "9000";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start DemoCrm.");
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && ListeningPattern().Match(e.Data) is { Success: true } m)
            {
                listening.TrySetResult(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Port = await listening.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                if ((await _http.GetAsync($"{BaseUrl}/Login")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                // not listening yet
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("DemoCrm did not become ready.");
            }

            await Task.Delay(100);
        }
    }

    public async Task ResetAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/demo/reset");
        request.Headers.Add("X-Demo-Control-Token", ControlToken);
        (await _http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    /// <summary>Every POST to /Customers/Register since the last reset (ADR-054 fixture counter).</summary>
    public async Task<int> RegistrationSubmissionsAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/demo/state");
        request.Headers.Add("X-Demo-Control-Token", ControlToken);
        var state = await (await _http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        return state.GetProperty("registrationSubmissions").GetInt32();
    }

    public async Task<int> CustomerCountAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/demo/state");
        request.Headers.Add("X-Demo-Control-Token", ControlToken);
        var state = await (await _http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        return state.GetProperty("customerCount").GetInt32();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        _http.Dispose();
    }

    [GeneratedRegex(@"Now listening on: http://[^:/\s]+:(\d+)")]
    private static partial Regex ListeningPattern();
}
