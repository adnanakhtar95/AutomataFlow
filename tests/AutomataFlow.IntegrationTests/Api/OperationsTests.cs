using System.Net;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 10: platform operations view and artifact retention.</summary>
public sealed class OperationsTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    private async Task<TokenDto> OperatorAsync()
    {
        var (_, tokens) = await api.RegisterAsync("ops");
        await api.GrantPlatformOperatorAsync(Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken).Subject));
        return tokens;
    }

    [Fact]
    public async Task Operators_see_queue_depth_and_customers_cannot()
    {
        var operatorTokens = await OperatorAsync();
        var before = JsonNode.Parse(await api.Client(operatorTokens.AccessToken).GetStringAsync("/api/v1/platform/operations"))!;

        var (_, owner) = await api.RegisterAsync();
        var asOwner = api.Client(owner.AccessToken);
        var version = await JobApiTests.PublishedVersionAsync(asOwner, "queue");
        await JobApiTests.UploadAsync(asOwner, version, string.Join("\n", JobApiTests.Header, JobApiTests.Row(1), JobApiTests.Row(2), JobApiTests.Row(3)) + "\n");

        var after = JsonNode.Parse(await api.Client(operatorTokens.AccessToken).GetStringAsync("/api/v1/platform/operations"))!;
        Assert.Equal(before["queue"]!["readyRecords"]!.GetValue<int>() + 3, after["queue"]!["readyRecords"]!.GetValue<int>());
        Assert.Equal(before["queue"]!["activeJobs"]!.GetValue<int>() + 1, after["queue"]!["activeJobs"]!.GetValue<int>());
        Assert.NotNull(after["queue"]!["oldestReadyAgeSeconds"]);
        Assert.Equal(24, after["metrics"]!["windowHours"]!.GetValue<int>());
        Assert.DoesNotContain("ada1@example.test", after.ToJsonString(), StringComparison.Ordinal); // counts only

        Assert.Equal(HttpStatusCode.Forbidden, (await asOwner.GetAsync("/api/v1/platform/operations")).StatusCode);
    }

    [Fact]
    public async Task Retention_deletes_only_expired_artifacts_in_every_workspace_and_keeps_records()
    {
        var (_, a) = await api.RegisterAsync();
        var (_, b) = await api.RegisterAsync();
        var created = new List<(Guid Org, Guid Id, string Path, bool Old)>();
        foreach (var (org, old) in new[] { (a.OrganizationId, true), (a.OrganizationId, false), (b.OrganizationId, true) })
        {
            var asOwner = api.Client(org == a.OrganizationId ? a.AccessToken : b.AccessToken);
            var version = await JobApiTests.PublishedVersionAsync(asOwner, "retention-" + Guid.NewGuid().ToString("N")[..6]);
            var job = JsonNode.Parse(await (await JobApiTests.UploadAsync(asOwner, version, JobApiTests.Header + "\n" + JobApiTests.Row(1) + "\n")).Content.ReadAsStringAsync())!["jobId"]!.GetValue<Guid>();
            await using var db = api.Database.Context(org);
            var record = await db.JobRecords.FirstAsync(r => r.JobId == job);
            var attempt = JobAttempt.Start(org, job, record.Id, 1, RecordIntent.Normal, Guid.NewGuid(), DateTimeOffset.UtcNow);
            attempt.Complete(JobAttemptStatus.Succeeded, null, null, false, true, null, DateTimeOffset.UtcNow);
            db.JobAttempts.Add(attempt);
            var key = $"{org:N}/{job:N}/{record.Id:N}/1/{Guid.NewGuid():N}.png";
            var path = Path.Combine(api.ArtifactsRoot, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var artifact = ExecutionArtifact.Create(Guid.NewGuid(), org, attempt.Id, "Screenshot", key, "s.png", "image/png", 3, "x", DateTimeOffset.UtcNow.AddDays(old ? -40 : -1));
            db.ExecutionArtifacts.Add(artifact);
            await db.SaveChangesAsync();
            created.Add((org, artifact.Id, path, old));
        }

        using var scope = api.Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ArtifactRetention>().PurgeAsync(30, CancellationToken.None);
        Assert.True(result.Rows >= 2 && result.FilesDeleted >= 2);

        foreach (var (org, id, path, old) in created)
        {
            await using var db = api.Database.Context(org);
            Assert.Equal(!old, await db.ExecutionArtifacts.AnyAsync(x => x.Id == id));
            Assert.Equal(!old, File.Exists(path));
            Assert.True(await db.JobRecords.AnyAsync()); // records stay
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scope.ServiceProvider.GetRequiredService<ArtifactRetention>().PurgeAsync(0, CancellationToken.None));
    }
}
