using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutomataFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Regression (Phase 5 step 8b): two records of one job finishing at the same moment must finalize the job. Before the
/// job-row lock, each transaction saw the other record still Running (read committed) and the job stayed Running.
/// </summary>
public sealed class JobFinalizationTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private ServiceProvider Services() => new ServiceCollection()
        .AddLogging()
        .AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.ConnectionString,
            ["DataProtection:KeysDirectory"] = Path.Combine(Path.GetTempPath(), "af-keys-" + Guid.NewGuid().ToString("N")),
        }).Build())
        .BuildServiceProvider();

    private async Task<(Guid Org, Guid Job, Guid[] Records)> SeedRunningJobAsync(Guid worker, int records)
    {
        var org = Organization.Create("Race", "race-" + Guid.NewGuid().ToString("N")[..12], Now);
        await using var context = db.Context(org.Id);
        context.Organizations.Add(org);
        var workflow = Workflow.Create(org.Id, "race", null, Now);
        var version = WorkflowVersion.CreateDraft(org.Id, workflow.Id, workflow.NextVersionNumber(), 1, """{"schemaVersion":1}""", "sha256:" + new string('c', 64), Now);
        version.Publish(Now);
        var job = Job.Create(org.Id, workflow.Id, version.Id, JobOptions.Default, "{}", Guid.NewGuid(), null, null, Now);
        var rows = Enumerable.Range(1, records).Select(i => JobRecord.CreatePending(org.Id, job.Id, i, "{}", false, Now)).ToList();
        context.AddRange(workflow, version, job);
        context.AddRange(rows);
        await context.SaveChangesAsync();
        await context.JobRecords.Where(r => r.JobId == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.Running).SetProperty(r => r.LockedBy, worker).SetProperty(r => r.LeaseExpiresAt, Now.AddMinutes(5)));
        await context.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running));
        return (org.Id, job.Id, [.. rows.Select(r => r.Id)]);
    }

    [Fact]
    public async Task Records_completing_concurrently_always_finalize_their_job()
    {
        await using var services = Services();
        var worker = Guid.NewGuid();
        var done = new RecordTransition(JobRecordStatus.Succeeded, RecordIntent.Normal, null, null, null, StoreResults: false);
        for (var i = 0; i < 40; i++)
        {
            var (org, job, records) = await SeedRunningJobAsync(worker, 2);
            using var go = new Barrier(records.Length);
            var stored = await Task.WhenAll(records.Select(record => Task.Run(async () =>
            {
                await using var scope = services.CreateAsyncScope();
                var recorder = scope.ServiceProvider.GetRequiredService<AttemptRecorder>();
                go.SignalAndWait();
                return await recorder.CompleteAsync(new ClaimedRecord(record, org, job, Guid.NewGuid(), RecordIntent.Normal, null), worker, null, done, [], Now, default);
            })));

            Assert.All(stored, Assert.True);
            await using var check = db.Context(org);
            Assert.Equal(JobStatus.Completed, (await check.Jobs.SingleAsync(j => j.Id == job)).Status);
        }
    }

    [Fact]
    public async Task The_idle_job_sweep_finalizes_a_job_whose_finalization_was_missed()
    {
        var (org, job, _) = await SeedRunningJobAsync(Guid.NewGuid(), 2);
        await using (var context = db.Context(org))
        {
            var records = await context.JobRecords.Where(r => r.JobId == job).OrderBy(r => r.RowNumber).ToListAsync();
            await context.JobRecords.Where(r => r.Id == records[0].Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.Succeeded));
            await context.JobRecords.Where(r => r.Id == records[1].Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.NeedsReview));
        }

        await using var source = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await new RecordDispatcher(source).FinalizeIdleJobsAsync(default) >= 1);
        await using var check = db.Context(org);
        Assert.Equal(JobStatus.CompletedWithErrors, (await check.Jobs.SingleAsync(j => j.Id == job)).Status);
    }

    [Fact]
    public async Task A_requeue_committed_after_the_sweep_chose_its_candidates_is_never_stranded_in_a_finished_job()
    {
        var (org, job, _) = await SeedRunningJobAsync(Guid.NewGuid(), 2);
        Guid reviewed;
        await using (var context = db.Context(org))
        {
            var records = await context.JobRecords.Where(r => r.JobId == job).OrderBy(r => r.RowNumber).ToListAsync();
            reviewed = records[1].Id;
            await context.JobRecords.Where(r => r.Id == records[0].Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.Succeeded));
            await context.JobRecords.Where(r => r.Id == reviewed).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.NeedsReview));
        }

        await using var source = NpgsqlDataSource.Create(db.ConnectionString);
        var dispatcher = new RecordDispatcher(source)
        {
            // The job is idle when the sweep picks it; an operator requeue (same writes and lock as RequeueAsync) commits
            // before the sweep takes the job lock.
            AfterSweepCandidates = async () =>
            {
                await using var requeue = db.Context(org);
                await using var tx = await requeue.Database.BeginTransactionAsync();
                await JobFinalizer.LockAsync(requeue, org, job, default);
                await requeue.JobRecords.Where(r => r.Id == reviewed).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobRecordStatus.Pending));
                await requeue.Jobs.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running));
                await tx.CommitAsync();
            },
        };

        await dispatcher.FinalizeIdleJobsAsync(default);

        await using var check = db.Context(org);
        Assert.Equal(JobStatus.Running, (await check.Jobs.SingleAsync(j => j.Id == job)).Status);
        Assert.Equal(JobRecordStatus.Pending, (await check.JobRecords.SingleAsync(r => r.Id == reviewed)).Status);
    }
}
