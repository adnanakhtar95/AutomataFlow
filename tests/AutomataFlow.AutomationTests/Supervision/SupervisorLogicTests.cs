using System.Net;
using System.Net.Sockets;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Fencing;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.Supervisor.Fencing;
using AutomataFlow.Supervisor.Linux;
using AutomataFlow.Tests.Journal;
using AutomataFlow.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutomataFlow.AutomationTests.Supervision;

/// <summary>
/// The supervisor's decision logic against scripted units, socket tables and the shared in-memory journal (ADR-075):
/// a fence is reported ONLY with a proven-empty unit and a complete drain. P3 (a unit that never empties), P10 (ledger
/// problems) and the procedure-level P13 cases live here; the real-process P tests are in SupervisionTests.
/// </summary>
public class SupervisorLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly IPEndPoint Local = IPEndPoint.Parse("172.18.0.3:41000");
    private static readonly IPEndPoint Remote = IPEndPoint.Parse("172.18.0.2:5000");
    private static readonly SupervisorPolicy Fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10), new DrainPolicy(3, TimeSpan.Zero, 1));

    private sealed class ScriptedUnit(bool reaps = true, params UnitScan[] scans) : IUnitControl
    {
        private int _scan;

        public int Kills { get; private set; }

        public void KillAll() => Kills++;

        public void Terminate()
        {
        }

        public bool TryReapWrapper() => reaps;

        public UnitScan Scan() => scans.Length == 0 ? new UnitScan(true, [], []) : scans[Math.Min(_scan++, scans.Length - 1)];
    }

    private sealed class Table(params SocketTableSnapshot[] snapshots) : ISocketTableReader, ISocketDestroyer
    {
        private int _next;

        public List<ObservedSocket> Destroyed { get; } = [];

        public SocketTableSnapshot Read() => snapshots.Length == 0 ? new SocketTableSnapshot(true, []) : snapshots[Math.Min(_next++, snapshots.Length - 1)];

        public DestroyOutcome Destroy(ObservedSocket socket)
        {
            Destroyed.Add(socket);
            return DestroyOutcome.Destroyed;
        }
    }

    private sealed record Setup(InMemoryAttemptJournal Journal, WorkerIncarnation Incarnation, string LedgerDirectory, ManualClock Clock);

    private static async Task<Setup> SetupAsync(bool writeLedger = true)
    {
        var clock = new ManualClock(T0);
        var journal = new InMemoryAttemptJournal(clock, TimeSpan.FromMinutes(5));
        var directory = Directory.CreateTempSubdirectory("af-sup-ledger-").FullName;
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".ledger");
        if (writeLedger)
        {
            using var ledger = new FileConnectionLedger(directory, Guid.NewGuid());
            ledger.Append(Guid.NewGuid(), Local, Remote, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 4711));
            path = ledger.Path;
        }

        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), "host", "boot", 7, T0,
            new ContainmentUnit(ContainmentKind.LinuxNamespaces, "workers/user:[1]/pid:[2]/" + Guid.NewGuid().ToString("N")), path);
        await journal.RegisterIncarnationAsync(incarnation, default);
        return new Setup(journal, incarnation, directory, clock);
    }

    private static FenceProcedure Procedure(Setup s, IUnitControl unit, Table table) =>
        new(s.Journal, unit, ConnectionLedgerReader.Read, table, table, Fast, TimeProvider.System, NullLogger.Instance);

    [Fact]
    public async Task A_proven_empty_unit_and_a_complete_drain_give_a_validated_ProcessDeathConfirmed_fence()
    {
        var s = await SetupAsync();
        var orphan = new ObservedSocket(Local, Remote, TcpConnectionState.FinWait1, new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 4711));
        var table = new Table(new SocketTableSnapshot(true, [orphan]), new SocketTableSnapshot(true, []));

        var outcome = await Procedure(s, new ScriptedUnit(), table).RunAsync(s.Incarnation, s.LedgerDirectory, default);

        Assert.Equal(FenceResultKind.Fenced, outcome.Kind);
        Assert.True(outcome.Confirmation!.Accepted);
        Assert.Equal(4711UL, Assert.Single(table.Destroyed).Identity!.Value.Value);
    }

    [Fact]
    public async Task P3_a_unit_that_never_empties_gives_no_fence_and_the_drain_never_runs()
    {
        var s = await SetupAsync();
        var table = new Table();
        var unit = new ScriptedUnit(true, new UnitScan(true, [4242], [])); // a task stuck in uninterruptible sleep

        var outcome = await Procedure(s, unit, table).RunAsync(s.Incarnation, s.LedgerDirectory, default);

        Assert.Equal((FenceResultKind.NotFenced, "unit-not-empty"), (outcome.Kind, outcome.Reason));
        Assert.True(unit.Kills > 1, "the kill is repeated until the deadline");
        Assert.Empty(table.Destroyed);
    }

    [Theory]
    [InlineData(false, "wrapper-not-reaped")]
    [InlineData(true, "process-scan-incomplete:proc-unreadable")]
    public async Task P13_no_proof_of_death_means_no_fence(bool reaps, string reason)
    {
        var s = await SetupAsync();
        var scan = reaps ? new UnitScan(false, [], [], "proc-unreadable") : new UnitScan(true, [], []);

        var outcome = await Procedure(s, new ScriptedUnit(reaps, scan), new Table()).RunAsync(s.Incarnation, s.LedgerDirectory, default);

        Assert.Equal((FenceResultKind.NotFenced, reason), (outcome.Kind, outcome.Reason));
    }

    [Fact]
    public async Task A_foreign_live_process_in_the_container_blocks_the_fence_until_the_deadline()
    {
        var s = await SetupAsync();

        var outcome = await Procedure(s, new ScriptedUnit(true, new UnitScan(true, [], [999])), new Table()).RunAsync(s.Incarnation, s.LedgerDirectory, default);

        Assert.Equal((FenceResultKind.NotFenced, "foreign-process-in-container"), (outcome.Kind, outcome.Reason));
    }

    [Fact]
    public async Task P10_a_missing_ledger_gives_no_fence()
    {
        var s = await SetupAsync(writeLedger: false);

        var outcome = await Procedure(s, new ScriptedUnit(), new Table()).RunAsync(s.Incarnation, s.LedgerDirectory, default);

        Assert.Equal((FenceResultKind.NotFenced, "drain:ledger-missing"), (outcome.Kind, outcome.Reason));
    }

    [Fact]
    public async Task A_ledger_outside_the_unit_directory_or_extra_ledger_files_give_no_fence()
    {
        var s = await SetupAsync();
        var elsewhere = Directory.CreateTempSubdirectory("af-other-").FullName;
        Assert.Equal("ledger-outside-unit-directory", (await Procedure(s, new ScriptedUnit(), new Table()).RunAsync(s.Incarnation, elsewhere, default)).Reason);

        await File.WriteAllTextAsync(Path.Combine(s.LedgerDirectory, "second.ledger"), string.Empty);
        Assert.Equal("unexpected-ledger-files", (await Procedure(s, new ScriptedUnit(), new Table()).RunAsync(s.Incarnation, s.LedgerDirectory, default)).Reason);
    }

    [Fact]
    public async Task A_worker_that_never_registered_needs_no_fence_but_its_unit_is_still_proven_gone()
    {
        var s = await SetupAsync();
        var unit = new ScriptedUnit(true, new UnitScan(true, [12], []), new UnitScan(true, [], []));

        var outcome = await Procedure(s, unit, new Table()).RunAsync(null, s.LedgerDirectory, default);

        Assert.Equal(FenceResultKind.NothingToFence, outcome.Kind);
        Assert.True(unit.Kills >= 2);
    }

    [Fact]
    public async Task The_journal_rejecting_the_evidence_is_never_reported_as_a_fence()
    {
        var s = await SetupAsync();
        var impostor = s.Incarnation with { Unit = new ContainmentUnit(ContainmentKind.LinuxNamespaces, "another-unit") };

        var outcome = await Procedure(s, new ScriptedUnit(), new Table()).RunAsync(impostor, s.LedgerDirectory, default);

        Assert.Equal((FenceResultKind.NotFenced, "journal-rejected:unit-mismatch"), (outcome.Kind, outcome.Reason));
    }

    // ---------- socket ownership in the shared container namespace ----------

    private static DiagSocket Diag(ulong cookie, uint inode, TcpConnectionState state = TcpConnectionState.Established) =>
        new(AddressFamily.InterNetwork, Local, Remote, state, cookie, inode, 10, 0, 0);

    [Fact]
    public void The_supervisors_own_sockets_are_excluded_by_inode_orphans_are_destroyable_and_foreign_owners_are_ambiguous()
    {
        var destroyed = new List<ulong>();
        var table = new NamespaceSocketTable(
            () => new DiagDump(true, [Diag(1, inode: 900), Diag(2, inode: 0), Diag(3, inode: 777)], null),
            () => new HashSet<uint> { 900 },
            s => { destroyed.Add(s.Cookie); return 0; });

        var snapshot = table.Read();

        Assert.DoesNotContain(snapshot.Sockets, s => s.Identity!.Value.Value == 1);                     // supervisor's own: invisible
        Assert.False(snapshot.Sockets.Single(s => s.Identity!.Value.Value == 2).OwnedByLiveProcess);    // orphan
        var foreign = snapshot.Sockets.Single(s => s.Identity!.Value.Value == 3);
        Assert.True(foreign.OwnedByLiveProcess);
        Assert.Equal(DestroyOutcome.IdentityMismatch, table.Destroy(foreign));                         // never destroyed
        Assert.Equal(DestroyOutcome.Destroyed, table.Destroy(snapshot.Sockets.Single(s => s.Identity!.Value.Value == 2)));
        Assert.Equal([2UL], destroyed);
    }

    [Theory]
    [InlineData(SockDiag.ENOENT, DestroyOutcome.Destroyed)] // gone OR cookie mismatch (observed): only re-enumeration decides
    [InlineData(SockDiag.ESTALE, DestroyOutcome.IdentityMismatch)]
    [InlineData(SockDiag.EPERM, DestroyOutcome.PermissionDenied)]
    [InlineData(SockDiag.EOPNOTSUPP, DestroyOutcome.Unsupported)]
    [InlineData(22, DestroyOutcome.Failed)]
    public void Destroy_errors_map_to_the_fail_closed_table(int errno, DestroyOutcome outcome) =>
        Assert.Equal(outcome, SockDiag.ToOutcome(errno));

    [Fact]
    public void An_incomplete_dump_is_an_incomplete_snapshot()
    {
        var table = new NamespaceSocketTable(() => new DiagDump(false, [], "netlink-dump-interrupted"), () => new HashSet<uint>(), _ => 0);
        Assert.Equal((false, "netlink-dump-interrupted"), (table.Read().Complete, table.Read().IncompleteReason));
    }

    // ---------- handshake peer and credentials ----------

    private static readonly UnitIdentity Unit = new(10001, "user:[10]", "pid:[20]", new ContainmentUnit(ContainmentKind.LinuxNamespaces, "u"), "/tmp/l");

    private static ProcStatus Status(int uid = 10001, ulong caps = 0, bool nnp = true) =>
        new(1, [uid, uid, uid, uid], caps, caps, caps, caps, caps, nnp, [5, 1]);

    [Theory]
    [InlineData(10001, 0UL, true, "user:[10]", "pid:[20]", null)]
    [InlineData(10002, 0UL, true, "user:[10]", "pid:[20]", "peer-not-worker-uid")]
    [InlineData(10001, 0UL, true, "user:[99]", "pid:[20]", "peer-not-in-unit")]
    [InlineData(10001, 0UL, true, "user:[10]", "pid:[99]", "peer-not-in-unit")]
    [InlineData(10001, 4096UL, true, "user:[10]", "pid:[20]", "peer-holds-privileges")]
    [InlineData(10001, 0UL, false, "user:[10]", "pid:[20]", "peer-holds-privileges")]
    public void Only_a_contained_worker_of_this_unit_is_answered(int uid, ulong caps, bool nnp, string userNs, string pidNs, string? problem) =>
        Assert.Equal(problem, HandshakePeer.Validate(Unit, Status(uid, caps, nnp), userNs, pidNs));

    [Fact]
    public void The_supervisor_accepts_only_its_own_uid_with_CAP_NET_ADMIN_alone()
    {
        var netAdmin = SupervisorStartup.CapNetAdmin;
        Assert.Empty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [10002, 10002, 10002, 10002], 0, netAdmin, netAdmin, netAdmin, netAdmin, true, [9]), 10001));
        Assert.NotEmpty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [10002, 10002, 10002, 10002], 0, 0, 0, netAdmin, 0, true, [9]), 10001));          // P9: no CAP_NET_ADMIN
        Assert.NotEmpty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [10002, 10002, 10002, 10002], 0, netAdmin | 1, netAdmin | 1, netAdmin | 1, 0, true, [9]), 10001)); // more than NET_ADMIN
        Assert.NotEmpty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [0, 0, 0, 0], 0, netAdmin, netAdmin, netAdmin, 0, true, [9]), 10001));            // root
        Assert.NotEmpty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [10001, 10001, 10001, 10001], 0, netAdmin, netAdmin, netAdmin, 0, true, [9]), 10001)); // Worker's uid
        Assert.NotEmpty(SupervisorStartup.CheckCredentials(new ProcStatus(1, [10002, 10002, 10002, 10002], 0, netAdmin, netAdmin, netAdmin, 0, false, [9]), 10001)); // no no_new_privs
    }

    [Fact]
    public void A_worker_with_any_capability_or_without_a_nested_pid_namespace_is_not_contained()
    {
        Assert.Empty(SelfCredentials.Parse("CapInh:\t0\nCapPrm:\t0\nCapEff:\t0\nCapBnd:\t0\nCapAmb:\t0\nNoNewPrivs:\t1\nNSpid:\t812\t3\n").ContainmentProblems());
        Assert.NotEmpty(SelfCredentials.Parse("CapInh:\t0\nCapPrm:\t0\nCapEff:\t0\nCapBnd:\t00000000a80425fb\nCapAmb:\t0\nNoNewPrivs:\t1\nNSpid:\t812\t3\n").ContainmentProblems());
        Assert.NotEmpty(SelfCredentials.Parse("CapInh:\t0\nCapPrm:\t0\nCapEff:\t0\nCapBnd:\t0\nCapAmb:\t0\nNoNewPrivs:\t1\nNSpid:\t812\n").ContainmentProblems());
        Assert.NotEmpty(SelfCredentials.Parse("CapInh:\t0\nCapPrm:\t0\nCapEff:\t0\nCapBnd:\t0\nCapAmb:\t0\nNoNewPrivs:\t0\nNSpid:\t812\t3\n").ContainmentProblems());
    }
}
