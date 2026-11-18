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
    /// ADR-058/073/075: production code claims containment ONLY through the real supervisor handshake. Exactly one production
    /// implementation exists, the Linux namespace handshake in the Worker; test handshakes live in test-only hosts.
    /// </summary>
    [Fact]
    public void The_only_production_containment_handshake_is_the_Linux_namespace_handshake()
    {
        var implementations = ProductionSources()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"(class|record)\s+\w+[^{;]*:[^{;]*\bIContainmentHandshake\b"))
            .Select(f => Path.GetRelativePath(ProjectGraph.RepositoryRoot, f).Replace('\\', '/'))
            .ToList();
        Assert.Equal(["src/AutomataFlow.Worker/LinuxNamespaceContainment.cs"], implementations);
        Assert.Contains("class LinuxNamespaceContainmentHandshake", File.ReadAllText(Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Worker", "LinuxNamespaceContainment.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-075: the supervisor must never install a file descriptor passed by a peer (SCM_RIGHTS), or a Worker could plant a
    /// socket the drain would treat as the supervisor's own. It reads with plain receives only.
    /// </summary>
    [Fact]
    public void The_supervisor_never_receives_ancillary_data()
    {
        var supervisor = Directory.EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Supervisor"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.All(supervisor, f =>
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("ReceiveMessageFrom", text, StringComparison.Ordinal);
            Assert.DoesNotContain("recvmsg", text, StringComparison.Ordinal);
        });
    }
}