using System.Net;
using System.Net.Sockets;

namespace AutomataFlow.Automation.Network.Proxy;

/// <summary>Deterministic interleaving points for tests (docs/PHASE4_PLAN.md §13 WG/V tests). Null in production.</summary>
internal enum EgressHookPoint
{
    /// <summary>After the credentials were checked, before connection admission (V2 admission race).</summary>
    AfterAuthenticate,

    /// <summary>Before upstream connection creation asks the write gate for admission (V5).</summary>
    BeforeOpen,

    /// <summary>H-pre: before a payload write asks the write gate for admission.</summary>
    BeforeWriteEnter,

    /// <summary>H-in: after a successful admission, immediately before the send syscall.</summary>
    AfterWriteEnter,

    /// <summary>H-post: after the send returned, before the admission is released.</summary>
    AfterWriteSend,
}

internal sealed class EgressTestHooks
{
    public Func<EgressHookPoint, long, Task>? OnPoint { get; set; }

    /// <summary>Number of upstream send syscalls actually issued (WG2: must not change after a refused admission).</summary>
    public int SendCalls => Volatile.Read(ref _sendCalls);

    internal int _sendCalls;

    public Task AtAsync(EgressHookPoint point, long connectionId) => OnPoint?.Invoke(point, connectionId) ?? Task.CompletedTask;
}

/// <summary>Upstream socket handling shared by the channel and the barrier.</summary>
internal static class UpstreamSockets
{
    private const int IpProtoTcp = 6;
    private const int LinuxTcpUserTimeout = 18;
    private const int WindowsTcpMaxRt = 5;

    /// <summary>
    /// Abortive close (barrier step B3): linger on with timeout 0 makes the close send RST and discard the unsent
    /// send queue, and TCP originates no further segment for the connection (E2). Segments already handed below the
    /// socket layer are NOT recalled (E3, never claimed).
    /// </summary>
    public static void Abort(Socket socket)
    {
        try
        {
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already closed or reset; closing below is still attempted.
        }

        try
        {
            socket.Close();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }
    }

    /// <summary>
    /// Fail-fast option only (docs/PHASE4_PLAN.md §7.6.4): Linux <c>TCP_USER_TIMEOUT</c> in milliseconds, Windows
    /// <c>TCP_MAXRT</c> in seconds. It bounds stalled connections; it is NOT a retransmission deadline and no guarantee
    /// depends on it. On any other platform, or if the OS refuses it, the connection is not used (fail closed).
    /// </summary>
    public static void ApplyFailFast(Socket socket, int userTimeoutSeconds)
    {
        if (OperatingSystem.IsLinux())
        {
            socket.SetRawSocketOption(IpProtoTcp, LinuxTcpUserTimeout, BitConverter.GetBytes(checked(userTimeoutSeconds * 1000)));
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)WindowsTcpMaxRt, userTimeoutSeconds);
            return;
        }

        throw new PlatformNotSupportedException("Upstream fail-fast socket options are only implemented for Linux and Windows.");
    }

    /// <summary>Reads the configured fail-fast value back (tests, V10): milliseconds on Linux, seconds on Windows.</summary>
    public static int ReadFailFast(Socket socket)
    {
        if (OperatingSystem.IsLinux())
        {
            Span<byte> value = stackalloc byte[4];
            socket.GetRawSocketOption(IpProtoTcp, LinuxTcpUserTimeout, value);
            return BitConverter.ToInt32(value);
        }

        if (OperatingSystem.IsWindows())
        {
            return (int)socket.GetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)WindowsTcpMaxRt)!;
        }

        throw new PlatformNotSupportedException();
    }
}

/// <summary>
/// The ONLY type that sends on an upstream socket (W2, source-scanned). Every payload chunk (at most 16 KB, W4) is
/// sent inside a write-gate admission, without a cancellation token (W3). An upstream connection is created inside an
/// admission and is usable only once its 4-tuple and OS identity are durably in the connection ledger (W5).
/// </summary>
internal sealed class UpstreamChannel
{
    public const int MaxChunkBytes = 16 * 1024;

