using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 5 step 8a: platform-operator fence confirmation (§7.5.6) and explicit requeue (§7.5.7) through the real Api.</summary>
public sealed class PlatformOperationsTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    private static readonly AttemptSnapshot Snapshot = new("sha256:" + new string('a', 64), 1, 120, false, true, null);

    /// <summary>A registered incarnation with one attempt that checkpointed and completed WITHOUT a completed barrier: unfenced.</summary>
    private async Task<(WorkerIncarnation Incarnation, Guid Org, Guid Operation)> UnfencedAttemptAsync()
    {
        var journal = api.Factory.Services.GetRequiredService<IAttemptJournal>();
        var incarnation = new WorkerIncarnation(new WorkerIncarnationId(Guid.NewGuid()), "host-x", "boot-x", 77, DateTimeOffset.UtcNow,
            new ContainmentUnit(ContainmentKind.LinuxCgroup, "/workers/" + Guid.NewGuid().ToString("N")), "/ledger");
        await journal.RegisterIncarnationAsync(incarnation, default);
        var (org, operation) = (Guid.NewGuid(), Guid.NewGuid());
        var reserved = await journal.ReserveAsync(org, operation, ExecutionIntent.Normal, incarnation.Id, default);
        await journal.StartAsync(reserved.Reservation!, Snapshot, default);
        await journal.SideEffectStartingAsync(reserved.Reservation!, "submit", TimeSpan.FromSeconds(30), default);
        await journal.CompleteAsync(reserved.Reservation!, new AttemptCompletion(WorkflowOutcome.Indeterminate, null, SideEffectState.Started, ExecutionFenced: false, null), default);
        return (incarnation, org, operation);
    }

    private static object Confirm(string reference, bool attested = true, string method = "HostPoweredOffOrDestroyed") =>
        new { method, evidenceReference = reference, attestedPersonally = attested };

    [Fact]
    public async Task Only_platform_operators_may_list_incarnations_or_confirm_fences()
    {
        var (_, owner) = await api.RegisterAsync();
        var (incarnation, _, _) = await UnfencedAttemptAsync();
        var client = api.Client(owner.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/platform/incarnations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{incarnation.Id.Value}/fence", Confirm("INC-1234 VM deleted in console"))).StatusCode);
    }

    [Fact]
    public async Task An_operator_confirmation_is_validated_audited_and_fences_the_incarnation()
    {
        var (_, operatorTokens) = await api.RegisterAsync("operator");
        var operatorId = Guid.Parse(new JsonWebTokenHandler().ReadJsonWebToken(operatorTokens.AccessToken).Subject);
        await api.GrantPlatformOperatorAsync(operatorId);
        var (incarnation, org, operation) = await UnfencedAttemptAsync();
        var client = api.Client(operatorTokens.AccessToken);

        var listed = JsonNode.Parse(await client.GetStringAsync("/api/v1/platform/incarnations"))!.AsArray();
        Assert.Equal(1, listed.Single(i => i!["id"]!.GetValue<Guid>() == incarnation.Id.Value)!["unfencedAttempts"]!.GetValue<int>());

        var tooShort = await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{incarnation.Id.Value}/fence", Confirm("short"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
        Assert.Contains("evidence-reference-invalid", await tooShort.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var notAttested = await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{incarnation.Id.Value}/fence", Confirm("INC-4821: VM deleted in the console", attested: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAttested.StatusCode);
        var supervisorOnly = await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{incarnation.Id.Value}/fence", Confirm("INC-4821: VM deleted in the console", method: "SupervisorUnitEmptyAndDrained"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, supervisorOnly.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{Guid.NewGuid()}/fence", Confirm("INC-4821: VM deleted in the console"))).StatusCode);

        var accepted = await client.PostAsJsonAsync($"/api/v1/platform/incarnations/{incarnation.Id.Value}/fence", Confirm("INC-4821: VM deleted in the console"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!["fencedAttempts"]!.GetValue<int>());

        var (history, _) = await api.Factory.Services.GetRequiredService<IAttemptJournal>().ReadHistoryAsync(org, operation, default);
        Assert.Equal(FenceSource.HumanConfirmed, Assert.Single(history).FenceSource);

        await using var db = api.Database.Context(null);
        var audit = await db.FenceConfirmations.Where(f => f.IncarnationId == incarnation.Id.Value).OrderBy(f => f.Id).ToListAsync();
        Assert.Equal([false, false, false, true], audit.Select(a => a.Accepted));
        Assert.All(audit, a => Assert.Equal(operatorId.ToString(), a.ActorUserId));
    }

    [Fact]
    public async Task Requeue_is_explicit_admin_only_and_limited_to_records_that_need_review()
    {
        var (_, owner) = await api.RegisterAsync();
        var (_, other) = await api.RegisterAsync();
        var version = await JobApiTests.PublishedVersionAsync(api.Client(owner.AccessToken), "requeue");
        var created = await JobApiTests.UploadAsync(api.Client(owner.AccessToken), version, JobApiTests.Header + "\n" + JobApiTests.Row(1) + "\n" + JobApiTests.Row(2) + "\n");
        var jobId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<Guid>();
        Guid review, pending;
        await using (var db = api.Database.Context(owner.OrganizationId))
        {
            var records = await db.JobRecords.Where(r => r.JobId == jobId).OrderBy(r => r.RowNumber).ToListAsync();
            (review, pending) = (records[0].Id, records[1].Id);
            await db.JobRecords.Where(r => r.Id == review).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.NeedsReview).SetProperty(r => r.ErrorCode, "FenceNotConfirmed"));
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.CompletedWithErrors));
        }

        var operatorUser = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(operatorUser.AccessToken).PostAsync($"/api/v1/jobs/{jobId}/records/{review}/requeue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(other.AccessToken).PostAsync($"/api/v1/jobs/{jobId}/records/{review}/requeue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client(owner.AccessToken).PostAsync($"/api/v1/jobs/{jobId}/records/{pending}/requeue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Client(owner.AccessToken).PostAsync($"/api/v1/jobs/{jobId}/records/{review}/requeue", null)).StatusCode);

        await using var check = api.Database.Context(owner.OrganizationId);
        Assert.Equal(JobRecordStatus.Pending, (await check.JobRecords.SingleAsync(r => r.Id == review)).Status);
        Assert.Equal(JobStatus.Running, (await check.Jobs.SingleAsync(j => j.Id == jobId)).Status);
    }
}
