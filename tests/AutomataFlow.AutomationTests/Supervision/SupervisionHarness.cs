using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutomataFlow.AutomationTests.Platform;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Supervision;

/// <summary>A process in the supervision container's process table.</summary>
public sealed record AgentProcess(int Pid, int PPid, int Uid, string Name, string State);

/// <summary>Client of the test-only root agent of the supervision container (scripts/verify-in-container.ps1, ADR-075).</summary>
public sealed class SupervisionAgent(string socketPath)
{
    public const int WorkerUid = 10001;
    public const int SupervisorUid = 10002;

    private async Task<JsonElement> CallAsync(object request)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"));
        var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await socket.ReceiveAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        var response = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("Supervision agent: " + error.GetString());
        }

        return response;
    }

    public async Task<UnitLaunch> LaunchAsync(IReadOnlyDictionary<string, string> environment)
    {
        var response = await CallAsync(new { command = "launch", launcher = SupervisionPaths.Launcher, environment });
        return new UnitLaunch(this, response.GetProperty("id").GetString()!, response.GetProperty("pid").GetInt32(), response.GetProperty("log").GetString()!);
    }

    public async Task<int?> WaitAsync(string id, TimeSpan timeout)
    {
        var response = await CallAsync(new { command = "wait", id, timeoutSeconds = (int)timeout.TotalSeconds });
        return response.GetProperty("exited").GetBoolean() ? response.GetProperty("exitCode").GetInt32() : null;
    }

    /// <summary>Runs a command as a NON-ROOT uid in the supervision container (exactly that uid's kernel permissions).</summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecAsync(int uid, params string[] arguments)
    {
        var response = await CallAsync(new { command = "exec", uid, arguments });
        return (response.GetProperty("exitCode").GetInt32(), response.GetProperty("stdout").GetString()!, response.GetProperty("stderr").GetString()!);
    }

    public async Task<IReadOnlyList<AgentProcess>> PsAsync() =>
        (await CallAsync(new { command = "ps" })).GetProperty("processes").EnumerateArray()
            .Select(p => new AgentProcess(p.GetProperty("pid").GetInt32(), p.GetProperty("ppid").GetInt32(), p.GetProperty("uid").GetInt32(), p.GetProperty("name").GetString()!, p.GetProperty("state").GetString()!))
            .ToList();

    /// <summary>The unit's processes: everything that runs as the Worker's kernel uid.</summary>
    public async Task<IReadOnlyList<AgentProcess>> UnitProcessesAsync() => [.. (await PsAsync()).Where(p => p.Uid == WorkerUid)];

    /// <summary>Signals a process as the given uid (the kernel decides whether that uid may).</summary>
    public Task<(int ExitCode, string Output, string Error)> SignalAsync(int asUid, string signal, params int[] pids) =>
        ExecAsync(asUid, ["kill", "-" + signal, .. pids.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    public async Task<string> StatusOfAsync(int pid) => (await ExecAsync(WorkerUid, "cat", $"/proc/{pid}/status")).Output;
}

/// <summary>One launch of the real launcher: the supervisor process (the launcher exec'd into it) and its log.</summary>
public sealed partial class UnitLaunch(SupervisionAgent agent, string id, int supervisorPid, string log)
{
    public int SupervisorPid => supervisorPid;

    public string Log => File.Exists(log) ? File.ReadAllText(log) : string.Empty;

    public Task<int?> WaitForExitAsync(TimeSpan timeout) => agent.WaitAsync(id, timeout);

    /// <summary>Waits for the supervisor to exit with <paramref name="expected"/>; on mismatch the failure carries its log.</summary>
    public async Task AssertExitAsync(int expected)
    {
        var code = await WaitForExitAsync(TimeSpan.FromMinutes(3));
        Assert.True(code == expected, $"supervisor exit {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(still running)"}, expected {expected}:{Environment.NewLine}{Tail(60)}");
    }

    public async Task<string> WaitForLogAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Log.Contains(text, StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, $"'{text}' never appeared in the supervision log:{Environment.NewLine}{Tail()}");
            await Task.Delay(200);
        }

        return Log;
    }

    public string Tail(int lines = 80) => string.Join(Environment.NewLine, Log.Split('\n').TakeLast(lines));

    /// <summary>Kills whatever is left (supervisor first, then the unit) so the next test starts in an empty container.</summary>
    public async Task CleanUpAsync()
    {
        if (await WaitForExitAsync(TimeSpan.FromSeconds(1)) is null)
        {
            await agent.SignalAsync(SupervisionAgent.SupervisorUid, "TERM", SupervisorPid);
            if (await WaitForExitAsync(TimeSpan.FromSeconds(90)) is null)
            {
                await agent.SignalAsync(SupervisionAgent.SupervisorUid, "KILL", SupervisorPid);
            }
        }

        var unit = await agent.UnitProcessesAsync();
        if (unit.Count > 0)
        {
            await agent.SignalAsync(SupervisionAgent.WorkerUid, "KILL", [.. unit.Select(p => p.Pid)]);
        }
    }

    [GeneratedRegex(@"HOLDER-READY incarnation=(?<inc>[0-9a-f-]+) cookie=(?<cookie>\d+)")]
    public static partial Regex HolderReady();
}

