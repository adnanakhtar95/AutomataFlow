using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AutomataFlow.Automation.Network.Fencing;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>One TCP socket from a <c>sock_diag</c> dump of the supervisor's (= the unit's) network namespace.</summary>
/// <param name="Inode">The socket's inode; 0 for an orphan (no process holds a file descriptor for it any more).</param>
public sealed record DiagSocket(AddressFamily Family, IPEndPoint Local, IPEndPoint Remote, TcpConnectionState State, ulong Cookie, uint Inode, uint SendQueue, uint ReceiveQueue, uint InterfaceIndex);

/// <summary>A complete dump, or the reason it is not complete (fail closed).</summary>
public sealed record DiagDump(bool Complete, IReadOnlyList<DiagSocket> Sockets, string? Failure);

/// <summary>
/// Linux <c>NETLINK_SOCK_DIAG</c> client (docs/PHASE4_PLAN.md §7.6.5, ADR-060, ADR-075): dumps EVERY TCP socket of the
/// caller's network namespace (IPv4 and IPv6, all states) with its cookie, and destroys exactly one socket by tuple AND
/// cookie (<c>SOCK_DESTROY</c>, which needs <c>CAP_NET_ADMIN</c> over the namespace). A dump that ends with an error, is
/// truncated, or carries <c>NLM_F_DUMP_INTR</c> is reported incomplete; nothing here ever guesses.
/// </summary>
public static class SockDiag
{
    private const int AfNetlink = 16;
    private const int SockRaw = 3;
    private const int SockCloexec = 0x80000;
    private const int NetlinkSockDiag = 4;
    private const ushort SockDiagByFamily = 20;
    private const ushort SockDestroy = 21;
    private const ushort NlmsgError = 2;
    private const ushort NlmsgDone = 3;
    private const ushort NlmFRequest = 0x1;
    private const ushort NlmFAck = 0x4;
    private const ushort NlmFDump = 0x300;
    private const ushort NlmFDumpIntr = 0x10;
    private const int HeaderSize = 16;
    private const int RequestSize = 56;
    private const int DiagMessageSize = 72;
    private const uint AllStates = 0xFFFFFFFF;
    private const byte IpProtoTcp = 6;

    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EOPNOTSUPP = 95;
    public const int ESTALE = 116;

    /// <summary>Dumps every TCP socket (both families) of the current network namespace.</summary>
    public static DiagDump DumpTcp()
    {
        var sockets = new List<DiagSocket>();
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var failure = DumpFamily(family, sockets);
            if (failure is not null)
            {
                return new DiagDump(false, [], failure);
            }
        }

