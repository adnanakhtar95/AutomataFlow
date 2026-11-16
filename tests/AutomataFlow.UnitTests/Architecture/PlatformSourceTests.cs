namespace AutomataFlow.UnitTests.Architecture;

/// <summary>Phase 5 source rules for the platform projects (docs/ARCHITECTURE.md §7.1).</summary>
public class PlatformSourceTests
{
    /// <summary>The only reviewed places allowed to bypass the query-filter backstop (named cases, §7.1 layer 3).</summary>
    private static readonly string[] IgnoreQueryFiltersAllowList =
    [
        // Sign-in lists the authenticated user's own memberships across organizations (always filtered by user id).
        "src/AutomataFlow.Infrastructure/Identity/AccountService.cs",
    ];

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void IgnoreQueryFilters_is_used_only_in_reviewed_named_places()
    {
        var offenders = ProductionSources()
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(ProjectGraph.RepositoryRoot, f).Replace('\\', '/'))
            .Where(f => !IgnoreQueryFiltersAllowList.Contains(f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_query_filter_test_hook_is_never_set_by_production_code()
    {
        var offenders = ProductionSources()
            .Where(f => !f.EndsWith("AutomataFlowDbContext.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("QueryFiltersDisabled", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// ADR-058/073: no production code may claim containment without a real supervisor handshake. Until step 7 delivers
    /// the platform implementations, src contains the port only; the test handshake lives in the test-only host.
    /// </summary>
    [Fact]
    public void No_production_code_implements_the_containment_handshake_yet()
    {
        var implementations = ProductionSources()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"(class|record)\s+\w+[^{;]*:[^{;]*\bIContainmentHandshake\b"))
            .ToList();
        Assert.Empty(implementations);
    }
}