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
    };

    [Theory]
    [InlineData("AutomataFlow.Domain")]
    [InlineData("AutomataFlow.Application")]
    [InlineData("AutomataFlow.Infrastructure")]
    [InlineData("AutomataFlow.Automation")]
    [InlineData("AutomataFlow.Api")]
    [InlineData("AutomataFlow.Worker")]
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
