using System.Net;
using AutomataFlow.Automation.Network.Fencing;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Automation;

/// <summary>
/// The supervisor's socket drain decision logic (docs/PHASE4_PLAN.md §7.6.5, ADR-060) against scripted socket tables:
/// partial drains, port reuse, identity ambiguity and every supervisor failure path must fail closed. The OS readers
/// and the Linux destroyer are Phase 5; their behaviour is scripted here.
/// </summary>
public class ConnectionDrainTests
{
    private static readonly IPEndPoint Local = IPEndPoint.Parse("10.0.0.2:40000");
    private static readonly IPEndPoint Remote = IPEndPoint.Parse("93.184.216.34:443");
    private static readonly ConnectionIdentity Cookie = new(ConnectionIdentityKind.LinuxSocketCookie, 77);
    private static readonly ConnectionIdentity Timestamp = new(ConnectionIdentityKind.WindowsCreateTimestamp, 133_400_000_000_000_000);
    private static readonly DrainPolicy FastPolicy = new(MaxPolls: 5, PollInterval: TimeSpan.Zero, MaxDumpRetries: 2);

    private static LedgerReadResult Ledger(ConnectionIdentity identity, bool tornTail = false) =>
        new(LedgerReadStatus.Ok, [new ConnectionLedgerEntry(1, Guid.NewGuid(), Local.AddressFamily, Local, Remote, identity)], tornTail, null);

    private static SocketTableSnapshot Table(params ObservedSocket[] sockets) => new(true, sockets);

    private static ObservedSocket Socket(TcpConnectionState state, ConnectionIdentity? identity, bool liveOwner = false, IPEndPoint? local = null) =>
        new(local ?? Local, Remote, state, identity, liveOwner);

    private static Task<DrainReport> Run(DrainPlatform platform, LedgerReadResult ledger, ScriptedTable table, ScriptedDestroyer? destroyer) =>
        new ConnectionDrain(table, destroyer, FastPolicy, _ => Task.CompletedTask).RunAsync(platform, ledger);

    // ---------- Linux (dedicated namespace, destroy by exact cookie) ----------

