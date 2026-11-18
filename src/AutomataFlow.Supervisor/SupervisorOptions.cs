namespace AutomataFlow.Supervisor;

/// <summary>Supervisor settings (section <c>Supervisor</c>). The launcher sets the unit fields; the rest have safe defaults.</summary>
public sealed class SupervisorOptions
{
    public const string SectionName = "Supervisor";

    /// <summary>PID of the unit wrapper started by the launcher (the supervisor's child).</summary>
    public int UnitWrapperPid { get; set; }

    /// <summary>The Worker's kernel uid (exclusive to the unit in this container).</summary>
    public int WorkerUid { get; set; } = 10001;

    /// <summary>A stale incarnation heartbeat (database clock) means a frozen or partitioned Worker: kill and prove.</summary>
    public int HeartbeatStaleSeconds { get; set; } = 45;

    /// <summary>A Worker that has not registered its incarnation by then is killed (nothing it did can have executed).</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;

    /// <summary>Deadline for the positive "unit empty" proof after the kill.</summary>
    public int FenceConfirmTimeoutSeconds { get; set; } = 60;

    public int DrainPolls { get; set; } = 60;

    public int DrainPollMilliseconds { get; set; } = 1_000;

    /// <summary>Graceful stop: how long the Worker may take to cancel its attempts after SIGTERM before it is killed.</summary>
    public int GracefulStopSeconds { get; set; } = 45;

    public int MonitorIntervalMilliseconds { get; set; } = 1_000;

    public static bool IsValid(SupervisorOptions o) =>
        o.UnitWrapperPid > 1 && o.WorkerUid > 0 && o.HeartbeatStaleSeconds is >= 3 and <= 3_600 && o.StartupTimeoutSeconds is >= 5 and <= 3_600
        && o.FenceConfirmTimeoutSeconds is >= 1 and <= 600 && o.DrainPolls is >= 1 and <= 600 && o.DrainPollMilliseconds is >= 10 and <= 10_000
        && o.GracefulStopSeconds is >= 1 and <= 600 && o.MonitorIntervalMilliseconds is >= 50 and <= 10_000;
}
