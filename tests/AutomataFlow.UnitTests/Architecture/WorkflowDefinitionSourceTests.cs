using System.Text.RegularExpressions;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>
/// Phase 4 step 3: the definition model, parser and canonicalizer are pure Application code without regular
/// expressions (§16 D6) and without browser, I/O or clock access.
/// </summary>
public class WorkflowDefinitionSourceTests
{
    /// <summary>Everything under Application/Workflows: definition, templates, URLs, records, binding (and later steps).</summary>
    private static string DefinitionSource =>
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Application", "Workflows");

    [Fact]
    public void The_scan_covers_every_workflow_component_folder()
    {
        var folders = Directory.EnumerateDirectories(DefinitionSource).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Superset(new HashSet<string?>(["Definition", "Templates", "Urls", "Records", "Binding", "Actions", "Execution"], StringComparer.Ordinal), folders);
    }

    /// <summary>§2: handlers and the executor reach the browser only through IBrowserSession, never Playwright; no scripting.</summary>
    [Theory]
    [InlineData(@"\bMicrosoft\.Playwright\b")]
    [InlineData(@"\b(IPage|ILocator|IBrowserContext)\b")]
    [InlineData(@"\b(EvaluateAsync|EvalOnSelector|AddInitScript|ExposeFunction)\w*")]
    [InlineData(@"\bRegex\b")]
    public void Workflow_handlers_use_only_the_browser_session(string forbiddenPattern)
    {
        var folder = Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Automation", "Workflows");
        var files = Directory.EnumerateFiles(folder, "*.cs").ToList();
        Assert.NotEmpty(files);

        Assert.Empty(files.Where(f => Regex.IsMatch(File.ReadAllText(f), forbiddenPattern)).Select(Path.GetFileName));
    }

    /// <summary>
    /// ADR-058 fail-closed rule 1: no production assembly contains an attempt journal in Phase 4 (the in-memory journal is
    /// test-only), so no real execution can rely on a non-durable journal.
    /// </summary>
    [Fact]
    public void No_production_code_implements_the_attempt_journal()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        var implementations = sources
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(class|record|struct)\s+\w+[^{;]*:[^{;]*\bIAttemptJournal\b"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(implementations);

        var loaded = new[] { typeof(AutomataFlow.Application.Workflows.Execution.IAttemptJournal).Assembly, typeof(AutomataFlow.Automation.Network.RequestGuard).Assembly };
        Assert.DoesNotContain(loaded.SelectMany(a => a.GetTypes()), t => !t.IsInterface && typeof(AutomataFlow.Application.Workflows.Execution.IAttemptJournal).IsAssignableFrom(t));
    }

    [Theory]
    [InlineData(@"\bRegex\b")]
    [InlineData(@"System\.Text\.RegularExpressions")]
    [InlineData(@"GeneratedRegex")]
    [InlineData(@"\bAutomataFlow\.(Automation|Infrastructure|Api|Worker)\b")]
    [InlineData(@"\bMicrosoft\.Playwright\b")]
    [InlineData(@"\bDemoCrm\b")]
    [InlineData(@"\bFile\.")]
    [InlineData(@"\bDirectory\.")]
    [InlineData(@"\bHttpClient\b")]
    [InlineData(@"\bEnvironment\.")]
    [InlineData(@"\bDateTime(Offset)?\.(Utc)?Now\b")]
    [InlineData(@"\bJsonSerializer\b")]
    [InlineData(@"\bJsonDocument\b")]
    public void The_definition_code_is_pure_and_regex_free(string forbiddenPattern)
    {
        var files = Directory.EnumerateFiles(DefinitionSource, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .Where(f => Regex.IsMatch(File.ReadAllText(f), forbiddenPattern))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
