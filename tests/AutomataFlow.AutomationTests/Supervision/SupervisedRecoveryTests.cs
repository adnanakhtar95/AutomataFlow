using System.Text.Json.Nodes;
using AutomataFlow.AutomationTests.DemoCrm;
using AutomataFlow.AutomationTests.Platform;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Supervisor;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.AutomationTests.Supervision;

/// <summary>
/// Phase 5 step 8 with the process-death supervisor (ADR-075): the REAL production Worker, real Chromium and DemoCrm, inside
/// real units in the supervision container. A Worker that dies or freezes is killed, proven gone and fenced AUTOMATICALLY
/// (ProcessDeathConfirmed); a restarted unit (the container's restart policy) then continues from the journal: never a blind
/// repeat, exact DemoCrm customer and POST counts, and no data crossing organizations.
/// </summary>
[Collection(SupervisionGroup.Name)]
public sealed class SupervisedRecoveryTests(SupervisionFixture fixture)
{
    private SupervisionAgent Agent => fixture.Agent;

    private const string Header = "firstName,lastName,email,phone,company,country,customerType";

    private async Task<UnitLaunch> LaunchWorkerAsync(PlatformRig rig)
    {
        var before = await SupervisionQueries.IncarnationCountAsync(rig);
        var launch = await Agent.LaunchAsync(SupervisionFixture.Environment(rig, SupervisionPaths.Worker));
        await SupervisionQueries.WaitForIncarnationAsync(rig, before, TimeSpan.FromSeconds(120), launch);
        return launch;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<string> diagnostics)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached: " + diagnostics());
            await Task.Delay(100);
        }
    }

    private async Task<int[]> WorkerPidsAsync() => [.. (await Agent.UnitProcessesAsync()).Where(p => p.Name == "dotnet").Select(p => p.Pid)];

    [Fact]
    public async Task P1_a_frozen_worker_after_its_side_effect_is_killed_proven_gone_and_fenced_and_a_restarted_unit_reconciles()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        var version = await rig.PublishAsync(WorkflowRig.VariantB); // no idempotency: an ambiguous attempt can only be reconciled
        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var email = $"p1{Guid.NewGuid():N}"[..18] + "+late-commit@example.test";
        var job = await rig.CreateJobAsync(version, $"{Header}\nGrace,Hopper,{email},+1 555 7201,Compilers,Canada,Premium\n", new JobOptions(1, 3, 1));
        var operation = await rig.QueryAsync(db => db.JobRecords.Where(r => r.JobId == job).Select(r => r.OperationId).SingleAsync());

        var first = await LaunchWorkerAsync(rig);
        UnitLaunch? second = null;
        try
        {
            // Freeze the Worker once its side effect is checkpointed and the POST reached DemoCrm (it commits 9 s later).
            await WaitUntilAsync(async () => await rig.QueryAsync(db => db.AttemptJournal.AnyAsync(e => e.OperationId == operation && e.CheckpointAt != null))
                && await fixture.DemoCrm.RegistrationSubmissionsAsync() == posts + 1, TimeSpan.FromSeconds(120), () => first.Tail());
            Assert.Equal(0, (await Agent.SignalAsync(SupervisionAgent.WorkerUid, "STOP", await WorkerPidsAsync())).ExitCode);

            // No operator: the stale heartbeat makes the supervisor kill the unit (SIGKILL works on a stopped process),
            // prove it empty, drain its sockets and confirm the fence.
            await first.AssertExitAsync(SupervisorExitCodes.Fenced);
            Assert.Contains("heartbeat-stale", first.Log, StringComparison.Ordinal);
            Assert.Empty(await Agent.UnitProcessesAsync());
            var killed = await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().SingleAsync(e => e.OperationId == operation && e.AttemptNumber == 1));
            Assert.Equal(("Abandoned", "ProcessDeathConfirmed"), (killed.State, killed.FenceSource));
            Assert.NotNull(killed.CheckpointAt);

            // The restarted unit continues from the journal: a reconcile FINDS the late commit; nothing is submitted again.
            second = await LaunchWorkerAsync(rig);
            Assert.Equal(JobStatus.Completed, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(4)));
            Assert.Equal(JobRecordStatus.Reconciled, await rig.QueryAsync(db => db.JobRecords.Where(r => r.JobId == job).Select(r => r.Status).SingleAsync()));
            Assert.Equal((customers + 1, posts + 1), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));
            Assert.Contains(await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().Where(e => e.OperationId == operation).ToListAsync()),
                e => e.Intent == "Reconcile" && e.Outcome == "ReconciledFound");
        }
        finally
        {
            await first.CleanUpAsync();
            if (second is not null)
            {
                await second.CleanUpAsync();
            }
        }
    }

    [Fact]
    public async Task P2_a_worker_killed_alone_is_fenced_automatically_and_a_restarted_unit_finishes_both_tenants_jobs_without_crossing_them()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        // Organization A: a 20 s pause before the submit, so the kill provably precedes any side effect.
        var paused = JsonNode.Parse(WorkflowRig.SampleJson)!.AsObject();
        var steps = paused["steps"]!.AsArray();
        steps.Insert(steps.IndexOf(steps.Single(s => (string?)s!["id"] == "submit")), JsonNode.Parse("""{"id":"pause","type":"delay","config":{"milliseconds":20000}}"""));
        var versionA = await rig.PublishAsync(paused.ToJsonString());
        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var jobA = await rig.CreateJobAsync(versionA, $"{Header}\nAda,Lovelace,p2a{Guid.NewGuid():N}@example.test,+1 555 7301,Engines,Canada,Premium\n", new JobOptions(1, 3, 1));
        var operationA = await rig.QueryAsync(db => db.JobRecords.Where(r => r.JobId == jobA).Select(r => r.OperationId).SingleAsync());

        // Organization B: its own Owner, workflow, secret and job, processed by the SAME Workers.
        var (orgB, userB) = await rig.AddOrganizationAsync();
        Guid jobB;
        using (rig.ActAs(orgB, userB))
        {
            var versionB = await rig.PublishAsync(WorkflowRig.SampleJson);
            jobB = await rig.CreateJobAsync(versionB, $"{Header}\nKatherine,Johnson,p2b{Guid.NewGuid():N}@example.test,+1 555 7302,Orbits,Canada,Premium\n", new JobOptions(1, 3, 1));
        }

        var first = await LaunchWorkerAsync(rig);
        UnitLaunch? second = null;
        try
        {
            await WaitUntilAsync(() => rig.QueryAsync(db => db.AttemptJournal.AnyAsync(e => e.OperationId == operationA && e.State == "Started" && e.CheckpointAt == null)),
                TimeSpan.FromSeconds(120), () => first.Tail());

            // SIGKILL of the Worker process ALONE: its Chromium processes and driver are orphans of the unit; the unit's init
            // exits with the Worker and the kernel kills and reaps the rest; the supervisor proves it before the fence.
            Assert.Equal(0, (await Agent.SignalAsync(SupervisionAgent.WorkerUid, "KILL", await WorkerPidsAsync())).ExitCode);
            await first.AssertExitAsync(SupervisorExitCodes.Fenced);
            Assert.Contains("unit-exited", first.Log, StringComparison.Ordinal);
            Assert.Empty(await Agent.UnitProcessesAsync());
            var killed = await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().SingleAsync(e => e.OperationId == operationA && e.AttemptNumber == 1));
            Assert.Equal("Abandoned", killed.State);
            Assert.Null(killed.CheckpointAt); // provably no side effect: it is repeated automatically

            second = await LaunchWorkerAsync(rig);
            Assert.Equal(JobStatus.Completed, await rig.WaitForJobAsync(jobA, TimeSpan.FromMinutes(4)));
            Assert.Equal(JobStatus.Completed, await WaitForJobAsync(rig, orgB, jobB));
            Assert.Equal((customers + 2, posts + 2), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync())); // one POST each

            // Nothing crosses organizations: each tenant sees only its own attempts and logs, also through the use cases.
            var attemptsB = await rig.QueryAsync(orgB, db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == jobB).ToListAsync());
            Assert.NotEmpty(attemptsB);
            Assert.All(attemptsB, a => Assert.Equal(orgB, a.OrganizationId));
            Assert.Empty(await rig.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == jobB).ToListAsync()));
            Assert.Empty(await rig.QueryAsync(orgB, db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == jobA).ToListAsync()));
            var recordB = await rig.QueryAsync(orgB, db => db.JobRecords.Where(r => r.JobId == jobB).Select(r => r.Id).SingleAsync());
            Assert.Equal(ServiceStatus.NotFound, (await rig.AsOwnerAsync<ExecutionQueryService, ServiceResult<IReadOnlyList<AttemptView>>>(s => s.AttemptsAsync(jobB, recordB, default))).Status);
            Assert.Equal(ServiceStatus.NotFound, (await rig.AsOwnerAsync<ExecutionQueryService, ServiceResult<IReadOnlyList<LogView>>>(s => s.LogsAsync(attemptsB[0].Id, default))).Status);
        }
        finally
        {
            await first.CleanUpAsync();
            if (second is not null)
            {
                await second.CleanUpAsync();
            }
        }
    }

    [Fact]
    public async Task P6_without_the_supervisor_there_is_no_fence_and_the_ambiguous_record_is_never_retried()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture.DemoCrm, fixture.SharedRoot);
        var version = await rig.PublishAsync(WorkflowRig.VariantB);
        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var email = $"p6{Guid.NewGuid():N}"[..18] + "+late-commit@example.test";
        var job = await rig.CreateJobAsync(version, $"{Header}\nMary,Jackson,{email},+1 555 7401,Wind Tunnels,Canada,Premium\n", new JobOptions(1, 3, 1));
        var operation = await rig.QueryAsync(db => db.JobRecords.Where(r => r.JobId == job).Select(r => r.OperationId).SingleAsync());

        var first = await LaunchWorkerAsync(rig);
        UnitLaunch? second = null;
        try
        {
            await WaitUntilAsync(async () => await rig.QueryAsync(db => db.AttemptJournal.AnyAsync(e => e.OperationId == operation && e.CheckpointAt != null))
                && await fixture.DemoCrm.RegistrationSubmissionsAsync() == posts + 1, TimeSpan.FromSeconds(120), () => first.Tail());

            // The supervisor dies first (in production its container then stops), then the unit.
            Assert.Equal(0, (await Agent.SignalAsync(SupervisionAgent.SupervisorUid, "KILL", first.SupervisorPid)).ExitCode);
            await Agent.SignalAsync(SupervisionAgent.WorkerUid, "KILL", [.. (await Agent.UnitProcessesAsync()).Select(p => p.Pid)]);
            await WaitUntilAsync(async () => (await Agent.UnitProcessesAsync()).Count == 0, TimeSpan.FromSeconds(30), () => "unit still alive");
            Assert.NotNull(await first.WaitForExitAsync(TimeSpan.FromSeconds(30))); // (its output pipe closes with the unit)

            // A new unit's Worker sees an unfenced attempt with a checkpoint: AwaitingFence, then NeedsReview. Never retried.
            second = await LaunchWorkerAsync(rig);
            await WaitUntilAsync(() => rig.QueryAsync(db => db.JobRecords.AnyAsync(r => r.JobId == job && r.Status == JobRecordStatus.NeedsReview)),
                TimeSpan.FromMinutes(6), () => second.Tail());
            Assert.Equal("FenceNotConfirmed", await rig.QueryAsync(db => db.JobRecords.Where(r => r.JobId == job).Select(r => r.ErrorCode).SingleAsync()));
            var dead = await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().SingleAsync(e => e.OperationId == operation && e.AttemptNumber == 1));
            Assert.Null(dead.FenceSource);
            Assert.Null(await SupervisionQueries.FenceConfirmationAsync(rig, dead.IncarnationId));
            // One POST (its late commit made one customer); nothing was repeated.
            Assert.Equal((customers + 1, posts + 1), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));
        }
        finally
        {
            await first.CleanUpAsync();
            if (second is not null)
            {
                await second.CleanUpAsync();
            }
        }
    }

    private static async Task<JobStatus> WaitForJobAsync(PlatformRig rig, Guid organization, Guid job)
    {
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (true)
        {
            var status = await rig.QueryAsync(organization, db => db.Jobs.Where(j => j.Id == job).Select(j => j.Status).SingleAsync());
            if (status is not (JobStatus.Pending or JobStatus.Running) || DateTime.UtcNow > deadline)
            {
                return status;
            }

            await Task.Delay(500);
        }
    }
}