        return new DiagDump(true, sockets, null);
    }

    /// <summary>
    /// <c>SOCK_DESTROY</c> of exactly the socket with this tuple AND cookie. The kernel refuses a cookie that does not
    /// match the socket currently holding the tuple (<c>ESTALE</c>), so a replacement is never destroyed (P11b).
    /// Returns 0 on success or the positive errno.
    /// </summary>
    public static int Destroy(DiagSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var fd = Open(out var openError);
        if (fd < 0)
        {
            return openError;
        }

        try
        {
            var request = BuildRequest(SockDestroy, (ushort)(NlmFRequest | NlmFAck), 2, socket.Family, AllStates, socket);
            if (!Send(fd, request, out var sendError))
            {
                return sendError;
            }

            var buffer = new byte[8192];
            var received = Native.recv(fd, buffer, buffer.Length, 0);
            if (received < HeaderSize)
            {
                return received < 0 ? Marshal.GetLastPInvokeError() : EOPNOTSUPP;
            }

            var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
            return type == NlmsgError ? -BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(HeaderSize)) : EOPNOTSUPP;
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    /// <summary>Maps a destroy errno to the drain's outcome table (ADR-060 fail-closed table).</summary>
    public static DestroyOutcome ToOutcome(int errno) => errno switch
    {
        0 => DestroyOutcome.Destroyed,
        ENOENT => DestroyOutcome.Destroyed, // nothing holds the tuple any more; re-enumeration still has to show it safe
        ESTALE => DestroyOutcome.IdentityMismatch,
        EPERM => DestroyOutcome.PermissionDenied,
        EOPNOTSUPP => DestroyOutcome.Unsupported,
        _ => DestroyOutcome.Failed,
    };

    public static ConnectionIdentity Identity(DiagSocket socket) => new(ConnectionIdentityKind.LinuxSocketCookie, socket.Cookie);

    private static string? DumpFamily(AddressFamily family, List<DiagSocket> sockets)
    {
        var fd = Open(out var openError);
        if (fd < 0)
        {
            return $"netlink-open-errno-{openError}";
        }

        try
        {
            var seq = (uint)Random.Shared.Next(1, int.MaxValue);
            if (!Send(fd, BuildRequest(SockDiagByFamily, (ushort)(NlmFRequest | NlmFDump), seq, family, AllStates, null), out var sendError))
            {
                return $"netlink-send-errno-{sendError}";
            }

            var buffer = new byte[64 * 1024];
            while (true)
            {
                var received = Native.recv(fd, buffer, buffer.Length, 0);
                if (received <= 0)
                {
                    return received < 0 ? $"netlink-recv-errno-{Marshal.GetLastPInvokeError()}" : "netlink-eof";
                }

                var offset = 0;
                while (offset + HeaderSize <= (int)received)
                {
                    var span = buffer.AsSpan(offset, (int)received - offset);
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
                    var type = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
                    if (length < HeaderSize || length > span.Length)
                    {
                        return "netlink-truncated";
                    }

                    if ((flags & NlmFDumpIntr) != 0)
                    {
                        return "netlink-dump-interrupted";
                    }

                    if (type == NlmsgDone)
                    {
                        return null;
                    }

                    if (type == NlmsgError)
                    {
                        return $"netlink-error-{-BinaryPrimitives.ReadInt32LittleEndian(span[HeaderSize..])}";
                    }

                    if (type == SockDiagByFamily)
                    {
                        if (length < HeaderSize + DiagMessageSize)
                        {
                            return "netlink-short-message";
                        }

                        sockets.Add(Parse(span.Slice(HeaderSize, DiagMessageSize)));
                    }

                    offset += (length + 3) & ~3;
                }
            }
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static DiagSocket Parse(ReadOnlySpan<byte> m)
    {
        var family = m[0] == 2 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var state = Map(m[1]);
        var id = m[4..52];
        var localPort = BinaryPrimitives.ReadUInt16BigEndian(id);
        var remotePort = BinaryPrimitives.ReadUInt16BigEndian(id[2..]);
        var local = Address(family, id.Slice(4, 16));
        var remote = Address(family, id.Slice(20, 16));
        var ifIndex = BinaryPrimitives.ReadUInt32LittleEndian(id[36..]);
        var cookie = BinaryPrimitives.ReadUInt32LittleEndian(id[40..]) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(id[44..]) << 32);
        var rqueue = BinaryPrimitives.ReadUInt32LittleEndian(m[56..]);
        var wqueue = BinaryPrimitives.ReadUInt32LittleEndian(m[60..]);
        var inode = BinaryPrimitives.ReadUInt32LittleEndian(m[68..]);
        return new DiagSocket(family, new IPEndPoint(local, localPort), new IPEndPoint(remote, remotePort), state, cookie, inode, wqueue, rqueue, ifIndex);
    }

    private static IPAddress Address(AddressFamily family, ReadOnlySpan<byte> raw) =>
        family == AddressFamily.InterNetwork ? new IPAddress(raw[..4]) : new IPAddress(raw);

    /// <summary>Kernel TCP states (include/net/tcp_states.h) → the drain's normalized states.</summary>
    private static TcpConnectionState Map(byte state) => state switch
    {
        1 => TcpConnectionState.Established,
        2 => TcpConnectionState.SynSent,
        3 or 12 => TcpConnectionState.SynReceived,
        4 => TcpConnectionState.FinWait1,
        5 => TcpConnectionState.FinWait2,
        6 => TcpConnectionState.TimeWait,
        7 => TcpConnectionState.Closed,
        8 => TcpConnectionState.CloseWait,
        9 => TcpConnectionState.LastAck,
        10 => TcpConnectionState.Listen,
        11 => TcpConnectionState.Closing,
        _ => TcpConnectionState.Unknown,
    };

    private static byte[] BuildRequest(ushort type, ushort flags, uint seq, AddressFamily family, uint states, DiagSocket? target)
    {
        var message = new byte[HeaderSize + RequestSize];
        var span = message.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)message.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], seq);
        var request = span[HeaderSize..];
        request[0] = family == AddressFamily.InterNetwork ? (byte)2 : (byte)10;
        request[1] = IpProtoTcp;
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], states);
        if (target is not null)
        {
            var id = request[8..];
            BinaryPrimitives.WriteUInt16BigEndian(id, (ushort)target.Local.Port);
            BinaryPrimitives.WriteUInt16BigEndian(id[2..], (ushort)target.Remote.Port);
            target.Local.Address.GetAddressBytes().CopyTo(id[4..]);
            target.Remote.Address.GetAddressBytes().CopyTo(id[20..]);
            BinaryPrimitives.WriteUInt32LittleEndian(id[36..], target.InterfaceIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(id[40..], (uint)target.Cookie);
            BinaryPrimitives.WriteUInt32LittleEndian(id[44..], (uint)(target.Cookie >> 32));
        }

        return message;
    }

    private static int Open(out int error)
    {
        var fd = Native.socket(AfNetlink, SockRaw | SockCloexec, NetlinkSockDiag);
        error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        return fd;
    }

    private static bool Send(int fd, byte[] message, out int error)
    {
        var kernel = new byte[12]; // sockaddr_nl { AF_NETLINK, pad, nl_pid = 0 (kernel), nl_groups = 0 }
        BinaryPrimitives.WriteUInt16LittleEndian(kernel, AfNetlink);
        var sent = Native.sendto(fd, message, message.Length, 0, kernel, kernel.Length);
        error = sent == message.Length ? 0 : Marshal.GetLastPInvokeError();
        return sent == message.Length;
    }
}
