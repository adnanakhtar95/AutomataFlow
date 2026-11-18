using System.Runtime.InteropServices;
using AutomataFlow.Supervisor.Fencing;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>
/// The containment unit on Linux (ADR-075): the wrapper the launcher started (the supervisor's child), the user namespace
/// it created (owned by the supervisor's uid, so the supervisor may signal and inspect unit tasks) and the PID namespace of
/// its child, the unit's init. Unit tasks are recognised three independent ways: the Worker's kernel uid, the unit's user
/// namespace, and the unit's PID namespace.
/// </summary>
public sealed class LinuxUnit : IUnitControl
{
    private readonly int _wrapper;
    private readonly int _workerUid;
    private readonly IReadOnlySet<int> _allowed;
    private bool _reaped;

    private LinuxUnit(int wrapper, int workerUid, string userNamespace, string pidNamespace, int init, IReadOnlySet<int> allowed)
    {
        _wrapper = wrapper;
        _workerUid = workerUid;
        UserNamespace = userNamespace;
        PidNamespace = pidNamespace;
        Init = init;
        _allowed = allowed;
    }

    public string UserNamespace { get; }

    public string PidNamespace { get; }

    public int Init { get; }

    public int Wrapper => _wrapper;

    /// <summary>
    /// Attaches to the unit the launcher started: waits for the wrapper's child (the init) and records the namespaces.
    /// Returns the problems that make the unit unsupervisable (empty on success).
    /// </summary>
    public static async Task<(LinuxUnit? Unit, IReadOnlyList<string> Problems)> AttachAsync(int wrapper, int workerUid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var self = Native.getpid();
        var status = ProcFs.Status(wrapper);
        if (status is null || status.PPid != self)
        {
            return (null, ["The unit wrapper is not a child of the supervisor."]);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        int init;
        while (true)
        {
            init = Children(wrapper).FirstOrDefault();
            if (init > 0 && ProcFs.Status(init) is { } s && s.Uids.Count > 0 && s.Uids.All(u => u == workerUid))
            {
                break;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                return (null, ["The unit's init did not start as the Worker uid."]);
            }

            await Task.Delay(50, cancellationToken);
        }

        var problems = new List<string>();
        var userNs = ProcFs.Namespace(wrapper, "user");
        var pidNs = ProcFs.Namespace(init, "pid");
        if (userNs is null || pidNs is null)
        {
            problems.Add("The unit's namespaces are not inspectable (the unit's user namespace is not owned by the supervisor's uid).");
        }
        else
        {
            if (userNs == ProcFs.SelfNamespace("user"))
            {
                problems.Add("The unit shares the supervisor's user namespace.");
            }

            if (pidNs == ProcFs.SelfNamespace("pid"))
            {
                problems.Add("The unit shares the supervisor's PID namespace.");
            }

            if (ProcFs.Namespace(init, "net") != ProcFs.SelfNamespace("net"))
            {
                problems.Add("The unit is not in the supervisor's (the container's) network namespace.");
            }

            // Owner rule: signalling a task of ANOTHER uid works only with CAP_KILL in its user namespace, i.e. only if the
            // supervisor's uid owns the unit's user namespace. Signal 0 checks permission without delivering anything.
            if (Native.kill(init, 0) != 0)
            {
                problems.Add($"The supervisor cannot signal the unit (errno {Marshal.GetLastPInvokeError()}): it does not own the unit's user namespace.");
            }
        }

        var unit = new LinuxUnit(wrapper, workerUid, userNs ?? string.Empty, pidNs ?? string.Empty, init, AllowedSurvivors(self));
        return (problems.Count == 0 ? unit : null, problems);
    }

    public void KillAll()
    {
        if (!_reaped)
        {
            _ = Native.kill(_wrapper, Native.SIGKILL);
        }

        var scan = Scan();
        foreach (var pid in scan.UnitTasks.Where(p => p != _wrapper || !_reaped))
        {
            _ = Native.kill(pid, Native.SIGKILL);
        }
    }

    public void Terminate() => _ = Native.kill(Init, Native.SIGTERM);

    public bool TryReapWrapper()
    {
        if (_reaped)
        {
            return true;
        }

        var result = Native.waitpid(_wrapper, out _, Native.WNOHANG);
        if (result == _wrapper)
        {
            _reaped = true;
        }
        else if (result < 0 && Marshal.GetLastPInvokeError() == Native.ECHILD && !Directory.Exists($"{ProcFs.Root}/{_wrapper}"))
        {
            _reaped = true; // already reaped (by us); the PID is gone
        }

        return _reaped;
    }

    /// <summary>True while the wrapper has not exited (a zombie counts as exited).</summary>
    public bool WrapperRunning()
    {
        if (_reaped)
        {
            return false;
        }

        try
        {
            var stat = File.ReadAllText($"{ProcFs.Root}/{_wrapper}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public UnitScan Scan()
    {
        IReadOnlyList<int> pids;
        try
        {
            pids = ProcFs.Pids();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UnitScan(false, [], [], "proc-unreadable");
        }

        var unit = new List<int>();
        var foreign = new List<int>();
        foreach (var pid in pids)
        {
            var status = ProcFs.Status(pid);
            if (status is null)
            {
                continue; // gone
            }

            var member = (pid == _wrapper && !_reaped)
                || status.Uids.Contains(_workerUid)
                || ProcFs.Namespace(pid, "user") == UserNamespace
                || ProcFs.Namespace(pid, "pid") == PidNamespace;
            if (member)
            {
                unit.Add(pid);
            }
            else if (!_allowed.Contains(pid))
            {
                foreign.Add(pid);
            }
        }

        return new UnitScan(true, unit, foreign);
    }

    /// <summary>PID 1 and the supervisor's own ancestry, derived at run time (never configurable).</summary>
    private static HashSet<int> AllowedSurvivors(int self)
    {
        var allowed = new HashSet<int> { 1, self };
        var pid = self;
        for (var i = 0; i < 64 && ProcFs.Status(pid) is { PPid: > 0 } status && allowed.Add(status.PPid); i++)
        {
            pid = status.PPid;
        }

        return allowed;
    }

    private static List<int> Children(int pid)
    {
        try
        {
            return File.ReadAllText($"{ProcFs.Root}/{pid}/task/{pid}/children").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => int.Parse(c, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