    private readonly EgressSession _session;
    private readonly long _connectionId;
    private readonly EgressTestHooks? _hooks;

    private UpstreamChannel(EgressSession session, long connectionId, Socket socket, ConnectionLedgerEntry ledgerEntry, EgressTestHooks? hooks)
    {
        _session = session;
        _connectionId = connectionId;
        Socket = socket;
        LedgerEntry = ledgerEntry;
        _hooks = hooks;
    }

    public Socket Socket { get; }

    public ConnectionLedgerEntry LedgerEntry { get; }

    public static async Task<(UpstreamChannel? Channel, string? Failure)> OpenAsync(
        EgressSession session,
        long connectionId,
        IPEndPoint endpoint,
        IUpstreamConnector connector,
        IConnectionLedger ledger,
        IConnectionIdentityProvider identities,
        int userTimeoutSeconds,
        TimeSpan connectTimeout,
        EgressTestHooks? hooks,
        CancellationToken stop)
    {
        if (hooks is not null)
        {
            await hooks.AtAsync(EgressHookPoint.BeforeOpen, connectionId).ConfigureAwait(false);
        }

        if (!session.Gate.TryEnter())
        {
            return (null, "revoked");
        }

        Socket? socket = null;
        try
        {
            // Connecting sends no payload, so it may be cancelled by revocation, proxy stop or the connect timeout.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop, session.RevocationToken);
            cts.CancelAfter(connectTimeout);
            try
            {
                socket = await connector.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                return (null, session.IsRevoked ? "revoked" : "upstream-unreachable");
            }

            try
            {
                UpstreamSockets.ApplyFailFast(socket, userTimeoutSeconds);
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or OverflowException)
            {
                UpstreamSockets.Abort(socket);
                return (null, "socket-options-unavailable");
            }

            if (!identities.TryGetIdentity(socket, out var identity, out _))
            {
                UpstreamSockets.Abort(socket);
                return (null, "connection-identity-unavailable");
            }

            ConnectionLedgerEntry entry;
            try
            {
                entry = ledger.Append(session.SessionId, (IPEndPoint)socket.LocalEndPoint!, (IPEndPoint)socket.RemoteEndPoint!, identity);
            }
#pragma warning disable CA1031 // Any ledger failure must abort the connection unused (W5): no payload without a durable record.
            catch (Exception)
#pragma warning restore CA1031
            {
                UpstreamSockets.Abort(socket);
                return (null, "ledger-unavailable");
            }

            if (!session.TrackUpstream(connectionId, socket))
            {
                UpstreamSockets.Abort(socket);
                return (null, "revoked");
            }

            return (new UpstreamChannel(session, connectionId, socket, entry, hooks), null);
        }
        finally
        {
            session.Gate.Exit();
        }
    }

    /// <summary>
    /// Sends <paramref name="data"/> in gated chunks. Returns false (and aborts the connection) as soon as an admission
    /// is refused because revocation began; the refused buffer is discarded, never sent.
    /// </summary>
    public async ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += MaxChunkBytes)
        {
            var chunk = data.Slice(offset, Math.Min(MaxChunkBytes, data.Length - offset));
            if (_hooks is not null)
            {
                await _hooks.AtAsync(EgressHookPoint.BeforeWriteEnter, _connectionId).ConfigureAwait(false);
            }

            if (!_session.Gate.TryEnter())
            {
                Abort();
                return false;
            }

            try
            {
                if (_hooks is not null)
                {
                    await _hooks.AtAsync(EgressHookPoint.AfterWriteEnter, _connectionId).ConfigureAwait(false);
                    Interlocked.Increment(ref _hooks._sendCalls);
                }

                var sent = 0;
                while (sent < chunk.Length)
                {
                    sent += await Socket.SendAsync(chunk[sent..], SocketFlags.None, CancellationToken.None).ConfigureAwait(false);
                }

                if (_hooks is not null)
                {
                    await _hooks.AtAsync(EgressHookPoint.AfterWriteSend, _connectionId).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _session.Gate.Exit();
            }
        }

        return true;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        await Socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);

    public void Abort() => UpstreamSockets.Abort(Socket);
}
