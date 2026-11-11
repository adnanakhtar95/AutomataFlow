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
public sealed partial class DemoCrmProcess : IAsyncDisposable
{
    private readonly HttpClient _http = new();
    private Process? _process;

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

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

        var start = new ProcessStartInfo("dotnet", [assembly, "--urls", "http://127.0.0.1:0"])
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DemoCrm__ControlApi__Token"] = ControlToken;
        start.Environment["DemoCrm__Simulation__SlowDelayMs"] = "200";

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

    [GeneratedRegex(@"Now listening on: http://127\.0\.0\.1:(\d+)")]
    private static partial Regex ListeningPattern();
}
