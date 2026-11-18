using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AutomataFlow.AutomationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>A started OS process with its captured output (for diagnostics).</summary>
internal sealed class RunningProcess(string name, Process process) : IAsyncDisposable
{
    public ConcurrentQueue<string> Output { get; } = new();

    public string Name => name;

    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    /// <summary>SIGKILL of the whole process tree (Worker, Playwright driver, every Chromium process): a real crash.</summary>
    public async Task KillAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    public string Tail(int lines = 60) => string.Join(Environment.NewLine, Output.TakeLast(lines));

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        process.Dispose();
    }

    internal void Attach()
    {
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Output.Enqueue(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Output.Enqueue("ERR " + e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
}

/// <summary>
/// Phase 5 step 8a: the real Api and the test-only Worker host as SEPARATE OS processes, started from their build output
/// with configuration only through environment variables (as in a deployment), against the rig's database and DemoCrm.
/// </summary>
internal sealed class PlatformProcesses(PlatformRig rig) : IAsyncDisposable
{
    private readonly List<RunningProcess> _processes = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string ApiBaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<RunningProcess> Processes => _processes;

    private static string Output(string project, string folder) =>
        Path.Combine(DemoCrmProcess.RepositoryRoot, folder, project, "bin",
            new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name, "net10.0", project + ".dll");

    private Dictionary<string, string> Environment(Dictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>(rig.Settings);
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings.Where(s => s.Value is not null).ToDictionary(s => s.Key.Replace(":", "__", StringComparison.Ordinal), s => s.Value!);
    }

    private RunningProcess Start(string name, string assembly, Dictionary<string, string> environment)
    {
        if (!File.Exists(assembly))
        {
            throw new InvalidOperationException($"{name} build output not found at {assembly}. Build the solution first.");
        }

        var start = new ProcessStartInfo("dotnet", [assembly])
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        var running = new RunningProcess(name, Process.Start(start) ?? throw new InvalidOperationException($"Could not start {name}."));
        running.Attach();
        _processes.Add(running);
        return running;
    }

    public async Task<RunningProcess> StartApiAsync()
    {
        var port = FreePort();
        var environment = Environment(new()
        {
            ["Auth:Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["Auth:AllowRegistration"] = "true",
            ["Auth:PermitsPerMinute"] = "10000",
        });
        environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        var api = Start("Api", Output("AutomataFlow.Api", "src"), environment);
        ApiBaseUrl = $"http://127.0.0.1:{port}";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                if ((await _http.GetAsync(ApiBaseUrl + "/health/ready")).IsSuccessStatusCode)
                {
                    return api;
                }
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline && !api.HasExited)
            {
            }

            if (DateTime.UtcNow >= deadline || api.HasExited)
            {
                throw new InvalidOperationException("The Api process did not become ready." + System.Environment.NewLine + api.Tail());
            }

            await Task.Delay(200);
        }
    }

    /// <summary>Starts a test-only Worker host process and waits until it registered its incarnation.</summary>
    public async Task<RunningProcess> StartWorkerAsync(Dictionary<string, string?>? overrides = null)
    {
        var before = await IncarnationsAsync();
        var worker = Start("Worker", Output("AutomataFlow.WorkerTestHost", "tests"), Environment(overrides));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (await IncarnationsAsync() <= before)
        {
            if (worker.HasExited || DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("The Worker process did not register an incarnation." + System.Environment.NewLine + worker.Tail());
            }

            await Task.Delay(200);
        }

        return worker;
    }

    /// <summary>Starts the PRODUCTION Worker (with its production containment handshake) as a plain process.</summary>
    public RunningProcess StartProductionWorker(Dictionary<string, string?>? overrides = null) =>
        Start("ProductionWorker", Output("AutomataFlow.Worker", "src"), Environment(overrides));

    private Task<int> IncarnationsAsync() =>
        rig.QueryAsync(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"WorkerIncarnations\"").SingleAsync());

    // ----- Api client helpers -----

    public HttpClient Client(string? accessToken = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public async Task<(string AccessToken, Guid Organization, Guid User)> RegisterAsync()
    {
        var email = $"operator-{Guid.NewGuid():N}@example.test";
        var response = await Client().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Correct-Horse-2026", organizationName = "E2E", organizationSlug = "e2e-" + Guid.NewGuid().ToString("N")[..20] });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("accessToken").GetString()!;
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        var user = JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("sub").GetGuid();
        return (token, body.GetProperty("organizationId").GetGuid(), user);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        _http.Dispose();
    }
}
