using System.Text.RegularExpressions;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>Source-level rules for Phase 4 step 2 (docs/PHASE4_PLAN.md §7.6.1 W2/W3 = WG6, ADR-038 content-only uploads).</summary>
public class Step2SourceTests
{
    private static string Automation => Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Automation");

    private static string ProxyFolder => Path.Combine(Automation, "Network", "Proxy");

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void WG6_only_UpstreamChannel_sends_on_upstream_sockets()
    {
        var offenders = Sources(Path.Combine(Automation, "Network"))
            .Where(f => !f.EndsWith("UpstreamChannel.cs", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.(Send|SendAsync|SendTo|SendToAsync|SendPacketsAsync|SendFile|SendFileAsync)\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void WG6_upstream_sockets_are_never_wrapped_in_a_stream_that_could_bypass_the_gate()
    {
        var offenders = Sources(ProxyFolder)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+NetworkStream\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void WG6_the_gated_send_passes_no_cancellation_token()
    {
        var channel = File.ReadAllText(Path.Combine(ProxyFolder, "UpstreamChannel.cs"));
        var sends = Regex.Matches(channel, @"\.SendAsync\s*\(([^;]*)\)");

        Assert.Single(sends);
        Assert.Contains("CancellationToken.None", sends[0].Value, StringComparison.Ordinal);
        Assert.Contains("TryEnter()", channel, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploads_pass_only_an_in_memory_FilePayload_never_a_path()
    {
        var calls = Sources(Automation)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"SetInputFilesAsync\s*\(([^,)]*)").Select(m => m.Groups[1].Value.Trim()))
            .ToList();
        var session = File.ReadAllText(Path.Combine(Automation, "Browser", "BrowserSession.cs"));

        Assert.Equal(["payload"], calls);
        Assert.Contains("var payload = new FilePayload", session, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fencing_code_has_no_browser_dependency()
    {
        var offenders = Sources(Path.Combine(Automation, "Network", "Fencing"))
            .Where(f => File.ReadAllText(f).Contains("Microsoft.Playwright", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Windows_drains_never_use_SetTcpEntry()
    {
        var offenders = Sources(Automation)
            .Where(f => File.ReadAllText(f).Contains("SetTcpEntry(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
