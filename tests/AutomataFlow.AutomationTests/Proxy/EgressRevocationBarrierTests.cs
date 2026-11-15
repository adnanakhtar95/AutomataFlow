using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AutomataFlow.Application.Network;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.AutomationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.Proxy;

/// <summary>
/// The egress revocation barrier, write gate, connection ledger and OS identity over raw sockets
/// (docs/PHASE4_PLAN.md §7.6, §13 V1–V12 and WG1–WG4, ADR-060). Every interleaving is forced with deterministic hooks.
/// </summary>
public sealed class EgressRevocationBarrierTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private readonly FakeDnsResolver _dns = new();
    private readonly RecordingConnector _connector = new();
    private readonly RecordingUpstream _upstream = new();
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async Task InitializeAsync()
    {
        _dns.Set("rec.test", "8.8.8.8");
        await _upstream.StartAsync();
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), _upstream.Endpoint);
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), _upstream.Endpoint);
    }

    public async Task DisposeAsync()
    {
        foreach (var item in _cleanup)
        {
            await item.DisposeAsync();
        }

        await _upstream.DisposeAsync();
    }

    public void Dispose() => _upstream.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private (EgressProxyServer Proxy, FileConnectionLedger Ledger) CreateProxy(int revocationSeconds = 5)
    {
        var ledger = TestLedgers.Create();
        var options = new EgressProxyOptions { RevocationTimeoutSeconds = revocationSeconds };
        var proxy = new EgressProxyServer(Options.Create(options), _dns, _connector, ledger, new OsConnectionIdentityProvider(), TimeProvider.System, NullLogger<EgressProxyServer>.Instance);
        proxy.Start();
        _cleanup.Add(proxy);
        return (proxy, ledger);
    }

    private static EgressSession Register(EgressProxyServer proxy) =>
        proxy.Sessions.Register(Guid.NewGuid(), new EgressPolicy([AllowedHostRule.Parse("https://rec.test"), AllowedHostRule.Parse("http://rec.test")]));

    private static string Auth(EgressSession session) =>
        "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Username}:{session.Password}"));

    private static async Task<(int Status, TcpClient Client, NetworkStream Stream)> ConnectTunnelAsync(EgressProxyServer proxy, EgressSession session)
    {
        var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT rec.test:443 HTTP/1.1\r\nHost: rec.test:443\r\n{Auth(session)}\r\n\r\n"));
        return (await ReadStatusAsync(stream), client, stream);
    }

    private static async Task<int> ReadStatusAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Long);
        var head = new List<byte>();
        var one = new byte[1];
        try
        {
            while (await stream.ReadAsync(one, cts.Token) == 1)
            {
                head.Add(one[0]);
                if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            // connection closed while reading
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        return text.Length > 12 ? int.Parse(text.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Hooks that pause the FIRST occurrence of a point until released.</summary>
    private static (EgressTestHooks Hooks, TaskCompletionSource Paused, TaskCompletionSource Release) PauseAt(EgressHookPoint point)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        var hooks = new EgressTestHooks
        {
            OnPoint = async (p, _) =>
            {
                if (p == point && Interlocked.Exchange(ref once, 1) == 0)
                {
                    paused.TrySetResult();
                    await release.Task;
                }
            },
        };
        return (hooks, paused, release);
    }

    // ---------- V1: no new request with revoked credentials ----------

    [Fact]
    public async Task V1_after_a_completed_barrier_new_connections_with_the_old_credentials_get_407_and_never_reach_upstream()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        Assert.Equal(RevocationResult.Completed, await session.RevokeAsync());
        var attempts = _connector.Attempts.Count;

        var (connectStatus, c1, _) = await ConnectTunnelAsync(proxy, session);
        using var c2 = new TcpClient();
        await c2.ConnectAsync(proxy.Endpoint!);
        var s2 = c2.GetStream();
        await s2.WriteAsync(Encoding.ASCII.GetBytes($"GET http://rec.test/x HTTP/1.1\r\nHost: rec.test\r\n{Auth(session)}\r\n\r\n"));
        var plainStatus = await ReadStatusAsync(s2);
        c1.Dispose();

        Assert.Equal(407, connectStatus);
        Assert.Equal(407, plainStatus);
        Assert.Equal(attempts, _connector.Attempts.Count);
    }

    // ---------- V2: admission race (authenticate -> revoke -> admit) ----------

    [Fact]
    public async Task V2_admission_racing_revocation_never_leaves_a_live_connection_or_an_upstream_byte()
    {
        var (proxy, _) = CreateProxy();
        for (var i = 0; i < 1_000; i++)
        {
            var session = Register(proxy);
            var revokeBetween = i % 2 == 0;
            Task<RevocationResult>? revocation = null;
            proxy.Hooks = new EgressTestHooks
            {
                OnPoint = (p, _) =>
                {
                    if (p == EgressHookPoint.AfterAuthenticate && revokeBetween)
                    {
                        revocation = session.RevokeAsync(); // B1 lands between authentication and admission
                    }

                    return Task.CompletedTask;
                },
            };

            var (status, client, _) = await ConnectTunnelAsync(proxy, session);
            using (client)
            {
                revocation ??= session.RevokeAsync();
                Assert.Equal(RevocationResult.Completed, await revocation);
                Assert.Equal(revokeBetween ? 407 : 200, status);
                Assert.Equal(0, session.ActiveConnections);
            }
        }

        proxy.Hooks = null;
        Assert.Equal(0, _upstream.TotalPayloadBytes);
    }

    // ---------- V3: established keep-alive tunnel ----------

    [Fact]
    public async Task V3_a_keep_alive_tunnel_carries_nothing_after_revocation_and_both_sides_are_closed()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (status, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("REQUEST-1"u8.ToArray());
            await _upstream.WaitForPayloadAsync("REQUEST-1", Long);

            Assert.Equal(RevocationResult.Completed, await session.RevokeAsync());
            var revoked = Stopwatch.GetTimestamp();

            // The browser keeps writing into the same (now dead) tunnel.
            try
            {
                await stream.WriteAsync("REQUEST-2"u8.ToArray());
            }
            catch (IOException)
            {
                // expected: the browser side was closed by the barrier
            }

            using var cts = new CancellationTokenSource(Long);
            var read = 0;
            try
            {
                read = await stream.ReadAsync(new byte[16], cts.Token);
            }
            catch (IOException)
            {
                read = 0;
            }

            await Task.Delay(300);
            Assert.Equal(200, status);
            Assert.Equal(0, read);
            Assert.Equal("REQUEST-1", _upstream.Payload);
            Assert.True(_upstream.ClosedAt(0) is { } closed && closed <= Stopwatch.GetTimestamp(), "upstream connection must be closed");
            Assert.True(_upstream.LastPayloadAt(0) <= revoked, "no payload after the barrier");
        }
    }

    // ---------- V5: queued plain-HTTP request ----------

    [Fact]
    public async Task V5_a_queued_plain_http_request_is_never_connected_or_sent_after_revocation()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (hooks, paused, release) = PauseAt(EgressHookPoint.BeforeOpen);
        proxy.Hooks = hooks;
        var attempts = _connector.Attempts.Count;

        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST http://rec.test/submit HTTP/1.1\r\nHost: rec.test\r\nContent-Length: 5\r\n{Auth(session)}\r\n\r\nhello"));
        await paused.Task.WaitAsync(Long);

        var revocation = session.RevokeAsync();
        await Task.Delay(200);
        Assert.False(revocation.IsCompleted, "B4 must wait for the paused handler");
        release.TrySetResult();

        Assert.Equal(RevocationResult.Completed, await revocation.WaitAsync(Long));
        Assert.Equal(attempts, _connector.Attempts.Count);
        Assert.Equal(0, _upstream.ConnectionCount);
        Assert.Equal(0, hooks.SendCalls);
    }

    // ---------- WG1 (= the review-round-8 interleaving, admitted case) ----------

    [Fact]
    public async Task WG1_a_write_admitted_before_revocation_completes_before_the_sockets_are_aborted()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (hooks, paused, release) = PauseAt(EgressHookPoint.AfterWriteEnter);
        proxy.Hooks = hooks; // before connecting: the channel captures the hooks when it is created
        var (status, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("ADMITTED-PAYLOAD"u8.ToArray());
            await paused.Task.WaitAsync(Long); // admitted (TryEnter succeeded), paused right before the send syscall

            var revocation = session.RevokeAsync();
            await Task.Delay(300);

            Assert.False(revocation.IsCompleted, "B2 must wait for the admitted write");
            Assert.True(session.IsRevoked, "B1 happened");
            Assert.Null(_upstream.ClosedAt(0));                // B3 has not aborted anything yet
            Assert.Equal(0, _upstream.TotalPayloadBytes);      // the send has not run yet

            release.TrySetResult();
            Assert.Equal(RevocationResult.Completed, await revocation.WaitAsync(Long));

            await _upstream.WaitForPayloadAsync("ADMITTED-PAYLOAD", Long);
            Assert.Equal(200, status);
            Assert.Equal(1, hooks.SendCalls);
            var closed = await _upstream.WaitForCloseAsync(0, Long);
            Assert.True(_upstream.LastPayloadAt(0) <= closed, "the admitted bytes arrived before the abort");
        }
    }

    // ---------- WG2 / V6 (the review-round-8 interleaving, not-admitted case) ----------

    [Fact]
    public async Task WG2_a_write_that_reaches_admission_after_revocation_issues_no_send_at_all()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (hooks, paused, release) = PauseAt(EgressHookPoint.BeforeWriteEnter);
        proxy.Hooks = hooks; // before connecting: the channel captures the hooks when it is created
        var (status, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("LATE-PAYLOAD"u8.ToArray());
            await paused.Task.WaitAsync(Long); // buffer read from the browser, paused BEFORE asking for admission

            var revocation = session.RevokeAsync();
            await _upstream.WaitForCloseAsync(0, Long); // B1-B3 ran: revoked and aborted while the handler is paused
            Assert.False(revocation.IsCompleted, "B4 waits for the paused handler");

            release.TrySetResult();
            Assert.Equal(RevocationResult.Completed, await revocation.WaitAsync(Long));

            await Task.Delay(300);
            Assert.Equal(200, status);
            Assert.Equal(0, hooks.SendCalls);
            Assert.Equal(0, _upstream.TotalPayloadBytes);
        }
    }

    // ---------- WG3 ----------

    [Fact]
    public async Task WG3_a_write_paused_after_its_send_holds_the_barrier_until_it_releases_admission()
    {
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (hooks, paused, release) = PauseAt(EgressHookPoint.AfterWriteSend);
        proxy.Hooks = hooks; // before connecting: the channel captures the hooks when it is created
        var (_, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("SENT-PAYLOAD"u8.ToArray());
            await paused.Task.WaitAsync(Long);
            await _upstream.WaitForPayloadAsync("SENT-PAYLOAD", Long);

            var revocation = session.RevokeAsync();
            await Task.Delay(300);
            Assert.False(revocation.IsCompleted);
            Assert.Null(_upstream.ClosedAt(0));

            release.TrySetResult();
            Assert.Equal(RevocationResult.Completed, await revocation.WaitAsync(Long));
            await _upstream.WaitForCloseAsync(0, Long);
        }
    }

    // ---------- WG4 / V8: an admitted write that never finishes ----------

    [Fact]
    public async Task WG4_an_admitted_write_still_outstanding_at_the_deadline_makes_the_barrier_time_out()
    {
        var (proxy, _) = CreateProxy(revocationSeconds: 1);
        var session = Register(proxy);
        var (hooks, paused, release) = PauseAt(EgressHookPoint.AfterWriteEnter);
        proxy.Hooks = hooks; // before connecting: the channel captures the hooks when it is created
        var (_, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("STUCK"u8.ToArray());
            await paused.Task.WaitAsync(Long);

            var result = await session.RevokeAsync();

            Assert.Equal(RevocationResult.TimedOut, result); // never a fence while a send is outstanding
            await _upstream.WaitForCloseAsync(0, Long);     // B3 still aborted the socket (defence in depth)
            release.TrySetResult();
        }
    }

    // ---------- V7: abortive close discards the unsent send queue (E2), verified on this kernel ----------

    [Fact]
    public async Task V7_abortive_close_discards_data_still_in_the_send_queue_and_resets_the_peer()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.ReceiveBufferSize = 4096; // inherited by the accepted socket: a small receive window
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { SendBufferSize = 1024 * 1024 };
        await sender.ConnectAsync(listener.LocalEndPoint!);
        using var receiver = await listener.AcceptAsync();

        // The peer does not read yet, so most of this stays in OUR send queue (no send outstanding: SendAsync completes).
        var sent = 0;
        var chunk = new byte[16 * 1024];
        for (var i = 0; i < 8; i++)
        {
            sent += await sender.SendAsync(chunk, SocketFlags.None);
        }

        UpstreamSockets.Abort(sender);

        var received = 0;
        var reset = false;
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(Long);
        try
        {
            int read;
            while ((read = await receiver.ReceiveAsync(buffer, SocketFlags.None, cts.Token)) > 0)
            {
                received += read;
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            reset = true;
        }

        Assert.Equal(128 * 1024, sent);
        Assert.True(received < sent, $"received {received} of {sent}: an abortive close must not deliver the queued data");
        Assert.True(reset, "the peer must observe RST");
    }

    // ---------- V12: destroyed is not delivered (E2 != E3) ----------

    [Fact]
    public async Task V12_socket_destruction_does_not_prove_delivery_bytes_in_the_network_can_arrive_after_the_fence()
    {
        await using var relay = new DelayingRelay(_upstream.Endpoint, TimeSpan.FromSeconds(1));
        await relay.StartAsync();
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), relay.Endpoint);
        var (proxy, _) = CreateProxy();
        var session = Register(proxy);
        var (_, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("IN-FLIGHT"u8.ToArray());
            await relay.WaitForBytesAsync(9, Long); // our TCP handed the bytes on and they were acknowledged

            Assert.Equal(RevocationResult.Completed, await session.RevokeAsync());
            var fence = Stopwatch.GetTimestamp();
            Assert.Equal(0, session.ActiveConnections); // E2: our connection is destroyed

            await _upstream.WaitForPayloadAsync("IN-FLIGHT", Long);
            Assert.True(_upstream.FirstPayloadAt(0) > fence, "the target received the request AFTER the fence (E3 is never claimed)");
        }
    }

    // ---------- V10 + W5: fail-fast options, OS identity, ledger before payload ----------

    [Fact]
    public async Task V10_W5_upstream_connections_are_ledgered_with_their_OS_identity_before_any_payload()
    {
        var (proxy, ledger) = CreateProxy();
        var session = Register(proxy);
        ConnectionLedgerEntry? ledgeredBeforeFirstWrite = null;
        proxy.Hooks = new EgressTestHooks
        {
            OnPoint = (p, _) =>
            {
                if (p == EgressHookPoint.AfterWriteEnter && ledgeredBeforeFirstWrite is null)
                {
                    var read = ConnectionLedgerReader.Read(ledger.Path);
                    ledgeredBeforeFirstWrite = read.Entries.Count > 0 ? read.Entries[^1] : null;
                }

                return Task.CompletedTask;
            },
        };
        var (_, client, stream) = await ConnectTunnelAsync(proxy, session);
        using (client)
        {
            await stream.WriteAsync("PAYLOAD"u8.ToArray());
            await _upstream.WaitForPayloadAsync("PAYLOAD", Long);
        }

        Assert.NotNull(ledgeredBeforeFirstWrite);
        Assert.Equal(session.SessionId, ledgeredBeforeFirstWrite!.SessionId);
        Assert.Equal(_upstream.Endpoint, ledgeredBeforeFirstWrite.Remote);
        Assert.NotEqual(0UL, ledgeredBeforeFirstWrite.Identity.Value);
        Assert.Equal(
            OperatingSystem.IsWindows() ? ConnectionIdentityKind.WindowsCreateTimestamp : ConnectionIdentityKind.LinuxSocketCookie,
            ledgeredBeforeFirstWrite.Identity.Kind);
    }

    [Fact]
    public async Task V10_fail_fast_socket_options_are_configured_and_identities_are_unique_per_connection()
    {
        var connector = new DirectUpstreamConnector();
        using var a = await connector.ConnectAsync(_upstream.Endpoint, CancellationToken.None);
        using var b = await connector.ConnectAsync(_upstream.Endpoint, CancellationToken.None);
        UpstreamSockets.ApplyFailFast(a, 30);
        var identities = new OsConnectionIdentityProvider();

        Assert.True(identities.TryGetIdentity(a, out var idA, out var failureA), failureA);
        Assert.True(identities.TryGetIdentity(b, out var idB, out var failureB), failureB);

        Assert.Equal(OperatingSystem.IsWindows() ? 30 : 30_000, UpstreamSockets.ReadFailFast(a));
        Assert.NotEqual(0UL, idA.Value);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public async Task The_identity_of_a_closed_socket_cannot_be_read_and_fails_closed()
    {
        var connector = new DirectUpstreamConnector();
        var socket = await connector.ConnectAsync(_upstream.Endpoint, CancellationToken.None);
        socket.Dispose();

        Assert.False(new OsConnectionIdentityProvider().TryGetIdentity(socket, out _, out var failure));
        Assert.NotNull(failure);
    }

    /// <summary>An upstream server that records every received byte and every close with a timestamp, per connection.</summary>
    private sealed class RecordingUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<int, Connection> _connections = new();
        private int _next = -1;

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

        public int ConnectionCount => _connections.Count;

        public int TotalPayloadBytes => _connections.Values.Sum(c => c.Bytes);

        public string Payload => string.Concat(_connections.OrderBy(c => c.Key).Select(c => c.Value.Text));

        public long? ClosedAt(int connection) => _connections.TryGetValue(connection, out var c) ? c.ClosedAt : null;

        public long LastPayloadAt(int connection) => _connections[connection].LastPayloadAt;

        public long FirstPayloadAt(int connection) => _connections[connection].FirstPayloadAt;

        public Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    var connection = new Connection();
                    _connections[Interlocked.Increment(ref _next)] = connection;
                    _ = connection.RunAsync(client, _stop.Token);
                }
            });
            return Task.CompletedTask;
        }

        public async Task WaitForPayloadAsync(string text, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!Payload.Contains(text, StringComparison.Ordinal))
            {
                if (clock.Elapsed > timeout)
                {
                    throw new TimeoutException($"upstream never received '{text}' (got '{Payload}')");
                }

                await Task.Delay(20);
            }
        }

        public async Task<long> WaitForCloseAsync(int connection, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (ClosedAt(connection) is { } closed)
                {
                    return closed;
                }

                if (clock.Elapsed > timeout)
                {
                    throw new TimeoutException("upstream connection was never closed");
                }

                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _stop.CancelAsync();
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
        }

        private int _disposed;

        private sealed class Connection
        {
            private readonly StringBuilder _text = new();

            public int Bytes { get; private set; }

            public string Text
            {
                get
                {
                    lock (_text)
                    {
                        return _text.ToString();
                    }
                }
            }

            public long FirstPayloadAt { get; private set; }

            public long LastPayloadAt { get; private set; }

            public long? ClosedAt { get; private set; }

            public async Task RunAsync(TcpClient client, CancellationToken stop)
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer, stop)) > 0)
                        {
                            var now = Stopwatch.GetTimestamp();
                            lock (_text)
                            {
                                _text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }

                            if (Bytes == 0)
                            {
                                FirstPayloadAt = now;
                            }

                            Bytes += read;
                            LastPayloadAt = now;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        // reset / closed
                    }

                    ClosedAt = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    /// <summary>
    /// Accepts and acknowledges bytes at once, then forwards them after a delay: from our TCP's point of view the
    /// bytes have left (they were acknowledged), yet the target receives them later, as with packets still in the network.
    /// </summary>
    private sealed class DelayingRelay(IPEndPoint target, TimeSpan delay) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _received;

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

        public Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var buffer = new byte[8192];
                    var collected = new MemoryStream();
                    var stream = inbound.GetStream();
                    var readUntil = Task.Delay(delay, _stop.Token);
                    var reading = Task.Run(async () =>
                    {
                        try
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                            {
                                lock (collected)
                                {
                                    collected.Write(buffer, 0, read);
                                }

                                Interlocked.Add(ref _received, read);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                        {
                            // our side reset the connection: the bytes we already hold are "in the network"
                        }
                    });
                    await readUntil;
                    using var outbound = new TcpClient();
                    await outbound.ConnectAsync(target, _stop.Token);
                    byte[] held;
                    lock (collected)
                    {
                        held = collected.ToArray();
                    }

                    await outbound.GetStream().WriteAsync(held, _stop.Token);
                    await reading.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or TimeoutException)
                {
                    // test teardown
                }
            });
            return Task.CompletedTask;
        }

        public async Task WaitForBytesAsync(int count, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _received) < count)
            {
                if (clock.Elapsed > timeout)
                {
                    throw new TimeoutException("relay never received the bytes");
                }

                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
