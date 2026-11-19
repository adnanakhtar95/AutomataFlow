namespace AutomataFlow.AutomationTests.Supervision;

/// <summary>
/// Static checks of the production launcher (deploy/worker/af-worker-launch.sh). The supervision suite runs the real
/// launcher many times; these pin properties whose regressions only show up as rare races.
/// </summary>
public sealed class LauncherScriptTests
{
    private static readonly string Script = File.ReadAllText(SupervisionPaths.Launcher);

    [Fact]
    public void The_unit_wrapper_waits_for_both_its_uid_and_gid_maps_before_dropping_to_the_inner_ids()
    {
        // Found by the Phase 6 verification run: waiting for uid_map alone let setpriv's setresgid(1000) race the
        // launcher's gid_map write ("setresgid failed: Invalid argument"), so a Worker container occasionally failed to start.
        var wait = Script[Script.IndexOf("sh -c 'while", StringComparison.Ordinal)..];
        wait = wait[..wait.IndexOf("done", StringComparison.Ordinal)];
        Assert.Contains("/proc/self/uid_map", wait, StringComparison.Ordinal);
        Assert.Contains("/proc/self/gid_map", wait, StringComparison.Ordinal);
        Assert.True(Script.IndexOf("for map in uid_map gid_map", StringComparison.Ordinal) > Script.IndexOf("WRAPPER=$!", StringComparison.Ordinal),
            "the maps are written by the launcher after the wrapper started");
    }

    [Fact]
    public void The_launcher_execs_into_the_supervisor_and_never_uses_an_init_process()
    {
        Assert.Contains("exec setpriv --reuid=\"$SUP_UID\"", Script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*(exec\s+)?\S*(tini|docker-init)\b", Script); // only comments may mention tini
    }
}

