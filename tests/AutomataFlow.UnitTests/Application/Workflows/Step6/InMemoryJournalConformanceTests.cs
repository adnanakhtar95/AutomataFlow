using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Tests.Journal;

namespace AutomataFlow.UnitTests.Application.Workflows.Step6;

/// <summary>Runs the shared conformance suite against the test-only in-memory journal (Phase 5 runs it against PostgreSQL).</summary>
public sealed class InMemoryJournalConformanceTests : AttemptJournalConformanceTests
{
    protected override IAttemptJournal CreateJournal(TimeProvider clock, TimeSpan lease, TimeSpan margin) => new InMemoryAttemptJournal(clock, lease, margin);

    [Fact]
    public void The_in_memory_journal_is_declared_non_durable()
    {
        var journal = new InMemoryAttemptJournal(Clock);

        Assert.False(journal.Capabilities.IsDurable);
        Assert.True(journal.Capabilities.SupportsFencing);
    }

    /// <summary>J2: a failed checkpoint write stores nothing, so the attempt provably started no side effect.</summary>
    [Fact]
    public async Task J2_a_checkpoint_that_throws_before_storing_leaves_no_side_effect()
    {
        var journal = new CheckpointThrowsJournal(Clock, Lease);
        var a = Incarnation();
        await journal.RegisterIncarnationAsync(a, default);
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);

        await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default));
        Clock.Advance(TimeSpan.FromHours(1));

        var entry = Assert.Single(journal.History(Org, operation));
        Assert.Equal(SideEffectState.NotStarted, entry.SideEffect);
        Assert.True(RepeatSafetyEvaluator.IsFenced(entry));
    }

    /// <summary>J3: the checkpoint is stored but the acknowledgement is lost: the journal shows it, so the attempt is ambiguous.</summary>
    [Fact]
    public async Task J3_a_lost_checkpoint_acknowledgement_still_counts_as_a_started_side_effect()
    {
        var journal = new CheckpointAckLostJournal(Clock, Lease);
        var a = Incarnation();
        await journal.RegisterIncarnationAsync(a, default);
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);

        await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default));
        await journal.CompleteAsync(reservation, new AttemptCompletion(WorkflowOutcome.Failed, new ErrorSummary("CheckpointFailed", ErrorCategory.Transient, true), SideEffectState.NotStarted, ExecutionFenced: true, null), default);

        var entry = Assert.Single(journal.History(Org, operation));
        Assert.Equal(SideEffectState.Started, entry.SideEffect);
        var decision = RepeatSafetyEvaluator.Evaluate(new CurrentWorkflow(false, false, false, null), operation, journal.History(Org, operation), Clock.GetUtcNow(), RepeatSafetyPolicy.Default);
        Assert.NotEqual(RepeatDecisionKind.Allowed, decision.Kind);
        Assert.Equal(RepeatReason.NoSafePath, decision.Reason);
    }

    /// <summary>J5: when completion fails, the entry becomes Abandoned at lease expiry and, with a checkpoint, stays unfenced.</summary>
    [Fact]
    public async Task J5_a_failed_completion_leaves_a_checkpointed_attempt_unfenced()
    {
        var journal = new CompleteThrowsJournal(Clock, Lease);
        var a = Incarnation();
        var b = Incarnation("/workers/b");
        await journal.RegisterIncarnationAsync(a, default);
        await journal.RegisterIncarnationAsync(b, default);
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default);

        await Assert.ThrowsAsync<AttemptJournalException>(() => journal.CompleteAsync(reservation, new AttemptCompletion(WorkflowOutcome.Indeterminate, null, SideEffectState.Started, true, null), default));
        Clock.Advance(Lease + TimeSpan.FromSeconds(1));

        Assert.Equal(ReservationStatus.AwaitingFence, (await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, b.Id, default)).Status);
    }

    /// <summary>F14 audit: the actor, method, reference and attestation are stored with the fence; kill requests are diagnostics.</summary>
    [Fact]
    public async Task F14_human_fence_evidence_is_audited_and_kill_requests_are_only_diagnostics()
    {
        var journal = new InMemoryAttemptJournal(Clock, Lease, Margin);
        var a = Incarnation();
        await journal.RegisterIncarnationAsync(a, default);
        var operation = Guid.NewGuid();
        var reservation = await UnfencedAttemptAsync(journal, a, operation);
        await journal.RecordKillRequestAsync(a.Id, default);

        var result = await journal.ConfirmFenceAsync(Human(a), default);

        var stored = journal.EvidenceFor(Org, operation, reservation.AttemptNumber)!;
        Assert.Equal(("operator-7", ActorRole.PlatformOperator, "INC-4821: VM deleted in the cloud console", true),
            (stored.Human!.ActorUserId, stored.Human.Role, stored.Human.EvidenceReference, stored.Human.AttestedPersonally));
        Assert.Equal(FenceMethod.HostPoweredOffOrDestroyed, stored.Method);
        var kill = Assert.Single(journal.KillRequests);
        Assert.Equal(a.Id, kill.Incarnation);
        Assert.Equal(result.FencedAt, journal.History(Org, operation).Single().FencedAt);
    }
}
