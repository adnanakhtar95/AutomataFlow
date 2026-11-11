using System.Text.RegularExpressions;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>Source-level rules for the browser engine (docs/PHASE3_PLAN.md section 9, ADR-030).</summary>
public class BrowserEngineSourceTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    private static string AutomationSource => Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Automation");

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Theory]
    [InlineData("EvaluateAsync")]
    [InlineData("EvaluateHandleAsync")]
    [InlineData("EvalOnSelector")]
    [InlineData("AddInitScriptAsync")]
    [InlineData("ExposeFunctionAsync")]
    [InlineData("ExposeBindingAsync")]
    [InlineData("AddScriptTagAsync")]
    [InlineData("SetContentAsync")]
    [InlineData("RegisterAsync")]
    public void The_engine_never_executes_or_injects_script(string api)
    {
        var offenders = SourceFiles(AutomationSource)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), $@"\.{api}\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_network_layer_has_no_playwright_dependency()
    {
        var offenders = SourceFiles(Path.Combine(AutomationSource, "Network"))
            .Where(f => File.ReadAllText(f).Contains("Microsoft.Playwright", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_browser_launch_disables_the_sandbox_or_the_proxy_in_source()
    {
        var text = string.Concat(SourceFiles(AutomationSource).Select(File.ReadAllText));

        Assert.DoesNotContain("--no-sandbox", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-proxy-server", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--proxy-bypass-list=*", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AutomataFlow.Domain")]
    [InlineData("AutomataFlow.Application")]
    [InlineData("AutomataFlow.Infrastructure")]
    [InlineData("AutomataFlow.Api")]
    [InlineData("AutomataFlow.Worker")]
    [InlineData("DemoCrm")]
    public void Only_the_automation_project_references_playwright(string project)
    {
        Assert.DoesNotContain("Microsoft.Playwright", Graph.Packages(project));
        Assert.Contains("Microsoft.Playwright", Graph.Packages("AutomataFlow.Automation"));
    }
}
