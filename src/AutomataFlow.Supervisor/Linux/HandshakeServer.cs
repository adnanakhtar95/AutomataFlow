using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AutomataFlow.Application.Workflows.Execution;

using Microsoft.Extensions.Logging;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>What the supervisor knows about its unit when a peer asks for the handshake.</summary>
public sealed record UnitIdentity(int WorkerUid, string UserNamespace, string PidNamespace, ContainmentUnit Unit, string LedgerDirectory);

/// <summary>Validation of a handshake peer (pure, unit-tested): only a contained Worker of THIS unit is answered.</summary>
public static class HandshakePeer
{
    public static string? Validate(UnitIdentity unit, ProcStatus? peer, string? peerUserNamespace, string? peerPidNamespace)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (peer is null)
        {
            return "peer-gone";
        }

        if (peer.Uids.Count == 0 || peer.Uids.Any(u => u != unit.WorkerUid))
        {
            return "peer-not-worker-uid";
        }

        if (peerUserNamespace != unit.UserNamespace || peerPidNamespace != unit.PidNamespace)
        {
            return "peer-not-in-unit";
        }

        if (!peer.HoldsNoCapability || !peer.NoNewPrivs)
        {
            return "peer-holds-privileges";
        }

        return null;
    }
}

/// <summary>
/// The supervisor's side of the containment handshake (ADR-075), on a Unix socket in a directory created by the supervisor's
/// uid before the unit could run (group: the Worker's; mode 2750; the socket 0660). The peer is identified by the kernel
/// (<c>SO_PEERCRED</c>), never by anything it says. Reads use plain <c>recv</c>: passed file descriptors (SCM_RIGHTS) are
/// never installed in the supervisor's fd table, so a Worker cannot plant a socket the drain would treat as the supervisor's.
/// </summary>
public sealed class HandshakeServer(string path, UnitIdentity? unit, string? refusal, ILogger logger) : IDisposable
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    public void Start(CancellationToken cancellationToken)
    {
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The containment handshake runs on Linux only.");
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        _listener.Listen(4);
        _ = Task.Run(() => AcceptLoopAsync(cancellationToken), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    await AnswerAsync(client, cancellationToken);
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                {
                    logger.LogWarning("Containment handshake connection failed {ExceptionType}", ex.GetType().Name);
                }
            }
        }
    }

    private async Task AnswerAsync(Socket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        var length = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (length < buffer.Length && Array.IndexOf(buffer, (byte)'\n', 0, length) < 0)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token); // plain recv: no SCM_RIGHTS
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        ContainmentProtocol.Reply reply;
        if (Encoding.ASCII.GetString(buffer, 0, length) != ContainmentProtocol.Hello)
        {
            reply = new ContainmentProtocol.Reply("refused", "protocol", null, null, null);
        }
        else if (refusal is not null || unit is null)
        {
            reply = new ContainmentProtocol.Reply("refused", refusal ?? "unit-unavailable", null, null, null);
        }
        else
        {
            Span<byte> credentials = stackalloc byte[12];
            var pid = client.GetRawSocketOption(SolSocket, SoPeerCred, credentials) == 12 ? BinaryPrimitives.ReadInt32LittleEndian(credentials) : 0;
            var problem = pid <= 0 ? "peer-credentials-unavailable"
                : HandshakePeer.Validate(unit, ProcFs.Status(pid), ProcFs.Namespace(pid, "user"), ProcFs.Namespace(pid, "pid"));
            reply = problem is null
                ? new ContainmentProtocol.Reply("ok", null, nameof(ContainmentKind.LinuxNamespaces), unit.Unit.Name, unit.LedgerDirectory)
                : new ContainmentProtocol.Reply("refused", problem, null, null, null);
            logger.LogInformation("Containment handshake {Result} {PeerPid} {Reason}", reply.Status, pid, problem);
        }

        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, ContainmentProtocol.Json), SocketFlags.None, cancellationToken);
    }

    public void Dispose()
    {
        _listener.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
