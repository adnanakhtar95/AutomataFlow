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
}
