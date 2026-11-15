using System.Net;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.Automation.Network.Fencing;

/// <summary>TCP state of an observed socket (Linux sock_diag / Windows owner-module table, normalized).</summary>
public enum TcpConnectionState
{
    Unknown = 0,
    Closed,
    Listen,
    SynSent,
    SynReceived,
    Established,
    FinWait1,
    FinWait2,
    CloseWait,
    Closing,
    LastAck,
    TimeWait,
    DeleteTcb,
}

/// <summary>One TCP socket as seen by the supervisor.</summary>
/// <param name="Identity">OS identity (Linux cookie / Windows creation timestamp); null or zero if the OS did not report one.</param>
/// <param name="OwnedByLiveProcess">Windows: the owning PID is a live process (port reuse by another process).</param>
public sealed record ObservedSocket(IPEndPoint Local, IPEndPoint Remote, TcpConnectionState State, ConnectionIdentity? Identity, bool OwnedByLiveProcess = false);

/// <summary>A socket table read. <see cref="Complete"/> is false for an interrupted, truncated or failed enumeration.</summary>
public sealed record SocketTableSnapshot(bool Complete, IReadOnlyList<ObservedSocket> Sockets, string? IncompleteReason = null)
{
    public static SocketTableSnapshot Incomplete(string reason) => new(false, [], reason);
}

public enum DestroyOutcome
{
    Destroyed,
    Unsupported,
    PermissionDenied,
    IdentityMismatch,
    Failed,
}

/// <summary>Reads the socket table of the incarnation's network namespace (Linux) or the host (Windows). Phase 5 supplies the OS implementation.</summary>
public interface ISocketTableReader
{
    SocketTableSnapshot Read();
}

/// <summary>
/// Destroys ONE socket identified by its exact cookie (Linux SOCK_DESTROY via sock_diag). Phase 5 supplies the OS
/// implementation. There is deliberately no Windows implementation: SetTcpEntry cannot target by identity (ADR-060).
/// </summary>
public interface ISocketDestroyer
{
    DestroyOutcome Destroy(ObservedSocket socket);
}

public enum DrainPlatform
{
    /// <summary>Dedicated network namespace per incarnation; every socket in it is an orphan; destroy by exact cookie.</summary>
    LinuxNamespace,

    /// <summary>Shared host table; match by tuple + creation timestamp; never terminate; ambiguity fails closed.</summary>
    Windows,
}

public enum DrainResult
{
    Drained,
    NotDrained,
}

/// <param name="Reason">Machine-readable reason for NotDrained (fail-closed table, docs/PHASE4_PLAN.md §7.6.5).</param>
public sealed record DrainReport(DrainResult Result, string? Reason, int LedgerEntries, int SocketsChecked, int SocketsDestroyed, int Polls)
{
    public bool IsDrained => Result == DrainResult.Drained;
}

/// <param name="MaxPolls">Re-enumerations before giving up (with <see cref="PollInterval"/> this is the FenceConfirmTimeout).</param>
/// <param name="MaxDumpRetries">Retries of an incomplete enumeration within one poll before failing closed.</param>
public sealed record DrainPolicy(int MaxPolls, TimeSpan PollInterval, int MaxDumpRetries)
{
    public static DrainPolicy Default { get; } = new(60, TimeSpan.FromSeconds(1), 3);
}

/// <summary>
/// The supervisor's socket drain (docs/PHASE4_PLAN.md §7.6.5, ADR-060): proves connection destruction (E2) for a dead
/// Worker incarnation, or reports NotDrained. Every unsupported, failed, incomplete or ambiguous step fails closed. A
/// drain never keeps partial state: each run starts from the ledger and a fresh enumeration (a supervisor restart
/// mid-drain simply runs it again). A Drained report proves E2 only, never physical departure (E3), and never that a
/// dispatched request failed.
/// </summary>
public sealed class ConnectionDrain
{
    private readonly ISocketTableReader _table;
    private readonly ISocketDestroyer? _destroyer;
    private readonly DrainPolicy _policy;
    private readonly Func<TimeSpan, Task> _delay;

    public ConnectionDrain(ISocketTableReader table, ISocketDestroyer? destroyer, DrainPolicy policy, Func<TimeSpan, Task>? delay = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _destroyer = destroyer;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _delay = delay ?? (d => Task.Delay(d));
    }

    public async Task<DrainReport> RunAsync(DrainPlatform platform, LedgerReadResult ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (ledger.Status != LedgerReadStatus.Ok)
        {
            return Fail(ledger.Status == LedgerReadStatus.Missing ? "ledger-missing" : "ledger-corrupt", ledger, 0, 0, 0);
        }

        if (platform == DrainPlatform.LinuxNamespace && _destroyer is null)
        {
            return Fail("destroy-capability-missing", ledger, 0, 0, 0);
        }

        var destroyed = 0;
        string lastReason = "not-drained";
        for (var poll = 1; poll <= _policy.MaxPolls; poll++)
        {
            var snapshot = ReadComplete();
            if (snapshot is null)
            {
                return Fail("enumeration-incomplete", ledger, 0, destroyed, poll);
            }

            var step = platform == DrainPlatform.LinuxNamespace
                ? EvaluateLinux(ledger.Entries, snapshot, ref destroyed)
                : EvaluateWindows(ledger.Entries, snapshot);

            if (step.Fatal is not null)
            {
                return Fail(step.Fatal, ledger, snapshot.Sockets.Count, destroyed, poll);
            }

            if (step.Drained)
            {
                return new DrainReport(DrainResult.Drained, null, ledger.Entries.Count, snapshot.Sockets.Count, destroyed, poll);
            }

            lastReason = step.Pending ?? "not-drained";
            if (poll < _policy.MaxPolls)
            {
                await _delay(_policy.PollInterval).ConfigureAwait(false);
            }
        }

        // Partial drain: something is still transmit-capable or ambiguous at the deadline.
        return Fail("partial-drain:" + lastReason, ledger, 0, destroyed, _policy.MaxPolls);
    }

