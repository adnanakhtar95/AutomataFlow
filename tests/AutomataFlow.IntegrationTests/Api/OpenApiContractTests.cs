using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutomataFlow.IntegrationTests.Support;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>
/// The dashboard's typed API client is generated from a committed snapshot of the Api's OpenAPI document
/// (<c>web/automataflow-web/openapi/automataflow-api.json</c>, ADR-076). This test fails as soon as the Api's contract
/// drifts from that snapshot, so the frontend can never compile against a contract the backend no longer serves.
/// Update mode (explicit, never silent): set <c>AUTOMATAFLOW_OPENAPI_OUTPUT</c> to a directory; the current document is
/// written there (see <c>scripts/update-openapi.ps1</c>), and the comparison is skipped for that run.
/// </summary>
public class OpenApiContractTests
{
    public static string SnapshotPath => Path.Combine(RepositoryRoot(), "web", "automataflow-web", "openapi", "automataflow-api.json");

    [Fact]
    public async Task The_committed_OpenAPI_snapshot_matches_the_contract_the_Api_serves()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, "Development");
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = Normalize(await response.Content.ReadAsStringAsync(CancellationToken.None));

        if (Environment.GetEnvironmentVariable("AUTOMATAFLOW_OPENAPI_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "automataflow-api.json"), actual);
            return;
        }

        Assert.True(File.Exists(SnapshotPath), $"Missing OpenAPI snapshot {SnapshotPath}: run scripts/update-openapi.ps1.");
        var committed = Normalize(await File.ReadAllTextAsync(SnapshotPath));
        Assert.True(committed == actual, "The Api's OpenAPI contract differs from the committed snapshot used by the dashboard. "
            + "Run scripts/update-openapi.ps1, review the diff, and regenerate the frontend types (npm run generate:api).");
    }

    [Fact]
    public async Task Enums_are_described_as_strings_like_the_Api_serializes_them()
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(SnapshotPath))!;
        var jobStatus = document["components"]!["schemas"]!["JobStatus"]!;

        Assert.Equal("string", (string?)jobStatus["type"]);
        Assert.Contains("CompletedWithErrors", jobStatus["enum"]!.AsArray().Select(v => (string?)v));
    }

    /// <summary>Indented, LF line endings: identical text for identical documents on every machine.</summary>
    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutomataFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
