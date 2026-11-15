using System.Reflection;
using System.Text.RegularExpressions;
using AutomataFlow.Application.Network;
using AutomataFlow.Automation.Network;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>
/// Phase 4 step 1 (docs/PHASE4_PLAN.md §16 D1, ADR-037): the pure host and IP policy definitions live in Application
/// so that publish-time validation (Api, via Application) uses the exact runtime parser, while all runtime enforcement
/// stays in Automation.
/// </summary>
public class NetworkPolicyPlacementTests
{
    private const string PolicyNamespace = "AutomataFlow.Application.Network";

    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    private static readonly Type[] MovedTypes =
    [
        typeof(AllowedHostRule),
        typeof(EgressPolicy),
        typeof(HostNameNormalizer),
        typeof(IpAddressPolicy),
        typeof(NormalizedHost),
    ];

    private static readonly Assembly ApplicationAssembly = typeof(EgressPolicy).Assembly;

    private static readonly Assembly AutomationAssembly = typeof(RequestGuard).Assembly;

    private static string ApplicationNetworkSource =>
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Application", "Network");

    [Fact]
    public void The_five_pure_policy_types_live_in_the_Application_network_namespace()
    {
        Assert.Equal("AutomataFlow.Application", ApplicationAssembly.GetName().Name);
        Assert.All(MovedTypes, t =>
        {
            Assert.Same(ApplicationAssembly, t.Assembly);
            Assert.Equal(PolicyNamespace, t.Namespace);
        });
    }

    [Fact]
    public void No_copy_of_a_moved_type_remains_in_Automation()
    {
        var names = MovedTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var duplicates = AutomationAssembly.GetTypes().Where(t => names.Contains(t.Name)).Select(t => t.FullName).ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Runtime_enforcement_stays_in_Automation()
    {
        Assert.Equal("AutomataFlow.Automation", AutomationAssembly.GetName().Name);
        Assert.Equal("AutomataFlow.Automation.Network", typeof(RequestGuard).Namespace);
        Assert.Same(AutomationAssembly, typeof(EgressProxyServer).Assembly);
        Assert.Same(AutomationAssembly, typeof(EgressSessionRegistry).Assembly);
    }

    [Fact]
    public void The_Application_assembly_references_no_outer_layer_or_browser_assembly()
    {
        var forbidden = new[] { "AutomataFlow.Automation", "AutomataFlow.Infrastructure", "AutomataFlow.Api", "AutomataFlow.Worker", "Microsoft.Playwright" };

        var referenced = ApplicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referenced, r => forbidden.Contains(r, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(@"\bMicrosoft\.Playwright\b")]
    [InlineData(@"\bnew\s+Socket\s*\(")]
    [InlineData(@"\bDns\.")]
    [InlineData(@"\bHttpClient\b")]
    [InlineData(@"\bFile\.")]
    [InlineData(@"\bDirectory\.")]
    [InlineData(@"\bProcess\.")]
    [InlineData(@"\bEnvironment\.")]
    [InlineData(@"\bDateTime(Offset)?\.(Utc)?Now\b")]
    public void The_moved_policy_code_is_pure(string forbiddenPattern)
    {
        var files = Directory.EnumerateFiles(ApplicationNetworkSource, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .Where(f => Regex.IsMatch(File.ReadAllText(f), forbiddenPattern))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_Api_can_reach_the_policy_types_through_Application_without_Automation()
    {
        var apiClosure = Graph.TransitiveReferences("AutomataFlow.Api");

        Assert.Contains("AutomataFlow.Application", apiClosure);
        Assert.DoesNotContain("AutomataFlow.Automation", apiClosure);
    }

    [Fact]
    public void The_old_Automation_network_files_are_gone()
    {
        var automationNetwork = Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Automation", "Network");

        Assert.False(File.Exists(Path.Combine(automationNetwork, "EgressPolicy.cs")));
        Assert.False(File.Exists(Path.Combine(automationNetwork, "HostNameNormalizer.cs")));
        Assert.False(File.Exists(Path.Combine(automationNetwork, "IpAddressPolicy.cs")));
    }
}