    [Fact]
    public async Task Linux_all_sockets_safe_is_drained_without_destroying_anything()
    {
        var destroyer = new ScriptedDestroyer();
        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.TimeWait, Cookie))), destroyer);

        Assert.True(report.IsDrained);
        Assert.Empty(destroyer.Calls);
    }

    [Fact]
    public async Task Linux_a_lingering_transmit_capable_socket_is_destroyed_by_cookie_and_verified_before_drained()
    {
        var destroyer = new ScriptedDestroyer();
        var table = new ScriptedTable(
            Table(Socket(TcpConnectionState.Established, Cookie)),
            Table()); // after the destroy, the re-enumeration shows nothing left

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), table, destroyer);

        Assert.True(report.IsDrained);
        Assert.Equal(1, report.SocketsDestroyed);
        Assert.Equal(Cookie, Assert.Single(destroyer.Calls));
        Assert.Equal(2, report.Polls);
    }

    [Fact]
    public async Task Linux_unledgered_transmit_capable_sockets_in_the_namespace_are_destroyed_too()
    {
        var loopback = new ObservedSocket(IPEndPoint.Parse("127.0.0.1:5000"), IPEndPoint.Parse("127.0.0.1:41000"), TcpConnectionState.CloseWait, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 99));
        var destroyer = new ScriptedDestroyer();

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(loopback), Table()), destroyer);

        Assert.True(report.IsDrained);
        Assert.Single(destroyer.Calls);
    }

    [Fact]
    public async Task Linux_a_socket_held_by_a_live_foreign_process_is_never_destroyed_and_never_drained()
    {
        // ADR-075: the container's namespace is shared with the supervisor; any OTHER live owner is ambiguous.
        var foreign = new ObservedSocket(IPEndPoint.Parse("10.0.0.2:40100"), Remote, TcpConnectionState.Established, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 5), OwnedByLiveProcess: true);
        var destroyer = new ScriptedDestroyer(throwIfCalled: true);

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(foreign)), destroyer);

        Assert.False(report.IsDrained);
        Assert.Equal("partial-drain:live-foreign-owner", report.Reason);
        Assert.Empty(destroyer.Calls);
    }

    [Theory]
    [InlineData(TcpConnectionState.Listen, true)]       // a live listener (Docker's embedded DNS on 127.0.0.11) carries no payload
    [InlineData(TcpConnectionState.TimeWait, false)]    // every other live foreign owner stays ambiguous, even in a "safe" state
    [InlineData(TcpConnectionState.SynSent, false)]
    [InlineData(TcpConnectionState.Established, false)]
    public async Task Linux_only_a_listening_socket_of_a_live_foreign_process_is_not_ambiguous(TcpConnectionState state, bool drained)
    {
        var foreign = new ObservedSocket(IPEndPoint.Parse("127.0.0.11:36527"), IPEndPoint.Parse("0.0.0.0:0"), state, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 6), OwnedByLiveProcess: true);

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(foreign)), new ScriptedDestroyer(throwIfCalled: true));

        Assert.Equal(drained, report.IsDrained);
    }

    [Fact]
    public async Task Linux_a_foreign_owner_that_disappears_before_the_deadline_lets_the_drain_complete()
    {
        var foreign = new ObservedSocket(IPEndPoint.Parse("10.0.0.2:40100"), Remote, TcpConnectionState.Established, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 5), OwnedByLiveProcess: true);

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(foreign), Table()), new ScriptedDestroyer(throwIfCalled: true));

        Assert.True(report.IsDrained);
    }

    [Theory]
    [InlineData(DestroyOutcome.Unsupported, "destroy-unsupported")]
    [InlineData(DestroyOutcome.PermissionDenied, "destroy-permission-denied")]
    [InlineData(DestroyOutcome.IdentityMismatch, "destroy-identity-mismatch")]
    [InlineData(DestroyOutcome.Failed, "destroy-failed")]
    public async Task Linux_any_destroy_failure_fails_closed(DestroyOutcome outcome, string reason)
    {
        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.Established, Cookie))), new ScriptedDestroyer(outcome));

        Assert.False(report.IsDrained);
        Assert.Equal(reason, report.Reason);
    }

    [Fact]
    public async Task Linux_a_destroy_that_reports_success_but_leaves_the_socket_is_a_partial_drain()
    {
        // ss -K style: "success" but the kernel silently skipped the socket; it is still ESTABLISHED on every poll.
        var stuck = Table(Socket(TcpConnectionState.Established, Cookie));

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(stuck), new ScriptedDestroyer());

        Assert.False(report.IsDrained);
        Assert.StartsWith("partial-drain", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_partial_drain_one_destroyed_one_stuck_is_not_drained()
    {
        var other = new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 78);
        var otherLocal = IPEndPoint.Parse("10.0.0.2:40001");
        var table = new ScriptedTable(
            Table(Socket(TcpConnectionState.Established, Cookie), Socket(TcpConnectionState.FinWait1, other, local: otherLocal)),
            Table(Socket(TcpConnectionState.FinWait1, other, local: otherLocal)));

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), table, new ScriptedDestroyer());

        Assert.False(report.IsDrained);
        Assert.StartsWith("partial-drain", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_a_ledgered_tuple_with_another_cookie_is_ambiguity_and_nothing_is_destroyed()
    {
        var destroyer = new ScriptedDestroyer();
        var replacement = new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 999);

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.Established, replacement))), destroyer);

        Assert.False(report.IsDrained);
        Assert.Equal("identity-mismatch", report.Reason);
        Assert.Empty(destroyer.Calls);
    }

    [Fact]
    public async Task Linux_a_socket_without_an_identity_fails_closed()
    {
        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.Established, null))), new ScriptedDestroyer());

        Assert.False(report.IsDrained);
        Assert.Equal("identity-missing", report.Reason);
    }

    [Fact]
    public async Task Linux_without_destroy_capability_never_reports_drained()
    {
        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table()), destroyer: null);

        Assert.False(report.IsDrained);
        Assert.Equal("destroy-capability-missing", report.Reason);
    }

    // ---------- Shared fail-closed rules ----------

    [Theory]
    [InlineData(DrainPlatform.LinuxNamespace)]
    [InlineData(DrainPlatform.Windows)]
    public async Task A_persistently_incomplete_enumeration_fails_closed(DrainPlatform platform)
    {
        var table = new ScriptedTable(SocketTableSnapshot.Incomplete("NLM_F_DUMP_INTR"));

        var report = await Run(platform, Ledger(platform == DrainPlatform.Windows ? Timestamp : Cookie), table, new ScriptedDestroyer());

        Assert.False(report.IsDrained);
        Assert.Equal("enumeration-incomplete", report.Reason);
    }

    [Fact]
    public async Task An_enumeration_that_succeeds_on_retry_is_accepted()
    {
        var table = new ScriptedTable(SocketTableSnapshot.Incomplete("interrupted"), Table());

        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), table, new ScriptedDestroyer());

        Assert.True(report.IsDrained);
    }

    [Theory]
    [InlineData(LedgerReadStatus.Corrupt, "ledger-corrupt")]
    [InlineData(LedgerReadStatus.Missing, "ledger-missing")]
    public async Task A_corrupt_or_missing_ledger_fails_closed(LedgerReadStatus status, string reason)
    {
        var report = await Run(DrainPlatform.LinuxNamespace, new LedgerReadResult(status, [], false, null), new ScriptedTable(Table()), new ScriptedDestroyer());

        Assert.False(report.IsDrained);
        Assert.Equal(reason, report.Reason);
    }

    [Fact]
    public async Task A_torn_final_ledger_record_does_not_block_the_drain()
    {
        var report = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie, tornTail: true), new ScriptedTable(Table()), new ScriptedDestroyer());

        Assert.True(report.IsDrained);
    }

    [Fact]
    public async Task A_supervisor_restart_mid_drain_re_evaluates_from_scratch()
    {
        // First supervisor gives up (stuck socket); a restarted supervisor runs a fresh drain with a fresh enumeration.
        var stuck = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.Established, Cookie))), new ScriptedDestroyer());
        var restarted = await Run(DrainPlatform.LinuxNamespace, Ledger(Cookie), new ScriptedTable(Table(Socket(TcpConnectionState.Established, Cookie)), Table()), new ScriptedDestroyer());

        Assert.False(stuck.IsDrained);
        Assert.True(restarted.IsDrained);
        Assert.Equal(1, restarted.SocketsDestroyed);
    }

    // ---------- Windows (tuple + creation timestamp, never terminate) ----------

    [Fact]
    public async Task Windows_an_absent_tuple_is_drained()
    {
        var report = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table()), new ScriptedDestroyer(throwIfCalled: true));

        Assert.True(report.IsDrained);
    }

    [Fact]
    public async Task Windows_the_original_in_a_safe_state_is_drained()
    {
        var report = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table(Socket(TcpConnectionState.TimeWait, Timestamp))), new ScriptedDestroyer(throwIfCalled: true));

        Assert.True(report.IsDrained);
    }

    [Fact]
    public async Task Windows_a_lingering_original_is_never_terminated_and_fails_closed_at_the_deadline()
    {
        var report = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table(Socket(TcpConnectionState.Established, Timestamp))), new ScriptedDestroyer(throwIfCalled: true));

        Assert.False(report.IsDrained);
        Assert.Equal("partial-drain:transmit-capable", report.Reason);
        Assert.Equal(0, report.SocketsDestroyed);
    }

    [Fact]
    public async Task Windows_port_reuse_by_another_process_is_ambiguous_never_drained_and_never_touched()
    {
        // P11d: same tuple, different creation timestamp, owned by a live process.
        var replacement = new ConnectionIdentity(ConnectionIdentityKind.WindowsCreateTimestamp, Timestamp.Value + 1);

        var report = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table(Socket(TcpConnectionState.Established, replacement, liveOwner: true))), new ScriptedDestroyer(throwIfCalled: true));

        Assert.False(report.IsDrained);
        Assert.Equal("partial-drain:ambiguous-identity", report.Reason);
    }

    [Fact]
    public async Task Windows_once_the_replacement_closes_the_absent_tuple_is_drained()
    {
        var replacement = new ConnectionIdentity(ConnectionIdentityKind.WindowsCreateTimestamp, Timestamp.Value + 1);
        var table = new ScriptedTable(Table(Socket(TcpConnectionState.Established, replacement, liveOwner: true)), Table());

        var report = await Run(DrainPlatform.Windows, Ledger(Timestamp), table, new ScriptedDestroyer(throwIfCalled: true));

        Assert.True(report.IsDrained);
    }

    [Fact]
    public async Task Windows_duplicate_rows_or_a_zero_timestamp_are_ambiguous()
    {
        var duplicates = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table(Socket(TcpConnectionState.TimeWait, Timestamp), Socket(TcpConnectionState.TimeWait, Timestamp))), new ScriptedDestroyer(throwIfCalled: true));
        var zero = await Run(DrainPlatform.Windows, Ledger(Timestamp), new ScriptedTable(Table(Socket(TcpConnectionState.TimeWait, new ConnectionIdentity(ConnectionIdentityKind.WindowsCreateTimestamp, 0)))), new ScriptedDestroyer(throwIfCalled: true));

        Assert.Equal("partial-drain:ambiguous-duplicate-rows", duplicates.Reason);
        Assert.Equal("partial-drain:ambiguous-identity", zero.Reason);
    }

    [Theory]
    [InlineData(TcpConnectionState.Closed, true)]
    [InlineData(TcpConnectionState.TimeWait, true)]
    [InlineData(TcpConnectionState.FinWait2, true)]
    [InlineData(TcpConnectionState.SynSent, true)]
    [InlineData(TcpConnectionState.Established, false)]
    [InlineData(TcpConnectionState.FinWait1, false)]
    [InlineData(TcpConnectionState.CloseWait, false)]
    [InlineData(TcpConnectionState.Closing, false)]
    [InlineData(TcpConnectionState.LastAck, false)]
    [InlineData(TcpConnectionState.SynReceived, false)]
    [InlineData(TcpConnectionState.Unknown, false)]
    public void Only_states_that_cannot_send_payload_are_safe(TcpConnectionState state, bool safe)
    {
        Assert.Equal(safe, ConnectionDrain.IsSafe(state));
    }

    private sealed class ScriptedTable(params SocketTableSnapshot[] snapshots) : ISocketTableReader
    {
        private int _next;

        public SocketTableSnapshot Read() => snapshots[Math.Min(_next++, snapshots.Length - 1)];
    }

    private sealed class ScriptedDestroyer(DestroyOutcome outcome = DestroyOutcome.Destroyed, bool throwIfCalled = false) : ISocketDestroyer
    {
        public List<ConnectionIdentity?> Calls { get; } = [];

        public DestroyOutcome Destroy(ObservedSocket socket)
        {
            if (throwIfCalled)
            {
                throw new InvalidOperationException("Windows drains must never terminate a connection.");
            }

            Calls.Add(socket.Identity);
            return outcome;
        }
    }
}