    /// <summary>Safe states cannot send payload: absent, CLOSED, TIME_WAIT, FIN_WAIT_2 (our FIN acked), SYN_SENT (no TFO), LISTEN.</summary>
    public static bool IsSafe(TcpConnectionState state) =>
        state is TcpConnectionState.Closed or TcpConnectionState.TimeWait or TcpConnectionState.FinWait2
            or TcpConnectionState.SynSent or TcpConnectionState.Listen or TcpConnectionState.DeleteTcb;

    private SocketTableSnapshot? ReadComplete()
    {
        for (var attempt = 0; attempt <= _policy.MaxDumpRetries; attempt++)
        {
            var snapshot = _table.Read();
            if (snapshot.Complete)
            {
                return snapshot;
            }
        }

        return null;
    }

    private StepOutcome EvaluateLinux(IReadOnlyList<ConnectionLedgerEntry> ledger, SocketTableSnapshot snapshot, ref int destroyed)
    {
        // Cross-check: a ledgered tuple present with another cookie means isolation is broken -> ambiguity -> fail closed.
        foreach (var entry in ledger)
        {
            foreach (var socket in snapshot.Sockets.Where(s => SameTuple(s, entry)))
            {
                if (socket.Identity is not { } id || id.Kind != ConnectionIdentityKind.LinuxSocketCookie || id.Value == 0)
                {
                    return StepOutcome.Fail("identity-missing");
                }

                if (id != entry.Identity)
                {
                    return StepOutcome.Fail("identity-mismatch");
                }
            }
        }

        var transmitCapable = snapshot.Sockets.Where(s => !IsSafe(s.State)).ToList();
        if (transmitCapable.Count == 0)
        {
            return StepOutcome.Done();
        }

        // Every socket in the dedicated namespace belongs to the dead incarnation: destroy by EXACT cookie only.
        foreach (var socket in transmitCapable)
        {
            if (socket.Identity is not { Kind: ConnectionIdentityKind.LinuxSocketCookie } id || id.Value == 0)
            {
                return StepOutcome.Fail("identity-missing");
            }

            var outcome = _destroyer!.Destroy(socket);
            switch (outcome)
            {
                case DestroyOutcome.Destroyed:
                    destroyed++;
                    break;
                case DestroyOutcome.Unsupported:
                    return StepOutcome.Fail("destroy-unsupported");
                case DestroyOutcome.PermissionDenied:
                    return StepOutcome.Fail("destroy-permission-denied");
                case DestroyOutcome.IdentityMismatch:
                    return StepOutcome.Fail("destroy-identity-mismatch");
                default:
                    return StepOutcome.Fail("destroy-failed");
            }
        }

        // A reported success is not trusted: the next poll re-enumerates (ss -K silently skips unsupported sockets).
        return StepOutcome.Wait("destroy-pending-verification");
    }

    private static StepOutcome EvaluateWindows(IReadOnlyList<ConnectionLedgerEntry> ledger, SocketTableSnapshot snapshot)
    {
        string? pending = null;
        foreach (var entry in ledger)
        {
            var rows = snapshot.Sockets.Where(s => SameTuple(s, entry)).ToList();
            if (rows.Count == 0)
            {
                continue; // tuple absent: the original connection is gone
            }

            if (rows.Count > 1)
            {
                pending ??= "ambiguous-duplicate-rows";
                continue;
            }

            var row = rows[0];
            if (row.Identity is not { Kind: ConnectionIdentityKind.WindowsCreateTimestamp } id || id.Value == 0 || id != entry.Identity)
            {
                // A different, missing or zero timestamp: maybe a replacement, maybe the original. Never "gone", never touched.
                pending ??= "ambiguous-identity";
                continue;
            }

            if (!IsSafe(row.State))
            {
                pending ??= "transmit-capable";
            }
        }

        return pending is null ? StepOutcome.Done() : StepOutcome.Wait(pending);
    }

    private static bool SameTuple(ObservedSocket socket, ConnectionLedgerEntry entry) =>
        Normalize(socket.Local).Equals(Normalize(entry.Local)) && Normalize(socket.Remote).Equals(Normalize(entry.Remote));

    private static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    private static DrainReport Fail(string reason, LedgerReadResult ledger, int checkedSockets, int destroyed, int polls) =>
        new(DrainResult.NotDrained, reason, ledger.Entries.Count, checkedSockets, destroyed, polls);

    private readonly record struct StepOutcome(bool Drained, string? Pending, string? Fatal)
    {
        public static StepOutcome Done() => new(true, null, null);

        public static StepOutcome Wait(string reason) => new(false, reason, null);

        public static StepOutcome Fail(string reason) => new(false, null, reason);
    }
}
