using System.Net;
using System.Net.Sockets;
using System.Text;
using AutomataFlow.Automation.Network;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.AutomationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.Proxy;

/// <summary>The egress proxy over raw sockets: no browser involved, every decision observable.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class EgressProxyTests(BrowserFixture fixture) : IAsyncLifetime, IDisposable
{
    private static readonly string[] HostileNames =
        ["private.test", "loop.test", "metadata.test", "v6loop.test", "v6ula.test", "v6ll.test", "mapped.test", "nat64.test", "cgnat.test", "mixed.test", "rebind.test"];

    private readonly FakeDnsResolver _dns = new();
    private readonly RecordingConnector _connector = new();
    private TcpListener _echo = null!;
    private CancellationTokenSource _echoStop = new();

    public async Task InitializeAsync()
    {
        _dns.Set("public.test", "8.8.8.8");
        _dns.Set("echo.test", "8.8.8.8");
        _dns.Set("private.test", "10.0.0.5");
        _dns.Set("loop.test", "127.0.0.1");
        _dns.Set("metadata.test", "169.254.169.254");
        _dns.Set("v6loop.test", "::1");
        _dns.Set("v6ula.test", "fd00::1");
        _dns.Set("v6ll.test", "fe80::1");
        _dns.Set("mapped.test", "::ffff:127.0.0.1");
        _dns.Set("nat64.test", "64:ff9b::7f00:1");
        _dns.Set("cgnat.test", "100.64.1.1");
        _dns.Set("mixed.test", "8.8.4.4", "192.168.1.10");
        _dns.SetSequence("rebind.test", ["8.8.8.8"], ["127.0.0.1"]);

        _echo = new TcpListener(IPAddress.Loopback, 0);
        _echo.Start();
        _ = EchoLoopAsync();
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), (IPEndPoint)_echo.LocalEndpoint);
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), new IPEndPoint(IPAddress.Loopback, fixture.Servers.PublicPort));
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _echoStop.CancelAsync();
        _echo.Stop();
    }

    public void Dispose()
    {
        _echoStop.Dispose();
        _echo.Dispose();
    }

    private EgressProxyServer CreateProxy(Action<EgressProxyOptions>? configure = null)
    {
        var options = new EgressProxyOptions();
        configure?.Invoke(options);
        var proxy = new EgressProxyServer(Options.Create(options), _dns, _connector, TimeProvider.System, NullLogger<EgressProxyServer>.Instance);
        proxy.Start();
        return proxy;
    }

    private static EgressPolicy AllHostsPolicy() => new(
    [
        AllowedHostRule.Parse("https://echo.test"),
        AllowedHostRule.Parse("http://public.test"),
        AllowedHostRule.Parse("https://public.test"),
        .. HostileNames.Select(h => AllowedHostRule.Parse($"https://{h}")),
        AllowedHostRule.Parse("https://127.0.0.1"),
        AllowedHostRule.Parse("https://[::1]"),
        AllowedHostRule.Parse("https://10.0.0.5"),
        AllowedHostRule.Parse("https://[::ffff:127.0.0.1]"),
        AllowedHostRule.Parse("http://169.254.169.254"),
    ]);

    private static string Auth(EgressSession session) =>
        "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Username}:{session.Password}"));

    private static async Task<(int Status, string Head, TcpClient Client, NetworkStream Stream)> SendAsync(EgressProxyServer proxy, string head)
    {
        var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "\r\n\r\n"));
        var response = await ReadHeadAsync(stream);
        var status = response.Length > 12 ? int.Parse(response.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (status, response, client, stream);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cts.Token) == 1)
        {
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private async Task EchoLoopAsync()
    {
        while (!_echoStop.IsCancellationRequested)
        {
            try
            {
                var c = await _echo.AcceptTcpClientAsync(_echoStop.Token);
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buffer = new byte[8192];
                        int read;
                        try
                        {
                            while ((read = await s.ReadAsync(buffer)) > 0)
                            {
                                await s.WriteAsync(buffer.AsMemory(0, read));
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task Requests_without_credentials_get_407_with_a_challenge()
    {
        await using var proxy = CreateProxy();

        var (status, head, client, _) = await SendAsync(proxy, "CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443");
        client.Dispose();

        Assert.Equal(407, status);
        Assert.Contains("Proxy-Authenticate: Basic", head, StringComparison.Ordinal);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task Wrong_or_revoked_credentials_get_407()
    {
        await using var proxy = CreateProxy();
        var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());
        var wrong = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Username}:not-the-password"));

        var (wrongStatus, _, c1, _) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{wrong}");
        session.Dispose();
        var (revokedStatus, _, c2, _) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");
        c1.Dispose();
        c2.Dispose();

        Assert.Equal(407, wrongStatus);
        Assert.Equal(407, revokedStatus);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task Allowed_connect_tunnels_to_the_pinned_public_address_and_relays_bytes()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, _, client, stream) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");
        using (client)
        {
            await stream.WriteAsync("ping"u8.ToArray());
            var buffer = new byte[4];
            await stream.ReadExactlyAsync(buffer);

            Assert.Equal(200, status);
            Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
        }

        var decision = Assert.Single(session.Events);
        Assert.True(decision.Allowed);
        Assert.Equal(("echo.test:443", "8.8.8.8"), (decision.Target, decision.Address));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), Assert.Single(_connector.Attempts));
    }

    [Theory]
    [InlineData("CONNECT other.test:443", "host-not-allowed")]
    [InlineData("CONNECT echo.test:8443", "host-not-allowed")]
    [InlineData("CONNECT echo.test:22", "host-not-allowed")]
    public async Task Hosts_and_ports_outside_the_policy_are_denied(string requestLine, string reason)
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, head, client, _) = await SendAsync(proxy, $"{requestLine} HTTP/1.1\r\nHost: x\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains($"X-AutomataFlow-Egress-Denied: {reason}", head, StringComparison.Ordinal);
        Assert.Empty(_connector.Attempts);
    }

    [Theory]
    [InlineData("private.test", "ipv4-private")]
    [InlineData("loop.test", "ipv4-loopback")]
    [InlineData("metadata.test", "ipv4-link-local-or-metadata")]
    [InlineData("cgnat.test", "ipv4-shared-cgnat")]
    [InlineData("v6loop.test", "ipv6-loopback")]
    [InlineData("v6ula.test", "ipv6-unique-local")]
    [InlineData("v6ll.test", "ipv6-link-or-site-local")]
    [InlineData("mapped.test", "ipv4-mapped-ipv4-loopback")]
    [InlineData("nat64.test", "ipv6-nat64-ipv4-loopback")]
    public async Task Allowed_names_that_resolve_to_internal_addresses_are_denied_without_connecting(string host, string reason)
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, head, client, _) = await SendAsync(proxy, $"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains(reason, head, StringComparison.Ordinal);
        Assert.Empty(_connector.Attempts);
        Assert.False(Assert.Single(session.Events).Allowed);
    }

    [Theory]
    [InlineData("CONNECT 127.0.0.1:443", "ipv4-loopback")]
    [InlineData("CONNECT [::1]:443", "ipv6-loopback")]
    [InlineData("CONNECT 10.0.0.5:443", "ipv4-private")]
    [InlineData("CONNECT [::ffff:7f00:1]:443", "ipv4-mapped-ipv4-loopback")]
    [InlineData("GET http://169.254.169.254/latest/meta-data/", "ipv4-link-local-or-metadata")]
    public async Task Internal_ip_literals_are_denied_even_when_a_rule_names_them(string requestLine, string reason)
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());
        var hostHeader = requestLine.StartsWith("GET", StringComparison.Ordinal) ? "169.254.169.254" : "x";

        var (status, head, client, _) = await SendAsync(proxy, $"{requestLine} HTTP/1.1\r\nHost: {hostHeader}\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains(reason, head, StringComparison.Ordinal);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task Mixed_public_and_private_dns_answers_are_denied_as_a_whole()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, head, client, _) = await SendAsync(proxy, $"CONNECT mixed.test:443 HTTP/1.1\r\nHost: mixed.test:443\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Contains("mixed-dns-answer:ipv4-private", head, StringComparison.Ordinal);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task Dns_rebinding_never_results_in_a_connection_to_the_rebound_address()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());
        _connector.Map(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), (IPEndPoint)_echo.LocalEndpoint);

        var (first, _, c1, _) = await SendAsync(proxy, $"CONNECT rebind.test:443 HTTP/1.1\r\nHost: rebind.test:443\r\n{Auth(session)}");
        var (second, head, c2, _) = await SendAsync(proxy, $"CONNECT rebind.test:443 HTTP/1.1\r\nHost: rebind.test:443\r\n{Auth(session)}");
        c1.Dispose();
        c2.Dispose();

        Assert.Equal(200, first);   // first answer: public, pinned
        Assert.Equal(403, second);  // second answer: rebound to loopback, denied
        Assert.Contains("ipv4-loopback", head, StringComparison.Ordinal);
        Assert.Equal(2, _dns.LookupCount("rebind.test"));
        Assert.All(_connector.Attempts, endpoint => Assert.Equal(IPAddress.Parse("8.8.8.8"), endpoint.Address));
    }

    [Fact]
    public async Task Unresolvable_names_fail_with_502()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), new EgressPolicy([AllowedHostRule.Parse("https://nxdomain.test")]));

        var (status, head, client, _) = await SendAsync(proxy, $"CONNECT nxdomain.test:443 HTTP/1.1\r\nHost: nxdomain.test:443\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(502, status);
        Assert.Contains("dns-failure", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_is_forwarded_once_with_connection_close_and_without_proxy_credentials()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, head, client, stream) = await SendAsync(proxy, $"GET http://public.test/headers HTTP/1.1\r\nHost: public.test\r\nX-Probe: 1\r\n{Auth(session)}");
        using (client)
        {
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync();

            Assert.True(status == 200, $"Expected 200 but got {status}: {head}{body}");
            Assert.Contains("X-Probe: 1", body, StringComparison.Ordinal);
            Assert.Contains("Connection: close", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Proxy-Authorization", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(session.Password, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Plain_http_is_refused_for_https_only_rules()
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), EgressPolicy.Create("https://public.test"));

        var (status, _, client, _) = await SendAsync(proxy, $"GET http://public.test/ HTTP/1.1\r\nHost: public.test\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(403, status);
        Assert.Empty(_connector.Attempts);
    }

    [Theory]
    [InlineData("GET https://public.test/ HTTP/1.1\r\nHost: public.test", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost: public.test", 400)]
    [InlineData("GET http://public.test/ HTTP/1.1\r\nHost: evil.test", 400)]
    [InlineData("GET http://public.test/ HTTP/1.1\r\nHost: public.test\r\nHost: public.test", 400)]
    [InlineData("POST http://public.test/ HTTP/1.1\r\nHost: public.test\r\nContent-Length: 1\r\nContent-Length: 2", 400)]
    [InlineData("POST http://public.test/ HTTP/1.1\r\nHost: public.test\r\nTransfer-Encoding: chunked", 501)]
    [InlineData("GET http://user:pw@public.test/ HTTP/1.1\r\nHost: public.test", 400)]
    [InlineData("TRACE http://public.test/ HTTP/1.1\r\nHost: public.test", 405)]
    [InlineData("CONNECT public.test HTTP/1.1\r\nHost: public.test", 400)]
    public async Task Ambiguous_or_smuggling_shaped_requests_are_rejected_without_connecting(string head, int expectedStatus)
    {
        await using var proxy = CreateProxy();
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, _, client, _) = await SendAsync(proxy, $"{head}\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(expectedStatus, status);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task Oversized_request_heads_are_rejected()
    {
        await using var proxy = CreateProxy(o => o.MaxHeaderBytes = 1024);
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, _, client, _) = await SendAsync(proxy, $"GET http://public.test/ HTTP/1.1\r\nHost: public.test\r\nX-Big: {new string('a', 4000)}\r\n{Auth(session)}");
        client.Dispose();

        Assert.Equal(431, status);
    }

    [Fact]
    public async Task Slow_header_clients_are_disconnected()
    {
        await using var proxy = CreateProxy(o => o.HeaderTimeoutSeconds = 1);
        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT echo.test:443 HTTP/1.1\r\n"u8.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(new byte[16], cts.Token);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Per_session_byte_quota_closes_the_tunnel()
    {
        await using var proxy = CreateProxy(o => o.MaxBytesPerSession = 1000);
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (status, _, client, stream) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");
        using (client)
        {
            await stream.WriteAsync(new byte[5000]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var total = 0;
            int read;
            var buffer = new byte[8192];
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                total += read;
            }

            Assert.Equal(200, status);
            Assert.True(total < 5000, $"Expected the tunnel to be cut before 5000 bytes, got {total}.");
        }

        Assert.Contains(session.Events, e => e.Reason == EgressReasons.QuotaExceeded);
    }

    [Fact]
    public async Task Per_session_connection_limit_is_enforced()
    {
        await using var proxy = CreateProxy(o => o.MaxConnectionsPerSession = 1);
        using var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());

        var (first, _, c1, _) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");
        var (second, _, c2, _) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");
        c1.Dispose();
        c2.Dispose();

        Assert.Equal(200, first);
        Assert.Equal(503, second);
    }

    [Fact]
    public async Task Revoking_a_session_closes_its_open_tunnels()
    {
        await using var proxy = CreateProxy();
        var session = proxy.Sessions.Register(Guid.NewGuid(), AllHostsPolicy());
        var (status, _, client, stream) = await SendAsync(proxy, $"CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n{Auth(session)}");

        session.Dispose();
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var read = await stream.ReadAsync(new byte[16], cts.Token);

            Assert.Equal(200, status);
            Assert.Equal(0, read);
        }
    }

    [Fact]
    public async Task A_stopped_proxy_refuses_connections_and_cannot_be_restarted()
    {
        var proxy = CreateProxy();
        var endpoint = proxy.Endpoint!;

        await proxy.StopAsync();

        Assert.False(await proxy.ProbeAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(endpoint));
        Assert.Throws<InvalidOperationException>(proxy.Start);
        await proxy.DisposeAsync();
    }
}
