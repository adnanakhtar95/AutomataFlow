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

/// <summary>Opens the upstream TCP connection to an already validated, pinned address.</summary>
public interface IUpstreamConnector
{
    Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}

public sealed class DirectUpstreamConnector : IUpstreamConnector
{
    public async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
