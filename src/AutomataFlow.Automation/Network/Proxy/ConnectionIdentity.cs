using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AutomataFlow.Automation.Network.Proxy;

public enum ConnectionIdentityKind
{
    /// <summary>Linux socket cookie (<c>SO_COOKIE</c>), the kernel's per-socket identifier.</summary>
    LinuxSocketCookie = 1,

    /// <summary>Windows <c>liCreateTimestamp</c> of the connection's owner-module TCP row (FILETIME).</summary>
    WindowsCreateTimestamp = 2,
}

/// <summary>OS-level identity of one upstream TCP connection (docs/PHASE4_PLAN.md §7.6.5, ADR-060).</summary>
public readonly record struct ConnectionIdentity(ConnectionIdentityKind Kind, ulong Value)
{
    public override string ToString() => $"{Kind}:{Value}";
}

/// <summary>Reads the OS identity of a connected socket. Anything unreadable, zero or ambiguous fails (fail closed).</summary>
public interface IConnectionIdentityProvider
{
    bool TryGetIdentity(Socket socket, out ConnectionIdentity identity, out string? failure);
}

/// <summary>
/// Linux: <c>getsockopt(SOL_SOCKET, SO_COOKIE)</c>. Windows: the connection's row in
/// <c>GetExtendedTcpTable(TCP_TABLE_OWNER_MODULE_ALL)</c>, matched by local AND remote endpoint AND this process's PID;
/// exactly one row with a non-zero creation timestamp is required. Every other platform fails closed.
/// </summary>
public sealed class OsConnectionIdentityProvider : IConnectionIdentityProvider
{
    private const int SolSocket = 1;
    private const int SoCookie = 57;

    public bool TryGetIdentity(Socket socket, out ConnectionIdentity identity, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(socket);
        identity = default;
        failure = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> value = stackalloc byte[8];
                var length = socket.GetRawSocketOption(SolSocket, SoCookie, value);
                var cookie = length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(value) : 0UL;
                if (cookie == 0)
                {
                    failure = "socket-cookie-unavailable";
                    return false;
                }

                identity = new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, cookie);
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsTcpTable.TryGetCreateTimestamp(socket, out identity, out failure);
            }

            failure = "unsupported-platform";
            return false;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException or InvalidOperationException)
        {
            failure = "identity-read-failed";
            return false;
        }
    }
}

/// <summary>Windows owner-module TCP table access (read-only; the platform never terminates connections on Windows).</summary>
internal static class WindowsTcpTable
{
    private const int TableOwnerModuleAll = 8;
    private const int ErrorInsufficientBuffer = 122;
    private const int Ipv4RowSize = 160;
    private const int Ipv6RowSize = 192;

    public static bool TryGetCreateTimestamp(Socket socket, out ConnectionIdentity identity, out string? failure)
    {
        identity = default;
        if (socket.LocalEndPoint is not IPEndPoint local || socket.RemoteEndPoint is not IPEndPoint remote)
        {
            failure = "endpoints-unavailable";
            return false;
        }

        var rows = Read(local.AddressFamily, out failure);
        if (rows is null)
        {
            return false;
        }

        var pid = (uint)Environment.ProcessId;
        var matches = rows.Where(r => r.OwningPid == pid && r.Local.Equals(Normalize(local)) && r.Remote.Equals(Normalize(remote))).ToList();
        if (matches.Count != 1)
        {
            failure = matches.Count == 0 ? "tcp-row-not-found" : "tcp-row-ambiguous";
            return false;
        }

        if (matches[0].CreateTimestamp == 0)
        {
            failure = "tcp-row-without-timestamp";
            return false;
        }

        identity = new ConnectionIdentity(ConnectionIdentityKind.WindowsCreateTimestamp, matches[0].CreateTimestamp);
        failure = null;
        return true;
    }

    internal sealed record Row(IPEndPoint Local, IPEndPoint Remote, int State, uint OwningPid, ulong CreateTimestamp);

    /// <summary>The complete table for one address family, or null (with a reason) if it cannot be read completely.</summary>
    internal static List<Row>? Read(AddressFamily family, out string? failure)
    {
        failure = null;
        if (!OperatingSystem.IsWindows())
        {
            failure = "unsupported-platform";
            return null;
        }

        var af = family == AddressFamily.InterNetworkV6 ? 23 : 2;
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, af, TableOwnerModuleAll, 0);
                if (result == ErrorInsufficientBuffer || buffer == IntPtr.Zero)
                {
                    size += 4096; // the table can grow between calls; retry with headroom
                    continue;
                }

                if (result != 0)
                {
                    failure = "tcp-table-error-" + result.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return null;
                }

                return Parse(buffer, af == 23);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        failure = "tcp-table-incomplete";
        return null;
    }

    private static List<Row> Parse(IntPtr table, bool ipv6)
    {
        var count = Marshal.ReadInt32(table);
        var rows = new List<Row>(count);
        var rowSize = ipv6 ? Ipv6RowSize : Ipv4RowSize;
        var first = table + 8; // DWORD dwNumEntries, rows aligned to 8 bytes
        for (var i = 0; i < count; i++)
        {
            var row = first + (i * rowSize);
            if (ipv6)
            {
                var localAddress = new IPAddress(ReadBytes(row, 0, 16), (uint)Marshal.ReadInt32(row, 16));
                var localPort = Port(Marshal.ReadInt32(row, 20));
                var remoteAddress = new IPAddress(ReadBytes(row, 24, 16), (uint)Marshal.ReadInt32(row, 40));
                var remotePort = Port(Marshal.ReadInt32(row, 44));
                var state = Marshal.ReadInt32(row, 48);
                var pid = (uint)Marshal.ReadInt32(row, 52);
                var created = (ulong)Marshal.ReadInt64(row, 56);
                rows.Add(new Row(new IPEndPoint(localAddress, localPort), new IPEndPoint(remoteAddress, remotePort), state, pid, created));
            }
            else
            {
                var state = Marshal.ReadInt32(row, 0);
                var localAddress = new IPAddress(ReadBytes(row, 4, 4));
                var localPort = Port(Marshal.ReadInt32(row, 8));
                var remoteAddress = new IPAddress(ReadBytes(row, 12, 4));
                var remotePort = Port(Marshal.ReadInt32(row, 16));
                var pid = (uint)Marshal.ReadInt32(row, 20);
                var created = (ulong)Marshal.ReadInt64(row, 24);
                rows.Add(new Row(new IPEndPoint(localAddress, localPort), new IPEndPoint(remoteAddress, remotePort), state, pid, created));
            }
        }

        return rows;
    }

    private static byte[] ReadBytes(IntPtr row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        return bytes;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
}

internal static class NativeMethods
{
#pragma warning disable SYSLIB1054 // DllImport keeps the Automation project free of AllowUnsafeBlocks; marshalling is blittable.
    [DllImport("iphlpapi.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
#pragma warning restore SYSLIB1054
}
