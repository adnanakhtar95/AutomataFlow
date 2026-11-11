using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Automation.Network.Proxy;

public sealed class EgressProxyOptions
{
    public const string SectionName = "Automation:EgressProxy";

    public int HeaderTimeoutSeconds { get; set; } = 10;

    public int DnsTimeoutSeconds { get; set; } = 5;

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public int IdleTimeoutSeconds { get; set; } = 60;

    public int MaxTunnelLifetimeSeconds { get; set; } = 600;

    public int MaxConnectionsPerSession { get; set; } = 32;

    public long MaxBytesPerSession { get; set; } = 200L * 1024 * 1024;

    public int MaxTotalConnections { get; set; } = 512;

    public int MaxHeaderBytes { get; set; } = 32 * 1024;
}

public enum EgressProxyState
{
    NotStarted,
    Running,
    Stopped,
}

/// <summary>
/// Authenticating forward proxy that every browser request must pass through (ADR-030, ADR-036).
/// For each connection it authenticates the browser session, applies that session's <see cref="EgressPolicy"/>,
/// resolves the host itself, rejects the destination if ANY resolved address is private/loopback/link-local/metadata
/// (unless the matching rule explicitly allows private networks in Development), and connects to the validated
/// address it resolved (pinning: the host is never re-resolved for that connection).
/// In-process mode (127.0.0.1) is policy enforcement, not a hard network boundary (ADR-031).
/// </summary>
public sealed class EgressProxyServer : IAsyncDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Authenticate", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    private readonly EgressProxyOptions _options;
    private readonly IDnsResolver _dns;
    private readonly IUpstreamConnector _connector;
    private readonly TimeProvider _time;
    private readonly ILogger<EgressProxyServer> _logger;
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private long _nextConnectionId;
    private int _totalConnections;

    public EgressProxyServer(
        IOptions<EgressProxyOptions> options,
        IDnsResolver dns,
        IUpstreamConnector connector,
        TimeProvider time,
        ILogger<EgressProxyServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _dns = dns;
        _connector = connector;
        _time = time;
        _logger = logger;
    }

    public EgressSessionRegistry Sessions { get; } = new();

    public EgressProxyState State { get; private set; }

    /// <summary>Loopback endpoint the browser is configured to use (available once started).</summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>Starts listening on an ephemeral loopback port. A stopped proxy cannot be restarted (fail-closed).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (State == EgressProxyState.Running)
            {
                return;
            }

            if (State == EgressProxyState.Stopped)
            {
                throw new InvalidOperationException("The egress proxy has been stopped and cannot be restarted.");
            }

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Endpoint = (IPEndPoint)_listener.LocalEndpoint;
            State = EgressProxyState.Running;
            _acceptLoop = AcceptLoopAsync(_stopping.Token);
            _logger.LogInformation("Egress proxy listening on {Endpoint}", Endpoint);
        }
    }

    /// <summary>True only if the proxy is running AND accepts a TCP connection right now.</summary>
    public async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (State != EgressProxyState.Running || Endpoint is null)
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var probe = new TcpClient();
        try
        {
            await probe.ConnectAsync(Endpoint, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (State == EgressProxyState.Stopped)
            {
                return;
            }

            State = EgressProxyState.Stopped;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _logger.LogInformation("Egress proxy stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (Interlocked.Increment(ref _totalConnections) > _options.MaxTotalConnections)
            {
                Interlocked.Decrement(ref _totalConnections);
                client.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextConnectionId);
            _clients[id] = client;
            _ = Task.Run(() => HandleClientAsync(id, client, stop), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(long connectionId, TcpClient client, CancellationToken stop)
    {
        EgressSession? session = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(_options.HeaderTimeoutSeconds));
            var (head, leftover, headError) = await ReadHeadAsync(stream, _options.MaxHeaderBytes, headerTimeout.Token).ConfigureAwait(false);
            if (head is null)
            {
                if (headError == "too-large")
                {
                    await WriteResponseAsync(stream, 431, "Request Header Fields Too Large", EgressReasons.BadRequest, stop).ConfigureAwait(false);
                }

                return;
            }

            if (!ProxyRequestParser.TryParse(head, out var request, out var parseError))
            {
                var status = parseError == "method-not-allowed" ? 405 : 400;
                await WriteResponseAsync(stream, status, status == 405 ? "Method Not Allowed" : "Bad Request", status == 405 ? EgressReasons.MethodNotAllowed : EgressReasons.BadRequest, stop).ConfigureAwait(false);
                return;
            }

            var (user, password) = ParseBasicCredentials(request!.GetHeader("Proxy-Authorization"));
            session = Sessions.Authenticate(user, password);
            if (session is null)
            {
                await WriteResponseAsync(stream, 407, "Proxy Authentication Required", EgressReasons.Unauthenticated, stop, proxyAuthenticate: true).ConfigureAwait(false);
                return;
            }

            if (!session.TryOpenConnection(_options.MaxConnectionsPerSession))
            {
                Record(session, false, EgressReasons.TooManyConnections, request.Method, "-", null);
                await WriteResponseAsync(stream, 503, "Service Unavailable", EgressReasons.TooManyConnections, stop).ConfigureAwait(false);
                session = null; // not tracked, nothing to release
                return;
            }

            session.Track(connectionId, client);
            await HandleRequestAsync(session, request, stream, leftover, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Connection closed, timed out, or proxy stopping.
        }
        finally
        {
            session?.CloseConnection(connectionId);
            _clients.TryRemove(connectionId, out _);
            Interlocked.Decrement(ref _totalConnections);
            client.Dispose();
        }
    }

    private async Task HandleRequestAsync(EgressSession session, ProxyRequest request, NetworkStream client, byte[] leftover, CancellationToken stop)
    {
        string rawHost;
        int port;
        string? ruleScheme;
        string? pathAndQuery = null;
        long contentLength = 0;

        if (request.IsConnect)
        {
            if (!ProxyRequestParser.TryParseAuthority(request.Target, out rawHost, out port))
            {
                await DenyAsync(session, request, client, 400, "Bad Request", EgressReasons.BadRequest, "-", null, stop).ConfigureAwait(false);
                return;
            }

            ruleScheme = null;
        }
        else
        {
            if (!TryParseAbsoluteHttpTarget(request.Target, out rawHost, out port, out pathAndQuery))
            {
                await DenyAsync(session, request, client, 400, "Bad Request", EgressReasons.BadRequest, "-", null, stop).ConfigureAwait(false);
                return;
            }

            if (request.CountHeader("Transfer-Encoding") > 0)
            {
                await DenyAsync(session, request, client, 501, "Not Implemented", EgressReasons.NotImplemented, "-", null, stop).ConfigureAwait(false);
                return;
            }

            if (!HostHeaderMatches(request, rawHost, port) || !TryGetContentLength(request, out contentLength))
            {
                await DenyAsync(session, request, client, 400, "Bad Request", EgressReasons.BadRequest, "-", null, stop).ConfigureAwait(false);
                return;
            }

            ruleScheme = "http";
        }

        if (!HostNameNormalizer.TryNormalize(rawHost, out var host, out var hostError))
        {
            await DenyAsync(session, request, client, 403, "Forbidden", $"{EgressReasons.InvalidHost}:{hostError}", "-", null, stop).ConfigureAwait(false);
            return;
        }

        var target = FormatTarget(host!, port);
        var rule = session.Policy.FindRule(host!.Value, port, ruleScheme);
        if (rule is null)
        {
            await DenyAsync(session, request, client, 403, "Forbidden", EgressReasons.HostNotAllowed, target, null, stop).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<IPAddress> addresses;
        if (host.Literal is { } literal)
        {
            addresses = [literal];
        }
        else
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(_options.DnsTimeoutSeconds));
            try
            {
                addresses = await _dns.ResolveAsync(host.Value, dnsTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                addresses = [];
            }
        }

        if (addresses.Count == 0)
        {
            await DenyAsync(session, request, client, 502, "Bad Gateway", EgressReasons.DnsFailure, target, null, stop).ConfigureAwait(false);
            return;
        }

        // Every resolved address must be acceptable; a mixed public/private answer is rejected as a whole.
        foreach (var address in addresses)
        {
            if (IpAddressPolicy.GetDenyReason(address) is { } ipReason && !rule.AllowPrivateNetwork)
            {
                var reason = addresses.Count > 1 && addresses.Any(IpAddressPolicy.IsAllowed)
                    ? $"{EgressReasons.MixedDnsAnswer}:{ipReason}"
                    : ipReason;
                await DenyAsync(session, request, client, 403, "Forbidden", reason, target, address.ToString(), stop).ConfigureAwait(false);
                return;
            }
        }

        // Pin: connect to exactly the address that was validated. IPv4-mapped IPv6 is connected as IPv4.
        var pinned = addresses[0].IsIPv4MappedToIPv6 ? addresses[0].MapToIPv4() : addresses[0];
        Stream upstream;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
            try
            {
                upstream = await _connector.ConnectAsync(new IPEndPoint(pinned, port), connectTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !stop.IsCancellationRequested)
            {
                await DenyAsync(session, request, client, 502, "Bad Gateway", EgressReasons.UpstreamUnreachable, target, pinned.ToString(), stop).ConfigureAwait(false);
                return;
            }
        }

        await using (upstream.ConfigureAwait(false))
        {
            Record(session, true, "allowed", request.Method, target, pinned.ToString());

            if (request.IsConnect)
            {
                await client.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop).ConfigureAwait(false);
                if (leftover.Length > 0)
                {
                    await upstream.WriteAsync(leftover, stop).ConfigureAwait(false);
                }

                await RelayAsync(session, request.Method, target, client, upstream, bidirectional: true, stop).ConfigureAwait(false);
                return;
            }

            // Plain HTTP: forward exactly one request in origin-form with Connection: close, then stream the response.
            var forwarded = new StringBuilder()
                .Append(request.Method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            foreach (var header in request.Headers)
            {
                if (!HopByHopHeaders.Contains(header.Key) && !header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                {
                    forwarded.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            forwarded.Append("Connection: close\r\n\r\n");
            await upstream.WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()), stop).ConfigureAwait(false);
            await CopyBodyAsync(client, upstream, leftover, contentLength, stop).ConfigureAwait(false);
            await RelayAsync(session, request.Method, target, upstream, client, bidirectional: false, stop).ConfigureAwait(false);
        }
    }

    private async Task RelayAsync(EgressSession session, string method, string target, Stream a, Stream b, bool bidirectional, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lifetime.CancelAfter(TimeSpan.FromSeconds(_options.MaxTunnelLifetimeSeconds));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var idleTimeout = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
        idle.CancelAfter(idleTimeout);

        var forward = PumpAsync(session, method, target, a, b, idle, idleTimeout);
        var backward = bidirectional ? PumpAsync(session, method, target, b, a, idle, idleTimeout) : null;
        if (backward is null)
        {
            await forward.ConfigureAwait(false);
            return;
        }

        await Task.WhenAny(forward, backward).ConfigureAwait(false);
        await idle.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(forward, backward).ConfigureAwait(false);
    }

    private async Task PumpAsync(EgressSession session, string method, string target, Stream from, Stream to, CancellationTokenSource idle, TimeSpan idleTimeout)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (!session.AddBytes(read, _options.MaxBytesPerSession))
                {
                    Record(session, false, EgressReasons.QuotaExceeded, method, target, null);
                    return;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                idle.CancelAfter(idleTimeout);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Tunnel closed, idle, lifetime exceeded, or proxy stopping.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyBodyAsync(Stream client, Stream upstream, byte[] leftover, long contentLength, CancellationToken stop)
    {
        var fromLeftover = (int)Math.Min(leftover.Length, contentLength);
        if (fromLeftover > 0)
        {
            await upstream.WriteAsync(leftover.AsMemory(0, fromLeftover), stop).ConfigureAwait(false);
        }

        var remaining = contentLength - fromLeftover;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (remaining > 0)
            {
                var read = await client.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stop).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await upstream.WriteAsync(buffer.AsMemory(0, read), stop).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DenyAsync(EgressSession session, ProxyRequest request, Stream client, int status, string phrase, string reason, string target, string? address, CancellationToken stop)
    {
        Record(session, false, reason, request.Method, target, address);
        await WriteResponseAsync(client, status, phrase, reason, stop).ConfigureAwait(false);
    }

    private void Record(EgressSession session, bool allowed, string reason, string method, string target, string? address)
    {
        var egressEvent = new EgressEvent(_time.GetUtcNow(), session.SessionId, allowed, reason, method, target, address);
        session.Record(egressEvent);
        if (allowed)
        {
            _logger.LogDebug("Egress allowed {Method} {Target} via {Address} for session {SessionId}", method, target, address, session.SessionId);
        }
        else
        {
            _logger.LogWarning("Egress denied {Method} {Target} ({Reason}, address {Address}) for session {SessionId}", method, target, reason, address, session.SessionId);
        }
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string phrase, string reason, CancellationToken cancellationToken, bool proxyAuthenticate = false)
    {
        var body = Encoding.UTF8.GetBytes($"Blocked by AutomataFlow egress policy ({reason}).");
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(phrase).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-AutomataFlow-Egress-Denied: ").Append(reason).Append("\r\n");
        if (proxyAuthenticate)
        {
            head.Append("Proxy-Authenticate: Basic realm=\"automataflow-egress\"\r\n");
        }

        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Connection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Client already gone.
        }
    }

    private static async Task<(byte[]? Head, byte[] Leftover, string? Error)> ReadHeadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        var searchFrom = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (null, [], "closed");
            }

            total += read;
            var end = buffer.AsSpan(0, total).Slice(searchFrom).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                end += searchFrom;
                return (buffer[..end], buffer[(end + 4)..total], null);
            }

            searchFrom = Math.Max(0, total - 3);
        }

        return (null, [], "too-large");
    }

    private static (string? User, string? Password) ParseBasicCredentials(string? header)
    {
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return colon <= 0 ? (null, null) : (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    /// <summary>Accepts only absolute-form <c>http://</c> targets; https must use CONNECT.</summary>
    private static bool TryParseAbsoluteHttpTarget(string target, out string host, out int port, out string pathAndQuery)
    {
        host = string.Empty;
        port = 0;
        pathAndQuery = "/";
        const string prefix = "http://";
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = target[prefix.Length..];
        var slash = rest.IndexOfAny(['/', '?', '#']);
        var authority = slash < 0 ? rest : rest[..slash];
        if (slash >= 0)
        {
            pathAndQuery = rest[slash] == '/' ? rest[slash..] : "/" + rest[slash..];
            var hash = pathAndQuery.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                pathAndQuery = pathAndQuery[..hash];
            }
        }

        if (authority.Length == 0 || authority.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        if (ProxyRequestParser.TryParseAuthority(authority, out host, out port))
        {
            return true;
        }

        if (authority.Contains(':', StringComparison.Ordinal) && !(authority.StartsWith('[') && authority.EndsWith(']')))
        {
            return false;
        }

        host = authority;
        port = 80;
        return true;
    }

    private static bool HostHeaderMatches(ProxyRequest request, string targetHost, int targetPort)
    {
        if (request.CountHeader("Host") != 1)
        {
            return false;
        }

        var header = request.GetHeader("Host")!;
        string headerHost;
        int headerPort;
        if (!ProxyRequestParser.TryParseAuthority(header, out headerHost, out headerPort))
        {
            headerHost = header;
            headerPort = 80;
        }

        return headerPort == targetPort && string.Equals(headerHost, targetHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetContentLength(ProxyRequest request, out long contentLength)
    {
        contentLength = 0;
        var count = request.CountHeader("Content-Length");
        if (count == 0)
        {
            return true;
        }

        var value = request.GetHeader("Content-Length")!;
        return count == 1
            && value.Length is > 0 and <= 12
            && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
    }

    private static string FormatTarget(NormalizedHost host, int port) =>
        (host.Literal?.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host.Value}]" : host.Value) + ":" + port.ToString(CultureInfo.InvariantCulture);
}
