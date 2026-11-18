using AutomataFlow.Automation.Network.Fencing;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>
/// The drain's view of the container's network namespace (ADR-075). The namespace is shared with the supervisor, so a
/// socket's owner is decided by its inode:
/// <list type="bullet">
/// <item>an inode in the supervisor's own fd table is the supervisor's: excluded, never touched;</item>
/// <item>inode 0 is an orphan (no process holds it); after the proven-empty unit it belongs to the dead incarnation;</item>
/// <item>any other inode is held by a live foreign process: reported as such, which the drain treats as ambiguous.</item>
/// </list>
/// The supervisor's fd table is read AFTER the dump, so a supervisor socket that existed during the dump is recognised;
/// one closed in between shows up as foreign and only delays (then fails) the drain, never destroys anything.
/// </summary>
public sealed class NamespaceSocketTable(Func<DiagDump> dump, Func<IReadOnlySet<uint>> ownInodes, Func<DiagSocket, int> destroy) : ISocketTableReader, ISocketDestroyer
{
    private readonly Dictionary<ulong, DiagSocket> _lastDump = [];

    /// <summary>Sockets of the latest read held by a live process other than the supervisor (diagnostics for a failed drain).</summary>
    public IReadOnlyList<DiagSocket> LastForeign { get; private set; } = [];

    public static NamespaceSocketTable System() => new(SockDiag.DumpTcp, ProcFs.OwnSocketInodes, SockDiag.Destroy);

    public SocketTableSnapshot Read()
    {
        var result = dump();
        if (!result.Complete)
        {
            return SocketTableSnapshot.Incomplete(result.Failure ?? "dump-incomplete");
        }

        IReadOnlySet<uint> own;
        try
        {
            own = ownInodes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SocketTableSnapshot.Incomplete("own-fd-table-unreadable");
        }

        _lastDump.Clear();
        LastForeign = [.. result.Sockets.Where(s => s.Inode != 0 && !own.Contains(s.Inode))];
        var observed = new List<ObservedSocket>();
        foreach (var socket in result.Sockets)
        {
            if (socket.Inode != 0 && own.Contains(socket.Inode))
            {
                continue; // the supervisor's own socket (verified by inode)
            }

            _lastDump[socket.Cookie] = socket;
            observed.Add(new ObservedSocket(socket.Local, socket.Remote, socket.State, SockDiag.Identity(socket), OwnedByLiveProcess: socket.Inode != 0));
        }

        return new SocketTableSnapshot(true, observed);
    }

    public DestroyOutcome Destroy(ObservedSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.OwnedByLiveProcess || socket.Identity is not { } identity || !_lastDump.TryGetValue(identity.Value, out var target))
        {
            return DestroyOutcome.IdentityMismatch; // never destroy anything not identified by the latest dump as an orphan
        }

        return SockDiag.ToOutcome(destroy(target));
    }
}
