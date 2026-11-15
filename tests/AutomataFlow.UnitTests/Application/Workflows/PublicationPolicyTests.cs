using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Publication;
using AutomataFlow.Domain.Organizations;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 5 step 3: who may publish attestations (hosted-mode restriction).</summary>
public class PublicationPolicyTests
{
    private static string RepositoryRoot => AutomataFlow.UnitTests.Architecture.ProjectGraph.RepositoryRoot;

    private static WorkflowDefinition Load(string json)
    {
        var result = ValidatedWorkflow.CreateForPublication(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Path + " " + e.Code)));
        return result.Workflow!.Definition;
    }

    private static string Sample => File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "workflows", "democrm-register-customer.json"));

    [Fact]
    public void Attestations_are_detected_from_the_definition()
    {
        Assert.Equal([Attestation.TargetEnforcedIdempotency], PublicationPolicy.AttestationsOf(Load(Sample)).ToArray());

        var root = JsonNode.Parse(Sample)!.AsObject();
        root["steps"]!.AsArray().Single(s => (string?)s!["id"] == "submit")!.AsObject().Remove("idempotency");
        root["steps"]!.AsArray().Remove(root["steps"]!.AsArray().Single(s => (string?)s!["id"] == "operation"));
        root["reconciliation"] = JsonNode.Parse("""{"evidence":"authoritative","matchKey":"businessKey","authority":{"maxCommitLatencySeconds":10,"contractReference":"docs/DEMO_CRM.md commit-latency contract"}}""");
        root["reconcile"]![0]!["config"]!["url"] = "{{params.crmBaseUrl}}/Customers/Search?Field=Email&Term={{record.email}}";
        var match = root["reconcile"]![1]!["config"]!;
        match["key"] = JsonNode.Parse("""{"text":{"testId":"customer-email"}}""");
        match["equals"] = "{{record.email}}";
        Assert.Equal([Attestation.AuthoritativeEvidence, Attestation.BusinessKeyMatch], PublicationPolicy.AttestationsOf(Load(root.ToJsonString())).ToArray());
    }

    [Theory]
    [InlineData(PlatformMode.SelfHosted, OrganizationRole.Operator, false, true, PublicationDecision.RoleTooLow)]
    [InlineData(PlatformMode.SelfHosted, OrganizationRole.Admin, false, false, PublicationDecision.Allowed)]
    [InlineData(PlatformMode.SelfHosted, OrganizationRole.Admin, true, false, PublicationDecision.AttestationNotAcknowledged)]
    [InlineData(PlatformMode.SelfHosted, OrganizationRole.Admin, true, true, PublicationDecision.Allowed)]
    [InlineData(PlatformMode.Hosted, OrganizationRole.Admin, false, false, PublicationDecision.Allowed)]
    [InlineData(PlatformMode.Hosted, OrganizationRole.Admin, true, true, PublicationDecision.AttestationRequiresOwner)]
    [InlineData(PlatformMode.Hosted, OrganizationRole.Owner, true, false, PublicationDecision.AttestationNotAcknowledged)]
    [InlineData(PlatformMode.Hosted, OrganizationRole.Owner, true, true, PublicationDecision.Allowed)]
    [InlineData(PlatformMode.Development, OrganizationRole.Viewer, false, false, PublicationDecision.RoleTooLow)]
    public void The_publication_matrix(PlatformMode mode, OrganizationRole role, bool attested, bool acknowledged, PublicationDecision expected)
    {
        ImmutableArray<Attestation> attestations = attested ? [Attestation.TargetEnforcedIdempotency] : [];
        Assert.Equal(expected, PublicationPolicy.Decide(mode, role, attestations, acknowledged));
    }
}
