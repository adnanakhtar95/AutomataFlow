using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Domain.Workflows;

namespace AutomataFlow.UnitTests.Domain;

/// <summary>Phase 5 step 1: invariants of the platform entities.</summary>
public class PlatformEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.NewGuid();

    [Theory]
    [InlineData(0, 3, 30)]
    [InlineData(17, 3, 30)]
    [InlineData(2, 0, 30)]
    [InlineData(2, 11, 30)]
    [InlineData(2, 3, -1)]
    [InlineData(2, 3, 3_601)]
    public void Job_options_outside_their_bounds_are_rejected(int concurrency, int attempts, int backoff)
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobOptions(concurrency, attempts, backoff).Validate());
    }

    [Fact]
    public void A_job_finishes_only_in_a_terminal_status_and_keeps_its_counts_consistent()
    {
        var job = Job.Create(Org, Guid.NewGuid(), Guid.NewGuid(), JobOptions.Default, "{}", Guid.NewGuid(), "k", "f.csv", Now);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Finish(JobStatus.Running, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.SetRecordCounts(1, 2));
        job.SetRecordCounts(3, 1);
        job.Finish(JobStatus.CompletedWithErrors, Now);
        Assert.Equal((JobStatus.CompletedWithErrors, Now, Now), (job.Status, job.FinishedAt!.Value, job.StartedAt!.Value));
        Assert.Throws<ArgumentException>(() => Job.Create(Org, Guid.NewGuid(), Guid.NewGuid(), JobOptions.Default, "{}", Guid.NewGuid(), new string('k', 101), null, Now));
    }

    [Fact]
    public void Records_get_a_fresh_operation_id_and_invalid_rows_are_terminal_and_never_scheduled()
    {
        var job = Guid.NewGuid();
        var a = JobRecord.CreatePending(Org, job, 1, "{}", false, Now);
        var b = JobRecord.CreatePending(Org, job, 2, "{}", false, Now);
        Assert.NotEqual(a.OperationId, b.OperationId);
        Assert.Equal((JobRecordStatus.Pending, RecordIntent.Normal, Now), (a.Status, a.NextIntent, a.NextAttemptAt!.Value));

        var invalid = JobRecord.CreateInvalid(Org, job, 3, """["email:InvalidEmail"]""", Now);
        Assert.Equal(JobRecordStatus.Invalid, invalid.Status);
        Assert.Null(invalid.NextAttemptAt);
        Assert.True(JobRecord.IsTerminal(invalid.Status));
        Assert.False(JobRecord.IsTerminal(JobRecordStatus.AwaitingFence));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobRecord.CreatePending(Org, job, 0, "{}", false, Now));
    }

    [Fact]
    public void An_artifact_key_must_start_with_its_organization()
    {
        Assert.Throws<ArgumentException>(() => ExecutionArtifact.Create(Guid.NewGuid(), Org, Guid.NewGuid(), "Screenshot", Guid.NewGuid().ToString("N") + "/x.png", "x.png", "image/png", 1, "ab", Now));
        var ok = ExecutionArtifact.Create(Guid.NewGuid(), Org, Guid.NewGuid(), "Screenshot", Org.ToString("N") + "/x.png", "x.png", "image/png", 1, "ab", Now);
        Assert.Equal(Org, ok.OrganizationId);
    }

    [Theory]
    [InlineData("crmPassword", true)]
    [InlineData("1abc", false)]
    [InlineData("has-dash", false)]
    [InlineData("", false)]
    public void Secret_names_follow_the_declared_name_grammar(string name, bool valid)
    {
        Assert.Equal(valid, OrganizationSecret.IsValidName(name));
    }

    [Fact]
    public void Attempts_complete_once_and_never_before_they_started()
    {
        var attempt = JobAttempt.Start(Org, Guid.NewGuid(), Guid.NewGuid(), 1, RecordIntent.Normal, Guid.NewGuid(), Now);
        attempt.Complete(JobAttemptStatus.Indeterminate, "Timeout", "Transient", true, false, null, Now.AddSeconds(-5));
        Assert.Equal(Now, attempt.FinishedAt);
        Assert.Throws<InvalidOperationException>(() => attempt.Complete(JobAttemptStatus.Succeeded, null, null, false, true, null, Now));
    }

    [Fact]
    public void Workflow_version_numbers_increase()
    {
        var workflow = Workflow.Create(Org, " register ", "  ", Now);
        Assert.Equal(("register", null), (workflow.Name, workflow.Description));
        Assert.Equal(1, workflow.NextVersionNumber());
        Assert.Equal(2, workflow.NextVersionNumber());
    }
}
