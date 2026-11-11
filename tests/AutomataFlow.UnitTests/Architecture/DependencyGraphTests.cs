namespace AutomataFlow.UnitTests.Architecture;

/// <summary>Enforces the dependency rules in CLAUDE.md and docs/ARCHITECTURE.md section 3.</summary>
public class DependencyGraphTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    private static readonly Dictionary<string, string[]> ApprovedSourceReferences = new()
    {
        ["AutomataFlow.Domain"] = [],
        ["AutomataFlow.Application"] = ["AutomataFlow.Domain"],
        ["AutomataFlow.Infrastructure"] = ["AutomataFlow.Domain", "AutomataFlow.Application"],
        ["AutomataFlow.Automation"] = ["AutomataFlow.Domain", "AutomataFlow.Application"],
        ["AutomataFlow.Api"] = ["AutomataFlow.Application", "AutomataFlow.Infrastructure"],
        ["AutomataFlow.Worker"] = ["AutomataFlow.Application", "AutomataFlow.Infrastructure", "AutomataFlow.Automation"],

        // The fictional target application is fully independent of the platform (ADR-011, ADR-025).
        ["DemoCrm"] = [],
    };

    [Theory]
    [InlineData("AutomataFlow.Domain")]
    [InlineData("AutomataFlow.Application")]
    [InlineData("AutomataFlow.Infrastructure")]
    [InlineData("AutomataFlow.Automation")]
    [InlineData("AutomataFlow.Api")]
    [InlineData("AutomataFlow.Worker")]
    [InlineData("DemoCrm")]
    public void Source_project_references_match_the_approved_graph(string project)
    {
        Assert.Equal(
            ApprovedSourceReferences[project].Order(StringComparer.Ordinal),
            Graph.DirectReferences(project).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void All_approved_source_projects_exist_and_nothing_unexpected_is_in_src()
    {
        var sourceProjects = Graph.Projects.Where(p => !p.EndsWith("Tests", StringComparison.Ordinal));

        Assert.Equal(
            ApprovedSourceReferences.Keys.Order(StringComparer.Ordinal),
            sourceProjects.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Api_never_depends_on_Automation_even_transitively()
    {
        Assert.DoesNotContain("AutomataFlow.Automation", Graph.TransitiveReferences("AutomataFlow.Api"));
        Assert.DoesNotContain("AutomataFlow.Worker", Graph.TransitiveReferences("AutomataFlow.Api"));
    }

    [Fact]
    public void Infrastructure_and_Automation_are_independent_of_each_other()
    {
        Assert.DoesNotContain("AutomataFlow.Automation", Graph.TransitiveReferences("AutomataFlow.Infrastructure"));
        Assert.DoesNotContain("AutomataFlow.Infrastructure", Graph.TransitiveReferences("AutomataFlow.Automation"));
    }

    [Theory]
    [InlineData("AutomataFlow.Domain")]
    [InlineData("AutomataFlow.Application")]
    public void Inner_layers_never_depend_on_outer_layers(string project)
    {
        var transitive = Graph.TransitiveReferences(project);

        Assert.DoesNotContain("AutomataFlow.Infrastructure", transitive);
        Assert.DoesNotContain("AutomataFlow.Automation", transitive);
        Assert.DoesNotContain("AutomataFlow.Api", transitive);
        Assert.DoesNotContain("AutomataFlow.Worker", transitive);
    }

    [Fact]
    public void Domain_and_Application_have_no_package_dependencies()
    {
        Assert.Empty(Graph.Packages("AutomataFlow.Domain"));
        Assert.Empty(Graph.Packages("AutomataFlow.Application"));
    }

    [Fact]
    public void DemoCrm_references_nothing_from_AutomataFlow_neither_projects_nor_packages()
    {
        Assert.Empty(Graph.TransitiveReferences("DemoCrm"));
        Assert.DoesNotContain(Graph.Packages("DemoCrm"), p => p.StartsWith("AutomataFlow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_AutomataFlow_source_project_references_DemoCrm()
    {
        foreach (var project in ApprovedSourceReferences.Keys)
        {
            Assert.DoesNotContain("DemoCrm", Graph.TransitiveReferences(project));
        }
    }

    [Fact]
    public void DemoCrm_tests_reference_only_DemoCrm()
    {
        Assert.Equal(["DemoCrm"], Graph.DirectReferences("DemoCrm.Tests"));
    }

    // ---- DemoCrm boundary (ADR-027) ----
    // DemoCrm is a browser-automation TARGET. Exactly two test projects may reference it:
    //   DemoCrm.Tests               - tests the portal itself
    //   AutomataFlow.AutomationTests - builds and launches it as a real-browser test target (Phase 3)
    // No production AutomataFlow project (and no other test project) may ever depend on it.

    private static readonly string[] ProjectsAllowedToReferenceDemoCrm = ["AutomataFlow.AutomationTests", "DemoCrm.Tests"];

    [Fact]
    public void Only_DemoCrm_Tests_and_AutomationTests_may_reference_DemoCrm()
    {
        var referencing = Graph.Projects
            .Where(p => !string.Equals(p, "DemoCrm", StringComparison.Ordinal))
            .Where(p => Graph.DirectReferences(p).Contains("DemoCrm"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.All(referencing, p => Assert.Contains(p, ProjectsAllowedToReferenceDemoCrm));
    }

    [Fact]
    public void No_production_project_depends_on_DemoCrm_directly_or_transitively()
    {
        var production = Graph.Projects.Where(p => !p.EndsWith("Tests", StringComparison.Ordinal) && p != "DemoCrm");

        Assert.All(production, p => Assert.DoesNotContain("DemoCrm", Graph.TransitiveReferences(p)));
    }

    [Fact]
    public void Unit_and_integration_tests_never_reference_DemoCrm()
    {
        Assert.DoesNotContain("DemoCrm", Graph.TransitiveReferences("AutomataFlow.UnitTests"));
        Assert.DoesNotContain("DemoCrm", Graph.TransitiveReferences("AutomataFlow.IntegrationTests"));
    }

    [Fact]
    public void AutomationTests_may_reference_only_Automation_and_DemoCrm()
    {
        var allowed = new[] { "AutomataFlow.Automation", "DemoCrm" };

        Assert.All(Graph.DirectReferences("AutomataFlow.AutomationTests"), r => Assert.Contains(r, allowed));
        Assert.Contains("AutomataFlow.Automation", Graph.DirectReferences("AutomataFlow.AutomationTests"));
    }

    [Fact]
    public void DemoCrm_tests_never_reference_any_AutomataFlow_project()
    {
        Assert.DoesNotContain(Graph.TransitiveReferences("DemoCrm.Tests"), p => p.StartsWith("AutomataFlow", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_Api_and_Worker_reference_Infrastructure_among_source_projects()
    {
        var referencing = ApprovedSourceReferences.Keys
            .Where(p => Graph.DirectReferences(p).Contains("AutomataFlow.Infrastructure"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(["AutomataFlow.Api", "AutomataFlow.Worker"], referencing);
    }

    [Fact]
    public void Source_projects_never_reference_test_projects()
    {
        foreach (var project in ApprovedSourceReferences.Keys)
        {
            Assert.DoesNotContain(Graph.TransitiveReferences(project), p => p.EndsWith("Tests", StringComparison.Ordinal));
        }
    }
}
