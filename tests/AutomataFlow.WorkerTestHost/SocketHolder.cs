using System.Net;
using System.Net.Sockets;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.Infrastructure;
using AutomataFlow.Worker;
using Microsoft.Extensions.Options;

/// <summary>
/// TEST-ONLY (ADR-075, P7/P8/P10/P11c/P11f): a unit process with a REAL upstream connection, run by the real launcher inside
/// a real unit. It performs the real containment handshake, registers a real incarnation, appends the connection to a real
/// ledger with its real socket cookie BEFORE any payload (W5), then keeps sending (the peer decides the window), and
/// heartbeats until it is killed. Variants: <c>wrong-cookie</c> ledgers another cookie for the tuple (P11c),
/// <c>corrupt-ledger</c> damages a record that is not the last one (P10).
/// Usage: socket-holder &lt;host&gt; &lt;port&gt; &lt;normal|wrong-cookie|corrupt-ledger&gt;
/// </summary>
internal static class SocketHolder
{
    public static async Task<int> RunAsync(string host, int port, string variant)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var containment = configuration.GetSection(ContainmentOptions.SectionName).Get<ContainmentOptions>() ?? new ContainmentOptions();
        var ledgerDirectory = configuration["Automation:EgressProxy:LedgerDirectory"]!;
        var unit = await new LinuxNamespaceContainmentHandshake(Options.Create(containment), Options.Create(new EgressProxyOptions { LedgerDirectory = ledgerDirectory }), TimeProvider.System)
            .VerifyAsync(CancellationToken.None);

        var services = new ServiceCollection().AddLogging().AddAttemptJournal(configuration).BuildServiceProvider();
        var journal = services.GetRequiredService<IAttemptJournal>();
        var ledger = new FileConnectionLedger(ledgerDirectory, Guid.NewGuid());
        var (pid, started) = HostIdentity.SelfProcess();
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), HostIdentity.HostId(), HostIdentity.BootId(), pid, started, unit, ledger.Path);
        await journal.RegisterIncarnationAsync(incarnation, CancellationToken.None);

        var address = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(address, port);
        if (!new OsConnectionIdentityProvider().TryGetIdentity(socket, out var identity, out var failure))
        {
            throw new InvalidOperationException("No socket identity: " + failure);
        }

        var recorded = variant == "wrong-cookie" ? identity with { Value = identity.Value + 1_000_003 } : identity;
        var entry = ledger.Append(Guid.NewGuid(), (IPEndPoint)socket.LocalEndPoint!, (IPEndPoint)socket.RemoteEndPoint!, recorded);
        if (variant == "corrupt-ledger")
        {
            ledger.Dispose();
            var valid = File.ReadAllLines(ledger.Path)[0];
            await File.AppendAllTextAsync(ledger.Path, valid[..^4] + "dead\n" + valid.Replace("v1|1|", "v1|2|", StringComparison.Ordinal) + "\n");
        }

        Console.WriteLine($"HOLDER-READY incarnation={incarnation.Id.Value} cookie={identity.Value} local={entry.Local} ledger={ledger.Path}");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1_000);
                try
                {
                    await journal.HeartbeatAsync(incarnation.Id, CancellationToken.None);
                }
                catch (AttemptJournalException)
                {
                }
            }
        });

        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');
        while (true)
        {
            await socket.SendAsync(chunk); // blocks once the peer's window and our send buffer are full
        }
    }
}
