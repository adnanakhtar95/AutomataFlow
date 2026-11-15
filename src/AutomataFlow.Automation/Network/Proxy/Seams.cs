using System.Net;
using System.Net.Sockets;

namespace AutomataFlow.Automation.Network.Proxy;

/// <summary>Name resolution used by the egress proxy. The browser itself never resolves names (ADR-036).</summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

/// <summary>
/// Opens the upstream TCP connection to an already validated, pinned address. Returns the connected socket itself:
/// the proxy needs it for the connection ledger (OS identity), the fail-fast options and the abortive close of the
/// revocation barrier (ADR-060). Connecting sends no payload, so it may be cancelled at any time.
/// </summary>
public interface IUpstreamConnector
{
    Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}

public sealed class DirectUpstreamConnector : IUpstreamConnector
{
    public async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
