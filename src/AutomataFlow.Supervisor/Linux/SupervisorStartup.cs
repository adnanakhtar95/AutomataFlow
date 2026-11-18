using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AutomataFlow.Automation.Network.Fencing;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>
/// Fail-closed supervisor startup (ADR-075). Every problem is reported; with any problem the handshake refuses, so the
/// Worker never starts. There is no override.
/// </summary>
public static class SupervisorStartup
{
    public const ulong CapNetAdmin = 1UL << 12;

    /// <summary>The supervisor's own credentials: its own uid, CAP_NET_ADMIN and nothing else, no_new_privs.</summary>
    public static IReadOnlyList<string> CheckCredentials(ProcStatus self, int workerUid)
    {
        ArgumentNullException.ThrowIfNull(self);
        var problems = new List<string>();
        if (self.Uids.Count == 0 || self.Uids.Distinct().Count() != 1 || self.Uids[0] == 0 || self.Uids[0] == workerUid)
        {
            problems.Add("The supervisor must run under its own non-root uid, distinct from the Worker's.");
        }

        if (self.CapEff != CapNetAdmin || self.CapPrm != CapNetAdmin || self.CapBnd != CapNetAdmin || (self.CapInh & ~CapNetAdmin) != 0 || (self.CapAmb & ~CapNetAdmin) != 0)
        {
            problems.Add("The supervisor must hold CAP_NET_ADMIN and no other capability (effective, permitted and bounding sets).");
        }

        if (!self.NoNewPrivs)
        {
            problems.Add("no_new_privs is not set for the supervisor.");
        }

        return problems;
    }

    /// <summary>
    /// Behavioural proof that the approved seccomp profile (Docker's default plus the Chromium <c>clone</c>/<c>unshare</c>
    /// rule, ADR-035) is enforced. <c>/proc/self/status</c> cannot tell: Docker Desktop attaches a filter even with
    /// <c>seccomp=unconfined</c>. The profile denies <c>keyctl</c> (the kernel keyring is not namespaced) with EPERM, so the
    /// harmless read-only <c>keyctl(KEYCTL_GET_KEYRING_ID, session keyring, create = 0)</c> must fail with EPERM; unconfined
    /// it returns a keyring id or ENOKEY. Null when enforced; otherwise the reason (the handshake then refuses).
    /// </summary>
    public static string? ProbeSeccompProfile()
    {
        nint keyctl = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 250,
            Architecture.Arm64 => 219,
            _ => 0,
        };
        if (keyctl == 0)
        {
            return "seccomp-probe-unsupported-architecture";
        }

        const int KeyctlGetKeyringId = 0;
        const int KeySpecSessionKeyring = -3;
        var result = Native.syscall(keyctl, KeyctlGetKeyringId, KeySpecSessionKeyring, 0);
        var errno = Marshal.GetLastPInvokeError();
        return result == -1 && errno == SockDiag.EPERM ? null : "the approved seccomp profile is not enforced (keyctl was not denied)";
    }

    /// <summary>Makes the supervisor non-dumpable: no other process of any uid may ptrace or read its memory.</summary>
    public static string? MakeNonDumpable() =>
        Native.prctl(Native.PR_SET_DUMPABLE, 0, 0, 0, 0) == 0 ? null : $"PR_SET_DUMPABLE failed (errno {Marshal.GetLastPInvokeError()}).";

    /// <summary>
    /// Live proof of destroy-by-exact-cookie on THIS kernel and with THIS process's capability (P9, P11b at every start): a
    /// same-tuple replacement must survive a destroy carrying the stale cookie, and the exact cookie must destroy it, as
    /// re-enumeration shows. Null when proven; otherwise the reason (the handshake then refuses).
    /// </summary>
    public static string? ProbeExactCookieDestroy()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var localPort = FreePort();
            ulong stale;
            using (var first = Connect(port, localPort))
            {
                stale = Cookie(first);
                first.LingerState = new LingerOption(true, 0); // abortive close: the tuple is free immediately
            }

            using var replacement = Connect(port, localPort);
            var fresh = Cookie(replacement);
            var dump = SockDiag.DumpTcp();
            if (!dump.Complete)
            {
                return "probe-dump-incomplete:" + dump.Failure;
            }

            var current = dump.Sockets.SingleOrDefault(s => s.Local.Port == localPort && s.Remote.Port == port && s.Cookie == fresh);
            if (current is null)
            {
                return "probe-socket-not-enumerated";
            }

            var staleResult = SockDiag.Destroy(current with { Cookie = stale });
            if (staleResult == 0 || !Alive(fresh))
            {
                return "probe-stale-cookie-destroyed-a-replacement";
            }

            var exact = SockDiag.Destroy(current);
            if (exact != 0)
            {
                return exact == SockDiag.EPERM ? "destroy-permission-denied" : exact == SockDiag.EOPNOTSUPP ? "destroy-unsupported" : $"probe-destroy-errno-{exact}";
            }

            return Alive(fresh) ? "probe-destroy-silently-skipped" : null;
        }
        catch (SocketException)
        {
            return "probe-socket-error";
        }
    }

    private static bool Alive(ulong cookie) =>
        SockDiag.DumpTcp().Sockets.Any(s => s.Cookie == cookie && !ConnectionDrain.IsSafe(s.State));

    private static Socket Connect(int port, int localPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, localPort));
        socket.Connect(IPAddress.Loopback, port);
        return socket;
    }

    private static ulong Cookie(Socket socket)
    {
        Span<byte> value = stackalloc byte[8];
        return socket.GetRawSocketOption(1, 57, value) == 8 ? BitConverter.ToUInt64(value) : 0;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
