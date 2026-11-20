using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.IntegrationTests.Support;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 9: workflow packages served as templates; each one is a valid, publishable definition.</summary>
public sealed class TemplatesApiTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    public static readonly string[] PackageIds = ["crm-customer-registration", "ecommerce-order-entry", "invoice-approval"];

    [Fact]
    public async Task The_shipped_packages_are_listed_and_every_one_validates()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var listed = JsonNode.Parse(await client.GetStringAsync("/api/v1/workflow-templates"))!.AsArray();
        Assert.Equal(PackageIds, listed.Select(t => t!["id"]!.GetValue<string>()));

        foreach (var id in PackageIds)
        {
            var detail = JsonNode.Parse(await client.GetStringAsync($"/api/v1/workflow-templates/{id}"))!;
            Assert.False(string.IsNullOrWhiteSpace(detail["sampleCsv"]!.GetValue<string>()));
            var definition = JsonNode.Parse(detail["definitionJson"]!.GetValue<string>());
            var validation = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/v1/workflows/validate", new { definition })).Content.ReadAsStringAsync())!;
            Assert.True(validation["valid"]!.GetValue<bool>(), $"{id}: {validation.ToJsonString()}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/workflow-templates/no-such-template")).StatusCode);
    }

    [Fact]
    public async Task A_template_becomes_a_draft_in_the_callers_workspace_that_can_be_published()
    {
        var (_, owner) = await api.RegisterAsync();
        var (_, other) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var created = await client.PostAsJsonAsync("/api/v1/workflow-templates/invoice-approval/instantiate", new { name = "Invoice approval" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var (workflowId, versionId) = (body["workflowId"]!.GetValue<Guid>(), body["version"]!["id"]!.GetValue<Guid>());
        Assert.Equal("Draft", body["version"]!["status"]!.GetValue<string>());

        var publish = await client.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/versions/{versionId}/publish", new { acknowledgeAttestations = true });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Empty(JsonNode.Parse(await api.Client(other.AccessToken).GetStringAsync("/api/v1/workflows"))!.AsArray()); // only in the caller's workspace

        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(viewer.AccessToken).PostAsJsonAsync("/api/v1/workflow-templates/invoice-approval/instantiate", new { name = "Not allowed" })).StatusCode);
    }

    [Fact]
    public void The_crm_package_is_the_verified_DemoCrm_sample_definition()
    {
        var package = File.ReadAllText(Path.Combine(PlatformApi.RepositoryRoot, "packages", "crm-customer-registration", "workflow.json"));
        Assert.Equal(WorkflowApiTests.Sample.ReplaceLineEndings("\n"), package.ReplaceLineEndings("\n"));
    }
}
