using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Security;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 5 step 3: workflow lifecycle, hash verification, publication policy and secrets through the real Api.</summary>
public sealed class WorkflowApiTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    public const string Simple = """
        {"schemaVersion":1,"name":"simple-flow","allowedHosts":["https://crm.example.test"],
         "steps":[{"id":"open","type":"navigate","config":{"url":"https://crm.example.test/"}},
                  {"id":"done","type":"captureResult","config":{"outcome":{"status":"succeeded"}}}]}
        """;

    public static string Sample => File.ReadAllText(Path.Combine(PlatformApi.RepositoryRoot, "docs", "workflows", "democrm-register-customer.json"));

    public static object Create(string name, string definition) => new { name, definition = JsonNode.Parse(definition) };

    public static async Task<(Guid Workflow, Guid Version, string Hash)> CreateAsync(HttpClient client, string name, string definition)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workflows", Create(name, definition));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (body["workflowId"]!.GetValue<Guid>(), body["version"]!["id"]!.GetValue<Guid>(), body["version"]!["definitionHash"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_workflow_goes_from_draft_to_published_to_archived_and_published_versions_are_immutable()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var (workflow, version, hash) = await CreateAsync(client, "lifecycle", Simple);
        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);

        var edited = Simple.Replace("https://crm.example.test/\"", "https://crm.example.test/start\"", StringComparison.Ordinal);
        var update = await client.PutAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}", new { definition = JsonNode.Parse(edited) });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.NotEqual(hash, JsonNode.Parse(await update.Content.ReadAsStringAsync())!["version"]!["definitionHash"]!.GetValue<string>());

        var publish = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = false });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal("Published", JsonNode.Parse(await publish.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}", new { definition = JsonNode.Parse(Simple) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = false })).StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions", new { definition = JsonNode.Parse(Simple) });
        Assert.Equal(2, JsonNode.Parse(await second.Content.ReadAsStringAsync())!["version"]!["versionNumber"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/workflows/{workflow}/versions/{version}/archive", null)).StatusCode);

        await using var db = api.Database.Context(owner.OrganizationId);
        var stored = await db.WorkflowVersions.SingleAsync(v => v.Id == version);
        Assert.Contains("crm.example.test/start", stored.DefinitionJson, StringComparison.Ordinal); // the edited draft was published
        Assert.NotNull(stored.PublishedBy);
        Assert.Null(stored.AcknowledgedAttestations);
        Assert.Equal(0, await db.WorkflowInputs.CountAsync(i => i.WorkflowVersionId == version)); // Simple declares no inputs
    }

    [Fact]
    public async Task Invalid_definitions_are_rejected_with_paths_and_codes()
    {
        var (_, owner) = await api.RegisterAsync();
        var response = await api.Client(owner.AccessToken).PostAsJsonAsync("/api/v1/workflows",
            Create("broken", """{"schemaVersion":1,"name":"broken","allowedHosts":["https://crm.example.test"],"steps":[{"id":"x","type":"script","config":{}}]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsArray();
        Assert.Contains(errors, e => e!["path"]!.GetValue<string>().StartsWith("$.steps[0]", StringComparison.Ordinal));

        var missing = await api.Client(owner.AccessToken).PostAsJsonAsync("/api/v1/workflows", new { name = "nothing" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Roles_gate_authoring_and_publication()
    {
        var (_, owner) = await api.RegisterAsync();
        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        var operatorUser = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(viewer.AccessToken).PostAsJsonAsync("/api/v1/workflows", Create("v", Simple))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(viewer.AccessToken).GetAsync("/api/v1/workflows")).StatusCode);

        var (workflow, version, _) = await CreateAsync(api.Client(operatorUser.AccessToken), "by-operator", Simple);
        var publish = await api.Client(operatorUser.AccessToken).PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true });
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
    }

    [Fact]
    public async Task Attestations_must_be_acknowledged_explicitly_and_are_recorded_with_the_publisher()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var (workflow, version, _) = await CreateAsync(client, "democrm", Sample);

        var unacknowledged = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = false });
        Assert.Equal(HttpStatusCode.BadRequest, unacknowledged.StatusCode);
        Assert.Contains("attestation-not-acknowledged", await unacknowledged.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var acknowledged = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal("TargetEnforcedIdempotency", JsonNode.Parse(await acknowledged.Content.ReadAsStringAsync())!["acknowledgedAttestations"]!.GetValue<string>());

        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.Equal(7, await db.WorkflowInputs.CountAsync(i => i.WorkflowVersionId == version)); // denormalized inputs of the sample
    }

    [Fact]
    public async Task A_stored_definition_that_no_longer_matches_its_hash_is_refused()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var (workflow, version, _) = await CreateAsync(client, "tampered", Simple);
        await using (var db = api.Database.Context(owner.OrganizationId))
        {
            await db.WorkflowVersions.Where(v => v.Id == version)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.DefinitionJson, Simple.Replace("crm.example.test/\"", "crm.example.test/admin\"", StringComparison.Ordinal)));
        }

        var publish = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = false });
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Contains("WorkflowTampered", await publish.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_values_are_write_only_encrypted_at_rest_and_bound_to_their_organization()
    {
        const string Value = "Fictional-CRM-Password-2026!";
        var (_, owner) = await api.RegisterAsync();
        var (_, other) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/secrets/crmPassword", new { value = Value })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/secrets/bad-name", new { value = Value })).StatusCode);

        var list = await client.GetStringAsync("/api/v1/secrets");
        Assert.Contains("crmPassword", list, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, list, StringComparison.Ordinal);

        string payload;
        await using (var db = api.Database.Context(owner.OrganizationId))
        {
            payload = (await db.OrganizationSecrets.SingleAsync()).ProtectedValue;
            Assert.DoesNotContain(Value, payload, StringComparison.Ordinal);
        }

        using var scope = api.Factory.Services.CreateScope();
        var protection = scope.ServiceProvider.GetRequiredService<ValueProtection>();
        Assert.Equal(Value, protection.UnprotectSecret(owner.OrganizationId, payload));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protection.UnprotectSecret(other.OrganizationId, payload));

        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(other.AccessToken).DeleteAsync("/api/v1/secrets/crmPassword")).StatusCode);
        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(viewer.AccessToken).GetAsync("/api/v1/secrets")).StatusCode);
    }
}

/// <summary>Hosted mode: attestations may be published only by an Owner (ADR-045, docs/WORKFLOWS.md §9).</summary>
public sealed class HostedPublicationTests
{
    [Fact]
    public async Task In_hosted_mode_only_an_owner_may_publish_attestations()
    {
        var api = new PlatformApi { Settings = new Dictionary<string, string?> { ["Platform:Mode"] = "Hosted" } };
        await api.InitializeAsync();
        try
        {
            var (_, owner) = await api.RegisterAsync();
            var admin = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Admin);
            var adminClient = api.Client(admin.AccessToken);

            var (workflow, version, _) = await WorkflowApiTests.CreateAsync(adminClient, "attested", WorkflowApiTests.Sample);
            var denied = await adminClient.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains("attestation-requires-owner", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Without attestations, an Admin publishes as usual; the Owner publishes the attested one.
            var (plain, plainVersion, _) = await WorkflowApiTests.CreateAsync(adminClient, "plain", WorkflowApiTests.Simple);
            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/workflows/{plain}/versions/{plainVersion}/publish", new { acknowledgeAttestations = false })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Client(owner.AccessToken).PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true })).StatusCode);
        }
        finally
        {
            await api.DisposeAsync();
        }
    }
}

/// <summary>
/// Cross-tenant access to workflows and secrets (§7.1 layer 6). Runs against a normal Api AND against an Api whose
/// query-filter backstop is OFF, proving the application layer scopes every access on its own.
/// </summary>
public abstract class WorkflowCrossTenantTests(PlatformApi api)
{
    [Fact]
    public async Task Another_organization_sees_404_for_every_workflow_operation_and_lists_never_leak()
    {
        var (_, a) = await api.RegisterAsync("tenant-a");
        var (_, b) = await api.RegisterAsync("tenant-b");
        var (workflow, version, _) = await WorkflowApiTests.CreateAsync(api.Client(a.AccessToken), "a-only", WorkflowApiTests.Simple);
        await api.Client(a.AccessToken).PutAsJsonAsync("/api/v1/secrets/aSecret", new { value = "fictional-a" });
        var asB = api.Client(b.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, (await asB.GetAsync($"/api/v1/workflows/{workflow}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.GetAsync($"/api/v1/workflows/{workflow}/versions/{version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.PutAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}", new { definition = JsonNode.Parse(WorkflowApiTests.Simple) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions", new { definition = JsonNode.Parse(WorkflowApiTests.Simple) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.PostAsync($"/api/v1/workflows/{workflow}/versions/{version}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.DeleteAsync("/api/v1/secrets/aSecret")).StatusCode);

        var list = JsonSerializer.Deserialize<JsonArray>(await asB.GetStringAsync("/api/v1/workflows"))!;
        Assert.DoesNotContain(list, w => w!["id"]!.GetValue<Guid>() == workflow);
        Assert.DoesNotContain("aSecret", await asB.GetStringAsync("/api/v1/secrets"), StringComparison.Ordinal);

        await using var db = api.Database.Context(a.Organization());
        Assert.Equal("Draft", (await db.WorkflowVersions.SingleAsync(v => v.Id == version)).Status.ToString());
    }
}

internal static class TokenExtensions
{
    public static Guid Organization(this TokenDto tokens) => tokens.OrganizationId;
}

public sealed class WorkflowCrossTenantWithBackstop(PlatformApi api) : WorkflowCrossTenantTests(api), IClassFixture<PlatformApi>;

public sealed class FiltersOffApi : IAsyncLifetime
{
    public PlatformApi Api { get; } = new() { Settings = new Dictionary<string, string?> { [ApiFactory.DisableQueryFiltersSetting] = "true" } };

    public Task InitializeAsync() => Api.InitializeAsync();

    public Task DisposeAsync() => Api.DisposeAsync();
}

public sealed class WorkflowCrossTenantWithoutBackstop(FiltersOffApi fixture) : WorkflowCrossTenantTests(fixture.Api), IClassFixture<FiltersOffApi>
{
    [Fact]
    public void This_host_really_runs_without_the_query_filter_backstop()
    {
        using var scope = fixture.Api.Factory.Services.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<AutomataFlow.Infrastructure.Persistence.AutomataFlowDbContext>().QueryFiltersDisabled);
    }
}
