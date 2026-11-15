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
/// Regression for audit defect D1 (ADR-060 B3/E2), through the REAL proxy and barrier: data that the proxy has already
/// handed to its upstream socket's send queue must be discarded by the barrier's abortive close, never drained by a
/// graceful close in the connection handler. The upstream peer has a 4 KB receive window and does not read, so most of
/// the 128 KB stays queued in the proxy's upstream send queue with NO send outstanding (all sends returned), which is
/// exactly the state in which the old graceful close kept transmitting after Ta.
/// Platform note: relies on Linux loopback TCP semantics (send-queue accounting, RST on linger-0 close); it runs in the
/// pinned Linux verification container.
/// </summary>
public sealed class EgressRevocationQueuedDataTests
{
    private const int PayloadBytes = 128 * 1024;
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task D1_forced_order_handler_cleanup_before_B3_still_discards_queued_upstream_data_with_RST()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await RunOnceAsync(forceHandlerCleanupBeforeAbort: true, iteration);
        }
    }

    [Fact]
    public async Task D1_natural_race_queued_upstream_data_is_always_discarded_with_RST()
    {
        for (var iteration = 0; iteration < 25; iteration++)
        {
            await RunOnceAsync(forceHandlerCleanupBeforeAbort: false, iteration);
        }
    }

    private static async Task RunOnceAsync(bool forceHandlerCleanupBeforeAbort, int iteration)
    {
        await using var peer = new NonReadingPeer();
        var dns = new FakeDnsResolver();
        dns.Set("rec.test", "8.8.8.8");
        var connector = new LargeSendBufferConnector(peer.Endpoint);
        using var ledger = TestLedgers.Create();
        await using var proxy = new EgressProxyServer(
            Options.Create(new EgressProxyOptions { RevocationTimeoutSeconds = 10 }), dns, connector, ledger, new OsConnectionIdentityProvider(), TimeProvider.System, NullLogger<EgressProxyServer>.Instance);
        proxy.Start();
        var session = proxy.Sessions.Register(Guid.NewGuid(), new EgressPolicy([AllowedHostRule.Parse("https://rec.test")]));

        if (forceHandlerCleanupBeforeAbort)
        {
            // Hold B3 until the connection handler has completely unwound (its finally ran and its lease was released).
            // This is the ordering in which the old graceful close beat the barrier's abortive close.
            proxy.Hooks = new EgressTestHooks
            {
                OnPoint = async (point, _) =>
                {
                    if (point == EgressHookPoint.BeforeAbort)
                    {
                        await WaitUntilAsync(() => session.ActiveConnections == 0, "handler cleanup before B3");
                    }
                },
            };
        }

        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Username}:{session.Password}"));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT rec.test:443 HTTP/1.1\r\nHost: rec.test:443\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
        Assert.Equal(200, await ReadStatusAsync(stream));
        await peer.Accepted.WaitAsync(Long);

        await stream.WriteAsync(new byte[PayloadBytes]);

        // Every byte was accepted by the proxy's upstream sends and no send is outstanding: the data is QUEUED in the
        // upstream socket, not in flight in a syscall. (If a send blocked, the barrier would correctly time out: WG4.)
        await WaitUntilAsync(() => session.BytesTransferred >= PayloadBytes && session.Gate.Admitted == 0, "all upstream sends returned");
        await Task.Delay(100);
        Assert.Equal(0, session.Gate.Admitted);

        var result = await session.RevokeAsync().WaitAsync(Long);
        Assert.True(result == RevocationResult.Completed, $"iteration {iteration}: barrier {result}");

        var (received, reset) = await peer.DrainAsync(Long);

        Assert.True(received < PayloadBytes, $"iteration {iteration}: peer received {received} of {PayloadBytes} bytes; queued data must be discarded, not drained");
        Assert.True(reset, $"iteration {iteration}: peer must observe RST (abortive close), not FIN");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Long)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
        }
    }

    private static async Task<int> ReadStatusAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Long);
        var head = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cts.Token) == 1)
        {
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        return int.Parse(text.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Connects the proxy's upstream sockets with a 1 MB send buffer so that 128 KB of sends complete into the queue.</summary>
    private sealed class LargeSendBufferConnector(IPEndPoint target) : IUpstreamConnector
    {
        public async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendBufferSize = 1024 * 1024 };
            try
            {
                await socket.ConnectAsync(target, cancellationToken);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>An upstream peer with a 4 KB receive window that reads nothing until asked to drain.</summary>
    private sealed class NonReadingPeer : IAsyncDisposable
    {
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly TaskCompletionSource<Socket> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NonReadingPeer()
        {
            _listener.ReceiveBufferSize = 4096; // inherited by the accepted socket: a small receive window
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen();
            _ = Task.Run(async () =>
            {
                try
                {
                    _accepted.TrySetResult(await _listener.AcceptAsync());
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    _accepted.TrySetException(ex);
                }
            });
        }

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndPoint!;

        public Task Accepted => _accepted.Task;

        public async Task<(int Received, bool Reset)> DrainAsync(TimeSpan timeout)
        {
            var socket = await _accepted.Task;
            var buffer = new byte[64 * 1024];
            var received = 0;
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                int read;
                while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token)) > 0)
                {
                    received += read;
                }

                return (received, false); // orderly FIN: the queued data was drained (the D1 failure mode)
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                return (received, true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_accepted.Task.IsCompletedSuccessfully)
            {
                (await _accepted.Task).Dispose();
            }

            _listener.Dispose();
        }
    }
}
