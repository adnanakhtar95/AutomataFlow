using System.Net.Sockets;
using System.Text;
using AutomataFlow.Application.Network;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.AutomationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.Proxy;

/// <summary>
/// F15 (docs/PHASE4_PLAN.md §7.5.5, R6): proxy credentials exist only in the memory of the process hosting the proxy. A
/// new proxy instance (a replacement Worker incarnation) has no way to accept credentials from another instance.
/// </summary>
public sealed class ProxyStateNotPersistedTests
{
    [Fact]
    public async Task F15_a_new_proxy_instance_refuses_the_credentials_of_a_previous_one()
    {
        var dns = new FakeDnsResolver();
        dns.Set("f15.test", "8.8.8.8");
        using var ledger = TestLedgers.Create();
        string username;
        string password;
        await using (var first = NewProxy(dns, ledger))
        {
            first.Start();
            var session = first.Sessions.Register(Guid.NewGuid(), new EgressPolicy([AllowedHostRule.Parse("https://f15.test")]));
            (username, password) = (session.Username, session.Password);
        }

        await using var second = NewProxy(dns, ledger);
        second.Start();

        Assert.Equal(407, await ConnectStatusAsync(second, username, password));
    }

    private static EgressProxyServer NewProxy(FakeDnsResolver dns, FileConnectionLedger ledger) =>
        new(Options.Create(new EgressProxyOptions()), dns, new DirectUpstreamConnector(), ledger, new OsConnectionIdentityProvider(), TimeProvider.System, NullLogger<EgressProxyServer>.Instance);

    private static async Task<int> ConnectStatusAsync(EgressProxyServer proxy, string username, string password)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Endpoint!);
        var stream = client.GetStream();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT f15.test:443 HTTP/1.1\r\nHost: f15.test:443\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
        var buffer = new byte[12];
        var read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return int.Parse(Encoding.ASCII.GetString(buffer, 9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }
}
