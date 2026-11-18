using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// TEST-ONLY (ADR-075). Runs as root in the step 7 supervision container, which has the production Worker posture
// (cap_drop ALL; NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT; Chromium seccomp profile). The tests in the main test
// container drive it over a Unix socket on the shared volume:
//   launch  - run the REAL production launcher (deploy/worker/af-worker-launch.sh) with the given environment;
//   wait    - wait for a launched supervisor to exit (exit code);
//   exec    - run a command as a given NON-ROOT uid (setpriv), e.g. to signal a process exactly as that uid may;
//   ps      - the container's process table (pid, ppid, uids, name), as root reads it.
// The agent holds no TCP socket, so it never affects the supervisor's socket-ownership checks.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: AutomataFlow.SupervisionAgent <socket-path> <log-directory>");
    return 2;
}

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The supervision agent runs on Linux only.");
    return 2;
}

var socketPath = args[0];
var logDirectory = args[1];
Directory.CreateDirectory(logDirectory);
File.Delete(socketPath);
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
File.SetUnixFileMode(socketPath, (UnixFileMode)0b110_110_110);
listener.Listen(16);
Console.WriteLine($"supervision agent listening on {socketPath}");

var launched = new ConcurrentDictionary<string, Process>();
while (true)
{
    var client = await listener.AcceptAsync();
    _ = Task.Run(async () =>
    {
        using (client)
        {
            try
            {
                var request = await ReadAsync(client);
                var response = await HandleAsync(request);
                await client.SendAsync(Encoding.UTF8.GetBytes(response.ToJsonString() + "\n"));
            }
            catch (Exception ex)
            {
                await client.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.GetType().Name + ": " + ex.Message }) + "\n"));
            }
        }
    });
}

async Task<JsonElement> ReadAsync(Socket client)
{
    var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
        var read = await client.ReceiveAsync(chunk);
        if (read == 0)
        {
            break;
        }

        buffer.Write(chunk, 0, read);
        if (chunk[read - 1] == (byte)'\n')
        {
            break;
        }
    }

    return JsonDocument.Parse(buffer.ToArray()).RootElement;
}

async Task<System.Text.Json.Nodes.JsonObject> HandleAsync(JsonElement request)
{
    switch (request.GetProperty("command").GetString())
    {
        case "launch":
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var start = new ProcessStartInfo("/bin/bash", [request.GetProperty("launcher").GetString()!])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/share/dotnet";
            foreach (var pair in request.GetProperty("environment").EnumerateObject())
            {
                start.Environment[pair.Name] = pair.Value.GetString();
            }

            var process = Process.Start(start)!;
            var log = Path.Combine(logDirectory, id + ".log");
            var writer = new StreamWriter(log, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (writer) { writer.WriteLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (writer) { writer.WriteLine("ERR " + e.Data); } } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            launched[id] = process;
            return new() { ["id"] = id, ["pid"] = process.Id, ["log"] = log };
        }

        case "wait":
        {
            var process = launched[request.GetProperty("id").GetString()!];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.GetProperty("timeoutSeconds").GetInt32()));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new() { ["exited"] = true, ["exitCode"] = process.ExitCode };
            }
            catch (OperationCanceledException)
            {
                return new() { ["exited"] = false };
            }
        }

        case "exec":
        {
            var uid = request.GetProperty("uid").GetInt32();
            if (uid <= 0)
            {
                throw new InvalidOperationException("exec runs only as a non-root uid");
            }

            var arguments = new List<string> { $"--reuid={uid}", $"--regid={uid}", "--clear-groups", "--inh-caps=-all", "--" };
            arguments.AddRange(request.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!));
            var start = new ProcessStartInfo("setpriv", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["HOME"] = "/tmp";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TryGetProperty("timeoutSeconds", out var t) ? t.GetInt32() : 60));
            await process.WaitForExitAsync(timeout.Token);
            return new() { ["exitCode"] = process.ExitCode, ["stdout"] = await stdout, ["stderr"] = await stderr };
        }

        case "ps":
        {
            var rows = new System.Text.Json.Nodes.JsonArray();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    continue;
                }

                try
                {
                    var status = File.ReadAllLines(Path.Combine(dir, "status")).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1].Trim());
                    rows.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["pid"] = pid,
                        ["ppid"] = int.Parse(status["PPid"], CultureInfo.InvariantCulture),
                        ["uid"] = int.Parse(status["Uid"].Split('\t')[0], CultureInfo.InvariantCulture),
                        ["name"] = status["Name"],
                        ["state"] = status["State"],
                    });
                }
                catch (IOException)
                {
                }
            }

            return new() { ["processes"] = rows };
        }

        default:
            throw new InvalidOperationException("unknown command");
    }
}
