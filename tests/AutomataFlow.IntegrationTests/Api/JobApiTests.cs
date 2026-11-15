using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 5 step 4: CSV/JSON upload and idempotent job creation through the real Api.</summary>
public sealed class JobApiTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    public const string Header = "firstName,lastName,email,phone,company,country,customerType";

    public static readonly Dictionary<string, string> Parameters = new() { ["crmBaseUrl"] = "http://127.0.0.1:5080", ["crmUser"] = "agent@democrm.test" };

    public static string Row(int n, string? email = null) => $"Ada,Lovelace,{email ?? $"ada{n}@example.test"},+1 555 {1000 + n},Analytical Engines,Canada,Premium";

    /// <summary>Creates and publishes the DemoCrm sample (or another definition) and returns its version id.</summary>
    public static async Task<Guid> PublishedVersionAsync(HttpClient client, string name, string? definition = null)
    {
        var (workflow, version, _) = await WorkflowApiTests.CreateAsync(client, name, definition ?? WorkflowApiTests.Sample);
        var publish = await client.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        return version;
    }

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid version, string content, string fileName = "records.csv", string contentType = "text/csv",
        Dictionary<string, string>? parameters = null, string? idempotencyKey = null, bool ignoreUnknownColumns = false)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(version.ToString()), "workflowVersionId");
        form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(parameters ?? Parameters)), "parameters");
        form.Add(new StringContent("""{"maxConcurrency":2,"maxAttempts":3,"retryBackoffSeconds":1}"""), "options");
        if (ignoreUnknownColumns)
        {
            form.Add(new StringContent("true"), "ignoreUnknownColumns");
        }

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs") { Content = form };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonNode> BodyAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_csv_upload_creates_pending_and_invalid_records_with_unique_operation_ids_and_no_values_in_errors()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "import");
        var csv = string.Join("\n", Header, Row(1), Row(2, "not-an-email"), Row(3)) + "\n";

        var response = await UploadAsync(client, version, csv);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyAsync(response);
        Assert.Equal((3, 1), (created["totalRecords"]!.GetValue<int>(), created["invalidRecords"]!.GetValue<int>()));
        var jobId = created["jobId"]!.GetValue<Guid>();

        var job = await BodyAsync(await client.GetAsync($"/api/v1/jobs/{jobId}"));
        Assert.Equal((2, 1), (job["recordsByStatus"]!["Pending"]!.GetValue<int>(), job["recordsByStatus"]!["Invalid"]!.GetValue<int>()));

        var recordsText = await client.GetStringAsync($"/api/v1/jobs/{jobId}/records");
        Assert.DoesNotContain("not-an-email", recordsText, StringComparison.Ordinal);
        Assert.DoesNotContain("ada1@example.test", recordsText, StringComparison.Ordinal); // inputs are never returned
        var records = JsonNode.Parse(recordsText)!.AsArray();
        Assert.Equal(3, records.Select(r => r!["operationId"]!.GetValue<Guid>()).Distinct().Count());
        var invalid = records.Single(r => r!["status"]!.GetValue<string>() == "Invalid")!;
        Assert.Equal(2, invalid["rowNumber"]!.GetValue<int>());
        Assert.Contains("email", invalid["validationErrors"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_idempotency_key_replays_the_same_job_and_refuses_a_different_request()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "idempotent");
        var csv = Header + "\n" + Row(1) + "\n";

        var first = await UploadAsync(client, version, csv, idempotencyKey: "upload-1");
        var second = await UploadAsync(client, version, csv, idempotencyKey: "upload-1");
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var (a, b) = (await BodyAsync(first), await BodyAsync(second));
        Assert.Equal(a["jobId"]!.GetValue<Guid>(), b["jobId"]!.GetValue<Guid>());
        Assert.True(b["replayed"]!.GetValue<bool>());

        var different = await UploadAsync(client, version, Header + "\n" + Row(2) + "\n", idempotencyKey: "upload-1");
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);

        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.Equal(1, await db.Jobs.CountAsync());
        Assert.Equal(1, await db.JobRecords.CountAsync());
    }

    [Fact]
    public async Task Concurrent_uploads_with_one_key_create_exactly_one_job()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "concurrent");
        var csv = Header + "\n" + string.Join("\n", Enumerable.Range(1, 50).Select(i => Row(i))) + "\n";

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => UploadAsync(api.Client(owner.AccessToken), version, csv, idempotencyKey: "race")));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        var ids = await Task.WhenAll(responses.Select(async r => (await BodyAsync(r))["jobId"]!.GetValue<Guid>()));
        Assert.Single(ids.Distinct());

        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.Equal(1, await db.Jobs.CountAsync());
        Assert.Equal(50, await db.JobRecords.CountAsync());
    }

    [Fact]
    public async Task A_malformed_file_creates_nothing()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "atomic");
        var csv = Header + "\n" + string.Join("\n", Enumerable.Range(1, 700).Select(i => Row(i))) + "\n\"broken,row\n";

        var response = await UploadAsync(client, version, csv);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("quote-unterminated", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.Equal(0, await db.Jobs.CountAsync());
        Assert.Equal(0, await db.JobRecords.CountAsync()); // the first 500-row batch was rolled back too
    }

    [Fact]
    public async Task Large_files_are_persisted_in_batches()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "batched");
        var csv = Header + "\n" + string.Join("\n", Enumerable.Range(1, 1_234).Select(i => Row(i))) + "\n";

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, version, csv)).StatusCode);
        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.Equal(1_234, await db.JobRecords.CountAsync());
        Assert.Equal(1_234, await db.JobRecords.Select(r => r.OperationId).Distinct().CountAsync());
        Assert.Equal(1_234, (await db.Jobs.SingleAsync()).TotalRecords);
    }

    [Theory]
    [InlineData("crmBaseUrl", "ftp://crm.example.test", "parameter-invalid-url")]
    [InlineData("crmBaseUrl", null, "parameter-missing")]
    [InlineData("extra", "x", "parameter-unknown")]
    public async Task Parameters_must_match_the_declarations(string name, string? value, string code)
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var version = await PublishedVersionAsync(client, "params");
        var parameters = new Dictionary<string, string>(Parameters);
        if (value is null)
        {
            parameters.Remove(name);
        }
        else
        {
            parameters[name] = value;
        }

        var response = await UploadAsync(client, version, Header + "\n" + Row(1) + "\n", parameters: parameters);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_published_versions_and_accepted_file_types_can_be_used()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var (_, draft, _) = await WorkflowApiTests.CreateAsync(client, "draft-only", WorkflowApiTests.Sample);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, draft, Header + "\n" + Row(1) + "\n")).StatusCode);

        var version = await PublishedVersionAsync(client, "types");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadAsync(client, version, "MZ", "tool.exe", "application/octet-stream")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadAsync(client, version, Header, "records.csv", "application/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, version, Header + ",expected\n" + Row(1) + ",created\n")).StatusCode);

        var json = "[" + string.Join(",", Enumerable.Range(1, 2).Select(i => $$"""{"firstName":"Ada","lastName":"L","email":"j{{i}}@example.test","phone":"+1 555 900{{i}}","country":"Canada","customerType":"Premium"}""")) + "]";
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, version, json, "records.json", "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, version, Header + ",expected\n" + Row(9) + ",created\n", ignoreUnknownColumns: true)).StatusCode);
    }

    [Fact]
    public async Task Sensitive_inputs_are_encrypted_at_rest()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var definition = JsonNode.Parse(WorkflowApiTests.Sample)!;
        definition["inputs"]!.AsArray().Single(i => (string?)i!["name"] == "phone")!["sensitive"] = true;
        definition["maskSelectors"] = JsonNode.Parse("""[{"testId":"input-phone"}]""");
        var version = await PublishedVersionAsync(client, "sensitive", definition.ToJsonString());

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, version, Header + "\n" + Row(77) + "\n")).StatusCode);
        await using var db = api.Database.Context(owner.OrganizationId);
        var record = await db.JobRecords.SingleAsync();
        Assert.True(record.InputProtected);
        Assert.DoesNotContain("+1 555 1077", record.InputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ada77", record.InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewers_cannot_create_jobs()
    {
        var (_, owner) = await api.RegisterAsync();
        var version = await PublishedVersionAsync(api.Client(owner.AccessToken), "viewer");
        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(api.Client(viewer.AccessToken), version, Header + "\n" + Row(1) + "\n")).StatusCode);
    }
}
