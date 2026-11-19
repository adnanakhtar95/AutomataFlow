using AutomataFlow.Application.Abstractions;
using AutomataFlow.AutomationTests.DemoCrm;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>
/// Phase 5 step 6: the real Worker (claiming, PostgreSQL journal, real Chromium, egress proxy) processing jobs created by
/// the real platform services against DemoCrm, with exact customer AND registration-POST counts.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class WorkerExecutionTests(BrowserFixture fixture)
{
    private const string Header = "firstName,lastName,email,phone,company,country,customerType";
    private static int _sequence;

    private static string Row(string email) => $"Grace,Hopper,{email},+1 555 {6000 + Interlocked.Increment(ref _sequence)},Fictional Compilers,Canada,Premium";

    private static string Email(string tag = "") => "e2e" + Guid.NewGuid().ToString("N")[..20] + (tag.Length == 0 ? string.Empty : "+" + tag) + "@example.test";

    private static string Csv(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";

    private static Task<List<JobRecord>> RecordsAsync(PlatformRig rig, Guid job) =>
        rig.QueryAsync(db => db.JobRecords.AsNoTracking().Where(r => r.JobId == job).OrderBy(r => r.RowNumber).ToListAsync());

    [Fact]
    public async Task A_mixed_job_runs_end_to_end_and_persists_attempts_logs_artifacts_and_usage()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishAsync(WorkflowRig.SampleJson);
        var email = Email();
        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var job = await rig.CreateJobAsync(version, Csv(Row(email), Row(email), Row("not-an-email")), new JobOptions(1, 3, 1));

        await rig.StartWorkerAsync();
        Assert.Equal(JobStatus.CompletedWithErrors, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(2)));

        var records = await RecordsAsync(rig, job);
        Assert.Equal([JobRecordStatus.Succeeded, JobRecordStatus.Failed, JobRecordStatus.Invalid], records.Select(r => r.Status));
        Assert.Contains("customerId", records[0].ResultJson, StringComparison.Ordinal);
        Assert.Equal("DuplicateCustomer", records[1].ErrorCode);
        Assert.Equal((customers + 1, posts + 2), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));

        var attempts = await rig.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == job).ToListAsync());
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, a => a.Status == JobAttemptStatus.Succeeded && a.SideEffectStarted && a.ExecutionFenced);
        Assert.True(await rig.QueryAsync(db => db.ExecutionLogs.CountAsync(l => attempts.Select(a => a.Id).Contains(l.JobAttemptId))) > 0);
        var artifacts = await rig.QueryAsync(db => db.ExecutionArtifacts.AsNoTracking().ToListAsync());
        var screenshot = Assert.Single(artifacts, a => a.Kind == "Screenshot");
        Assert.StartsWith(rig.Organization.ToString("N") + "/", screenshot.StorageKey, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(rig.ArtifactsRoot, screenshot.StorageKey)));
        Assert.Equal(2, await rig.QueryAsync(db => db.UsageRecords.Where(u => u.Metric == UsageMetrics.AttemptsStarted).SumAsync(u => u.Quantity)));

        // No credential and no customer value in stored execution logs or in the Worker's own logs.
        var stored = (await rig.QueryAsync(db => db.ExecutionLogs.AsNoTracking().Select(l => new { l.Message, l.PropertiesJson }).ToListAsync()))
            .Select(l => l.Message + " " + l.PropertiesJson).ToList();
        foreach (var value in new[] { "DemoPass-2026!", email })
        {
            Assert.DoesNotContain(stored, s => s.Contains(value, StringComparison.Ordinal));
            Assert.DoesNotContain(rig.WorkerLogs.Lines, l => l.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Ambiguous_and_temporary_failures_are_retried_safely_through_target_enforced_idempotency()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishAsync(WorkflowRig.SampleJson);
        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var job = await rig.CreateJobAsync(version, Csv(Row(Email("commit-fail")), Row(Email("fail-1"))), new JobOptions(2, 3, 1));

        await rig.StartWorkerAsync();
        Assert.Equal(JobStatus.Completed, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(3)));

        var records = await RecordsAsync(rig, job);
        Assert.All(records, r => Assert.Equal((JobRecordStatus.Succeeded, 2), (r.Status, r.AttemptCount)));
        Assert.Contains("replayed", records[0].ResultJson, StringComparison.Ordinal); // the second POST was deduplicated by the target
        Assert.Equal((customers + 2, posts + 4), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));
    }

    [Fact]
    public async Task Cancelling_a_job_stops_further_records_without_extra_submissions()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishAsync(WorkflowRig.SampleJson);
        var posts = await fixture.DemoCrm.RegistrationSubmissionsAsync();
        var job = await rig.CreateJobAsync(version, Csv([.. Enumerable.Range(0, 6).Select(_ => Row(Email()))]), new JobOptions(1, 3, 1));

        await rig.StartWorkerAsync();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while ((await RecordsAsync(rig, job)).All(r => r.Status != JobRecordStatus.Succeeded) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var cancelled = await rig.AsOwnerAsync<ExecutionQueryService, ServiceResult<bool>>(s => s.CancelAsync(job, default));
        Assert.Equal(ServiceStatus.Ok, cancelled.Status);
        Assert.Equal(JobStatus.Cancelled, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(1)));

        var records = await RecordsAsync(rig, job);
        var succeeded = records.Count(r => r.Status == JobRecordStatus.Succeeded);

        // The record in flight when the cancellation arrived either finished, or was interrupted. Interrupted AFTER its
        // side effect started, it must become NeedsReview, never Cancelled (ADR-072: a cancelled job never hides an
        // ambiguity). Which one happens depends on where the attempt was when the heartbeat delivered the cancellation.
        var interrupted = records.Where(r => r.Status == JobRecordStatus.NeedsReview).ToList();
        Assert.True(interrupted.Count <= 1, "at most the one in-flight record (MaxConcurrency 1) can be interrupted");
        Assert.All(interrupted, r => Assert.Equal("CancelledWithOpenAmbiguity", r.ErrorCode));
        Assert.InRange(succeeded + interrupted.Count, 1, 2); // the one done before the cancel, plus at most the one in flight
        Assert.All(records.Where(r => r.Status is not (JobRecordStatus.Succeeded or JobRecordStatus.NeedsReview)), r => Assert.Equal(JobRecordStatus.Cancelled, r.Status));

        // No record that was never started reached the target, and none was submitted twice.
        Assert.InRange(await fixture.DemoCrm.RegistrationSubmissionsAsync() - posts, succeeded, succeeded + interrupted.Count);
    }

    [Fact]
    public async Task Per_job_concurrency_holds_across_two_workers()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishAsync(WorkflowRig.SampleJson);
        var job = await rig.CreateJobAsync(version, Csv([.. Enumerable.Range(0, 4).Select(_ => Row(Email()))]), new JobOptions(1, 3, 1));

        await rig.StartWorkerAsync();
        await rig.StartWorkerAsync();
        var maxRunning = 0;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var records = await RecordsAsync(rig, job);
            maxRunning = Math.Max(maxRunning, records.Count(r => r.Status == JobRecordStatus.Running));
            if (records.All(r => JobRecord.IsTerminal(r.Status)))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(JobStatus.Completed, await rig.WaitForJobAsync(job, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, maxRunning);
        var workers = await rig.QueryAsync(db => db.JobAttempts.Where(a => a.JobId == job).Select(a => a.WorkerIncarnationId).Distinct().CountAsync());
        Assert.InRange(workers, 1, 2);
    }

    [Fact]
    public async Task A_tampered_pinned_definition_is_never_executed()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishAsync(WorkflowRig.SampleJson);
        var job = await rig.CreateJobAsync(version, Csv(Row(Email())));
        await rig.QueryAsync(db => db.WorkflowVersions.Where(v => v.Id == version)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.DefinitionJson, v => v.DefinitionJson.Replace("register-submit", "admin-delete"))));
        var posts = await fixture.DemoCrm.RegistrationSubmissionsAsync();

        await rig.StartWorkerAsync();
        Assert.Equal(JobStatus.Failed, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(1)));
        var record = Assert.Single(await RecordsAsync(rig, job));
        Assert.Equal((JobRecordStatus.Failed, "WorkflowTampered"), (record.Status, record.ErrorCode));
        Assert.Equal(posts, await fixture.DemoCrm.RegistrationSubmissionsAsync());
        Assert.Equal(0, await rig.QueryAsync(db => db.JobAttempts.CountAsync()));
    }

    [Fact]
    public async Task The_worker_refuses_to_start_without_containment_or_with_too_short_a_lease()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { DisableDefaults = true });
        var settings = new Dictionary<string, string?>(rig.Settings) { ["Journal:LeaseSeconds"] = "60" };
        settings.Remove("DataProtection:KeysDirectory");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddWorkerServices(builder.Configuration);
        using var host = builder.Build();

        var (incarnation, problems) = await host.Services.GetRequiredService<WorkerStartup>().CheckAsync(default);

        Assert.Null(incarnation);
        Assert.Contains(problems, p => p.Contains("containment handshake", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("lease does not cover", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("DataProtection:KeysDirectory", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<AutomataFlow.Application.Workflows.Execution.WorkerIncarnation>());
        Assert.Equal(0, await rig.QueryAsync(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"WorkerIncarnations\"").SingleAsync()));
    }
}
