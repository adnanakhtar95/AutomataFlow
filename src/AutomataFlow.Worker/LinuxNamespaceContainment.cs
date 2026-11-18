using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Worker;

public sealed class ContainmentOptions
{
    public const string SectionName = "Containment";

    /// <summary>The supervisor's handshake socket (set by the launcher, deploy/worker/af-worker-launch.sh).</summary>
    public string? SupervisorSocket { get; set; }

    /// <summary>How long the Worker waits for the supervisor to accept the handshake before refusing to start.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// The Worker's own credentials as the kernel reports them in <c>/proc/self/status</c>. Inside the unit the PID namespace is
/// nested, so process identity is always read from <c>/proc/self</c>, never by (namespace-relative) PID (ADR-075).
/// </summary>
public sealed record SelfCredentials(ulong CapInh, ulong CapPrm, ulong CapEff, ulong CapBnd, ulong CapAmb, bool NoNewPrivs, IReadOnlyList<int> NsPids)
{
    public static SelfCredentials Parse(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var fields = status.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        ulong Cap(string name) => fields.TryGetValue(name, out var v) ? ulong.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ulong.MaxValue;
        var nspid = fields.TryGetValue("NSpid", out var n) ? n.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList() : [];
        return new SelfCredentials(Cap("CapInh"), Cap("CapPrm"), Cap("CapEff"), Cap("CapBnd"), Cap("CapAmb"),
            fields.TryGetValue("NoNewPrivs", out var nnp) && nnp == "1", nspid);
    }

    /// <summary>Problems that make this process unfit to run inside a unit (empty when contained correctly).</summary>
    public IReadOnlyList<string> ContainmentProblems()
    {
        var problems = new List<string>();
        if ((CapInh | CapPrm | CapEff | CapBnd | CapAmb) != 0)
        {
            problems.Add("The Worker holds capabilities (all capability sets, including the bounding set, must be empty).");
        }

        if (!NoNewPrivs)
        {
            problems.Add("no_new_privs is not set.");
        }

        if (NsPids.Count < 2)
        {
            problems.Add("The Worker is not in a nested PID namespace.");
        }

        return problems;
    }
}

/// <summary>
/// Production Linux containment handshake (ADR-059 as amended by ADR-075). The Worker first checks its own credentials
/// (no capability in any set, <c>no_new_privs</c>, nested PID namespace), then asks the supervisor over its Unix socket.
/// The supervisor authenticates this process by kernel peer credentials and answers with the unit and the ledger directory,
/// which must equal the one this Worker writes. Anything else throws: the Worker then refuses to start.
/// </summary>
public sealed class LinuxNamespaceContainmentHandshake(IOptions<ContainmentOptions> options, IOptions<EgressProxyOptions> egress, TimeProvider clock) : IContainmentHandshake
{
    public async Task<ContainmentUnit> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Linux namespace containment is available on Linux only.");
        }

        var self = SelfCredentials.Parse(await File.ReadAllTextAsync("/proc/self/status", cancellationToken));
        if (self.ContainmentProblems() is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        var path = options.Value.SupervisorSocket;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Containment:SupervisorSocket is not configured.");
        }

        var ledger = egress.Value.LedgerDirectory;
        if (string.IsNullOrWhiteSpace(ledger))
        {
            throw new InvalidOperationException("Automation:EgressProxy:LedgerDirectory is not configured.");
        }

        var reply = await AskAsync(path, cancellationToken);
        if (reply.Status != "ok" || reply.Kind != nameof(ContainmentKind.LinuxNamespaces) || string.IsNullOrWhiteSpace(reply.Unit))
        {
            throw new InvalidOperationException("The supervisor refused the containment handshake: " + (reply.Reason ?? "no reason"));
        }

        if (!string.Equals(Path.GetFullPath(reply.LedgerDirectory ?? string.Empty), Path.GetFullPath(ledger), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supervisor drains another ledger directory than the one this Worker writes.");
        }

        return new ContainmentUnit(ContainmentKind.LinuxNamespaces, reply.Unit);
    }

    private async Task<ContainmentProtocol.Reply> AskAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow().AddSeconds(options.Value.HandshakeTimeoutSeconds);
        while (true)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                await socket.SendAsync(Encoding.ASCII.GetBytes(ContainmentProtocol.Hello), cancellationToken);
                var buffer = new byte[ContainmentProtocol.MaxReplyBytes];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }

                return JsonSerializer.Deserialize<ContainmentProtocol.Reply>(buffer.AsSpan(0, length), ContainmentProtocol.Json)
                    ?? throw new InvalidOperationException("Empty containment reply.");
            }
            catch (SocketException) when (clock.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, cancellationToken); // the supervisor may still be starting
            }
        }
    }
}