/// <summary>Build-output paths; the supervision container mounts the same volume at the same path (/work).</summary>
public static class SupervisionPaths
{
    private static string Configuration => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    private static string Output(string folder, string project) =>
        Path.Combine(DemoCrmProcess.RepositoryRoot, folder, project, "bin", Configuration, "net10.0", project + ".dll");

    public static string Launcher => Path.Combine(DemoCrmProcess.RepositoryRoot, "deploy", "worker", "af-worker-launch.sh");

    public static string Worker => "dotnet " + Output("src", "AutomataFlow.Worker");

    public static string Supervisor => "dotnet " + Output("src", "AutomataFlow.Supervisor");

    public static string SocketHolder(string host, int port, string variant) => $"dotnet {Output("tests", "AutomataFlow.WorkerTestHost")} socket-holder {host} {port} {variant}";
}

[CollectionDefinition(Name)]
public sealed class SupervisionGroup : ICollectionFixture<SupervisionFixture>
{
    public const string Name = "Supervision";
}

/// <summary>
/// The supervision container (ADR-075) plus a DemoCrm reachable from it ("tests" on the private network). Requires the
/// environment set by scripts/verify-in-container.ps1; without it these tests FAIL (like the database tests), never skip.
/// </summary>
public sealed class SupervisionFixture : IAsyncLifetime
{
    public SupervisionAgent Agent { get; private set; } = null!;

    public DemoCrmProcess DemoCrm { get; } = new("0.0.0.0", "tests");

    public string SharedRoot { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var socket = System.Environment.GetEnvironmentVariable("AUTOMATAFLOW_SUPERVISION_AGENT")
            ?? throw new InvalidOperationException("AUTOMATAFLOW_SUPERVISION_AGENT is not set: run scripts/verify-in-container.ps1 (step 7 supervision container).");
        SharedRoot = System.Environment.GetEnvironmentVariable("AUTOMATAFLOW_SHARED_DIRECTORY")
            ?? throw new InvalidOperationException("AUTOMATAFLOW_SHARED_DIRECTORY is not set.");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(socket))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("The supervision agent did not start.");
            }

            await Task.Delay(500);
        }

        Agent = new SupervisionAgent(socket);
        await DemoCrm.StartAsync();
    }

    /// <summary>The environment for one launch: the rig's platform settings, the commands, and fast supervisor timings.</summary>
    internal static Dictionary<string, string> Environment(PlatformRig rig, string workerCommand, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>(rig.Settings)
        {
            ["Journal:LeaseSeconds"] = "130",
            ["Worker:ClaimLeaseSeconds"] = "10",
            ["Worker:HeartbeatSeconds"] = "2",
            ["Worker:RecoveryIntervalSeconds"] = "2",
            ["Worker:FenceRecheckSeconds"] = "5",
            ["Worker:FenceWaitSeconds"] = "60",
            ["Supervisor:HeartbeatStaleSeconds"] = "8",
            ["Supervisor:StartupTimeoutSeconds"] = "120",
            ["Supervisor:FenceConfirmTimeoutSeconds"] = "30",
            ["Supervisor:DrainPolls"] = "20",
            ["Supervisor:DrainPollMilliseconds"] = "500",
            ["Supervisor:GracefulStopSeconds"] = "40",
            ["Supervisor:MonitorIntervalMilliseconds"] = "500",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var environment = settings.Where(s => s.Value is not null).ToDictionary(s => s.Key.Replace(":", "__", StringComparison.Ordinal), s => s.Value!);
        environment["PLAYWRIGHT_BROWSERS_PATH"] = "/ms-playwright";
        environment["DOTNET_NOLOGO"] = "1";
        environment["AF_WORKER_COMMAND"] = workerCommand;
        environment["AF_SUPERVISOR_COMMAND"] = SupervisionPaths.Supervisor;
        return environment;
    }

    public async Task DisposeAsync() => await DemoCrm.DisposeAsync();
}

/// <summary>Journal reads used by the supervision tests (FenceConfirmations is the audit of every fence report).</summary>
internal static class SupervisionQueries
{
    public static async Task<(bool Accepted, string Source, string Method, string? Reason)?> FenceConfirmationAsync(PlatformRig rig, Guid incarnation)
    {
        await using var connection = new Npgsql.NpgsqlConnection(rig.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Accepted","Source","Method","RejectionReason" FROM "FenceConfirmations" WHERE "IncarnationId" = @id ORDER BY "RecordedAt" DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("id", incarnation);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetBoolean(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }

    public static async Task<(Guid Id, string UnitKind)> WaitForIncarnationAsync(PlatformRig rig, int before, TimeSpan timeout, UnitLaunch launch)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var connection = new Npgsql.NpgsqlConnection(rig.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new Npgsql.NpgsqlCommand("""SELECT "Id","UnitKind" FROM "WorkerIncarnations" ORDER BY "RegisteredAt" """, connection);
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<(Guid, string)>();
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetGuid(0), reader.GetString(1)));
                }

                if (rows.Count > before)
                {
                    return rows[^1];
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "No incarnation registered:" + Environment.NewLine + launch.Tail());
            await Task.Delay(300);
        }
    }

    public static async Task<int> IncarnationCountAsync(PlatformRig rig)
    {
        await using var connection = new Npgsql.NpgsqlConnection(rig.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""SELECT count(*)::int FROM "WorkerIncarnations" """, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
