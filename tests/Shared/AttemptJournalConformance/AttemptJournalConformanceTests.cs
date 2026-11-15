using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using Xunit;

namespace AutomataFlow.Tests.Journal;

/// <summary>
/// The shared journal conformance suite (docs/PHASE4_PLAN.md §7.2, §7.4, §7.5; F7–F12, F14 and the J contract rules).
/// Phase 4 runs it against the test-only in-memory journal; Phase 5 runs the SAME suite against PostgreSQL.
/// </summary>
public abstract class AttemptJournalConformanceTests
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    protected static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);
    protected static readonly AttemptSnapshot Snapshot = new("sha256:" + new string('a', 64), 1, 120, false, true, null);

    protected ManualClock Clock { get; } = new(T0);
    protected Guid Org { get; } = Guid.NewGuid();

    protected abstract IAttemptJournal CreateJournal(TimeProvider clock, TimeSpan lease, TimeSpan margin);

    protected static WorkerIncarnation Incarnation(string unit = "/workers/a") =>
        new(new WorkerIncarnationId(Guid.NewGuid()), "host-1", "boot-1", 4242, T0, new ContainmentUnit(ContainmentKind.LinuxCgroup, unit), "/var/lib/af/ledger");

    protected async Task<(IAttemptJournal Journal, WorkerIncarnation A, WorkerIncarnation B)> SetUpAsync()
    {
        var journal = CreateJournal(Clock, Lease, Margin);
        var a = Incarnation("/workers/a");
        var b = Incarnation("/workers/b");
        await journal.RegisterIncarnationAsync(a, default);
        await journal.RegisterIncarnationAsync(b, default);
        return (journal, a, b);
    }

    protected async Task<AttemptReservation> StartAsync(IAttemptJournal journal, WorkerIncarnation who, Guid operation, ExecutionIntent intent = ExecutionIntent.Normal)
    {
        var result = await journal.ReserveAsync(Org, operation, intent, who.Id, default);
        Assert.Equal(ReservationStatus.Reserved, result.Status);
        await journal.StartAsync(result.Reservation!, Snapshot, default);
        return result.Reservation!;
    }

    /// <summary>An attempt that wrote its checkpoint and then lost its lease (a frozen Worker): abandoned and UNFENCED.</summary>
    protected async Task<AttemptReservation> UnfencedAttemptAsync(IAttemptJournal journal, WorkerIncarnation who, Guid operation)
    {
        var reservation = await StartAsync(journal, who, operation);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default);
        Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        return reservation;
    }

    protected async Task<AttemptReservationResult> ProbeAsync(IAttemptJournal journal, WorkerIncarnation who, Guid operation, ExecutionIntent intent = ExecutionIntent.Normal)
    {
        var result = await journal.ReserveAsync(Org, operation, intent, who.Id, default);
        if (result.Reservation is not null)
        {
            await journal.ReleaseAsync(result.Reservation, default);
        }

        return result;
    }

    // ----- Exclusivity, reservation, release -----

    [Fact]
    public async Task Only_one_attempt_per_operation_is_in_flight_for_either_intent()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await StartAsync(journal, a, operation);

        Assert.Equal(ReservationStatus.InFlight, (await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, b.Id, default)).Status);
        var reconcile = await journal.ReserveAsync(Org, operation, ExecutionIntent.Reconcile, b.Id, default);
        Assert.Equal(ReservationStatus.InFlight, reconcile.Status);
        Assert.Contains(reconcile.History, h => h.State == AttemptState.Started);
    }

    [Fact]
    public async Task Unregistered_incarnations_cannot_reserve()
    {
        var (journal, _, _) = await SetUpAsync();

        var result = await journal.ReserveAsync(Org, Guid.NewGuid(), ExecutionIntent.Normal, new WorkerIncarnationId(Guid.NewGuid()), default);

        Assert.Equal(ReservationStatus.UnknownIncarnation, result.Status);
        Assert.Null(result.Reservation);
    }

    [Fact]
    public async Task A_released_reservation_never_becomes_an_attempt()
    {
        var (journal, a, _) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reserved = await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, a.Id, default);

        await journal.ReleaseAsync(reserved.Reservation!, default);

        var next = await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, a.Id, default);
        Assert.Empty(next.History);
        Assert.Equal(ReservationStatus.Reserved, next.Status);
    }

    [Fact]
    public async Task The_history_is_organization_scoped()
    {
        var (journal, a, _) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await StartAsync(journal, a, operation);

        var otherOrg = await journal.ReserveAsync(Guid.NewGuid(), operation, ExecutionIntent.Normal, a.Id, default);

        Assert.Equal(ReservationStatus.Reserved, otherOrg.Status);
        Assert.Empty(otherOrg.History);
    }

    [Fact]
    public async Task Reservation_returns_the_journal_clock()
    {
        var (journal, a, _) = await SetUpAsync();
        Clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(T0 + TimeSpan.FromMinutes(3), (await journal.ReserveAsync(Org, Guid.NewGuid(), ExecutionIntent.Normal, a.Id, default)).Now);
    }

    // ----- Lease expiry and fencing of writes -----

    [Fact]
    public async Task An_expired_started_attempt_without_checkpoint_is_abandoned_and_fenced_by_NoCheckpoint()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await StartAsync(journal, a, operation);
        Clock.Advance(Lease + TimeSpan.FromSeconds(1));

        var result = await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, b.Id, default);

        Assert.Equal(ReservationStatus.Reserved, result.Status);
        var abandoned = Assert.Single(result.History);
        Assert.Equal(AttemptState.Abandoned, abandoned.State);
        Assert.Equal(Clock.GetUtcNow(), abandoned.AbandonedAt);
        Assert.Equal(SideEffectState.NotStarted, abandoned.SideEffect);
        Assert.True(RepeatSafetyEvaluator.IsFenced(abandoned));
    }

    /// <summary>F7: an abandoned attempt with a checkpoint is unfenced and blocks both intents, atomically with the abandonment.</summary>
    [Fact]
    public async Task F7_an_unfenced_attempt_blocks_every_reservation()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);

        var normal = await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, b.Id, default);
        var reconcile = await journal.ReserveAsync(Org, operation, ExecutionIntent.Reconcile, b.Id, default);

        Assert.Equal(ReservationStatus.AwaitingFence, normal.Status);
        Assert.Equal(ReservationStatus.AwaitingFence, reconcile.Status);
        var entry = Assert.Single(normal.History);
        Assert.Equal(AttemptState.Abandoned, entry.State);
        Assert.Equal(SideEffectState.Started, entry.SideEffect);
        Assert.False(RepeatSafetyEvaluator.IsFenced(entry));
    }

    [Fact]
    public async Task Every_write_after_lease_loss_is_fenced()
    {
        var (journal, a, _) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        Clock.Advance(Lease + TimeSpan.FromSeconds(1));

        var checkpoint = await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(10), default));
        Assert.Equal(JournalErrorCodes.Fenced, checkpoint.Code);
        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.StartAsync(reservation, Snapshot, default))).Code);
    }

    [Fact]
    public async Task A_stale_fencing_token_is_refused()
    {
        var (journal, a, _) = await SetUpAsync();
        var reservation = await StartAsync(journal, a, Guid.NewGuid());
        var forged = reservation with { FencingToken = reservation.FencingToken + 1000 };

        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(forged, "s", TimeSpan.FromSeconds(5), default))).Code);
    }

    /// <summary>F5: the checkpoint needs the remaining lease to cover the whole step timeout plus the safety margin.</summary>
    [Fact]
    public async Task F5_a_checkpoint_with_too_short_a_lease_is_refused()
    {
        var (journal, a, _) = await SetUpAsync();
        var reservation = await StartAsync(journal, a, Guid.NewGuid());
        Clock.Advance(Lease - TimeSpan.FromSeconds(30)); // 30 s remain

        Assert.Equal(JournalErrorCodes.LeaseTooShort,
            (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(21), default))).Code);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(20), default); // 20 + 10 = 30: exactly enough
    }

    /// <summary>F8: concurrent abandonment and checkpoint on the same entry — exactly one wins, never both.</summary>
    [Fact]
    public async Task F8_checkpoint_and_abandonment_are_serialized()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var clock = new ManualClock(T0);
            var journal = CreateJournal(clock, Lease, Margin);
            var a = Incarnation();
            var b = Incarnation();
            await journal.RegisterIncarnationAsync(a, default);
            await journal.RegisterIncarnationAsync(b, default);
            var operation = Guid.NewGuid();
            var reserved = await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, a.Id, default);
            await journal.StartAsync(reserved.Reservation!, Snapshot, default);
            clock.Advance(Lease - TimeSpan.FromSeconds(20)); // 20 s remain; a 5 s step + 10 s margin fits

            using var start = new Barrier(2);
            var checkpoint = Task.Run(async () =>
            {
                start.SignalAndWait();
                try
                {
                    await journal.SideEffectStartingAsync(reserved.Reservation!, "submit", TimeSpan.FromSeconds(5), default);
                    return true;
                }
                catch (AttemptJournalException)
                {
                    return false;
                }
            });
            var takeover = Task.Run(async () =>
            {
                start.SignalAndWait();
                clock.Advance(TimeSpan.FromSeconds(21)); // the lease expires
                return await journal.ReserveAsync(Org, operation, ExecutionIntent.Normal, b.Id, default);
            });

            var checkpointAccepted = await checkpoint;
            var other = await takeover;

            Assert.False(checkpointAccepted && other.Status == ReservationStatus.Reserved, $"iteration {i}: checkpoint accepted AND another attempt admitted");
            if (checkpointAccepted)
            {
                Assert.Equal(ReservationStatus.AwaitingFence, other.Status);
            }
        }
    }

    /// <summary>Lease renewal (Phase 5 heartbeat): extends a valid lease from journal now; never revives a lost one.</summary>
    [Fact]
    public async Task Lease_renewal_extends_a_valid_lease_and_never_revives_a_lost_one()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        Clock.Advance(Lease - TimeSpan.FromSeconds(5));

        Assert.Equal(Clock.GetUtcNow() + Lease, await journal.RenewLeaseAsync(reservation, default));
        Clock.Advance(TimeSpan.FromSeconds(30)); // past the ORIGINAL expiry, inside the renewed one (30 s left >= 10 s step + 10 s margin)
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(10), default);
        Assert.Equal(ReservationStatus.InFlight, (await ProbeAsync(journal, b, operation)).Status);

        var other = await StartAsync(journal, a, Guid.NewGuid());
        Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.RenewLeaseAsync(other, default))).Code);
        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.RenewLeaseAsync(other with { FencingToken = other.FencingToken + 99 }, default))).Code);
    }

    [Fact]
    public async Task Capabilities_report_the_lease_and_margin_in_use()
    {
        var (journal, _, _) = await SetUpAsync();
        Assert.Equal((Lease, Margin), (journal.Capabilities.LeaseLength, journal.Capabilities.LeaseSafetyMargin));
        Assert.True(journal.Capabilities.SupportsFencing);
    }

    // ----- Completion -----

    [Fact]
    public async Task Completion_in_time_records_the_outcome_and_the_fence_at_the_journal_clock()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default);
        Clock.Advance(TimeSpan.FromSeconds(3));

        await journal.CompleteAsync(reservation, new AttemptCompletion(WorkflowOutcome.Indeterminate, new ErrorSummary("Timeout", ErrorCategory.Transient, true), SideEffectState.Started, ExecutionFenced: true, null), default);

        var entry = Assert.Single((await ProbeAsync(journal, b, operation)).History);
        Assert.Equal(AttemptState.Completed, entry.State);
        Assert.Equal(T0 + TimeSpan.FromSeconds(3), entry.FinishedAt);
        Assert.Equal(T0, entry.SideEffectCheckpointAt);
        Assert.Equal(T0 + TimeSpan.FromSeconds(3), RepeatSafetyEvaluator.FenceTime(entry));
    }

    [Fact]
    public async Task A_completion_whose_barrier_did_not_complete_leaves_the_attempt_unfenced()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default);

        await journal.CompleteAsync(reservation, new AttemptCompletion(WorkflowOutcome.Indeterminate, null, SideEffectState.Started, ExecutionFenced: false, null), default);

        Assert.Equal(ReservationStatus.AwaitingFence, (await journal.ReserveAsync(Org, operation, ExecutionIntent.Reconcile, b.Id, default)).Status);
    }

    /// <summary>A late completion after lease loss adds the outcome and the fence; it never admits anything by itself.</summary>
    [Fact]
    public async Task A_late_completion_fences_an_abandoned_attempt_at_its_own_time()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await UnfencedAttemptAsync(journal, a, operation);
        Assert.Equal(ReservationStatus.AwaitingFence, (await ProbeAsync(journal, b, operation)).Status);
        Clock.Advance(TimeSpan.FromSeconds(30));
        var lateAt = Clock.GetUtcNow();

        await journal.CompleteAsync(reservation, new AttemptCompletion(WorkflowOutcome.Indeterminate, null, SideEffectState.Started, ExecutionFenced: true, null), default);

        var entry = Assert.Single((await ProbeAsync(journal, b, operation)).History);
        Assert.Equal(AttemptState.Abandoned, entry.State);
        Assert.Equal(WorkflowOutcome.Indeterminate, entry.Outcome);
        Assert.Equal(lateAt, RepeatSafetyEvaluator.FenceTime(entry));
    }

    // ----- Observation -----

    [Fact]
    public async Task The_observation_time_is_the_journal_clock_and_only_for_reconcile_attempts()
    {
        var (journal, a, _) = await SetUpAsync();
        var reconcile = await StartAsync(journal, a, Guid.NewGuid(), ExecutionIntent.Reconcile);
        Clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Equal(T0 + TimeSpan.FromSeconds(7), await journal.MarkObservationAsync(reconcile, default));

        var normal = await StartAsync(journal, a, Guid.NewGuid());
        Assert.Equal(JournalErrorCodes.InvalidState, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.MarkObservationAsync(normal, default))).Code);
    }

    // ----- External fence confirmation (§7.5) -----

    protected static FenceEvidence ProcessDeath(WorkerIncarnation who, DrainReport? drain = null, DateTimeOffset? observedAt = null) =>
        new(FenceSource.ProcessDeathConfirmed, who.Id, who.Unit, FenceMethod.SupervisorUnitEmptyAndDrained, observedAt ?? T0, drain ?? new DrainReport(true, 2, 2, false), null, null);

    /// <summary>F9: evidence must match the recorded incarnation, use an allowed method and carry the required proof.</summary>
    [Fact]
    public async Task F9_mismatched_or_incomplete_evidence_is_rejected_and_the_attempt_stays_unfenced()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);

        FenceEvidence[] invalid =
        [
            ProcessDeath(b) with { Unit = a.Unit },                                     // another incarnation's id with A's unit
            ProcessDeath(a) with { Unit = b.Unit },                                     // A's id with another incarnation's unit
            ProcessDeath(a) with { Incarnation = new WorkerIncarnationId(Guid.NewGuid()) }, // unknown incarnation
            ProcessDeath(a) with { Unit = new ContainmentUnit(ContainmentKind.LinuxCgroup, "/workers/other") }, // another unit
            ProcessDeath(a) with { Method = FenceMethod.HostPoweredOffOrDestroyed },    // method not allowed for this source
            ProcessDeath(a, new DrainReport(true, 2, 1, false)),                        // drain incomplete
            ProcessDeath(a, new DrainReport(false, 2, 2, false)),                       // enumeration incomplete
            ProcessDeath(a, new DrainReport(true, 2, 2, true)),                         // ownership ambiguous
            ProcessDeath(a) with { Drain = null },                                      // unit empty but no drain report
            ProcessDeath(a) with { Method = FenceMethod.SupervisorHostRebooted, CurrentBootId = a.BootId }, // boot id unchanged
            ProcessDeath(a) with { Source = FenceSource.IsolationTerminated },          // Phase 7 source: not accepted
            ProcessDeath(a) with { Source = FenceSource.EgressLeaseExpired },
            ProcessDeath(a) with { Source = FenceSource.ExecutorEgressRevoked },        // only via completion
            Human(a) with { Human = new HumanConfirmation("op-1", ActorRole.PlatformOperator, "short", true) },
            Human(a) with { Human = new HumanConfirmation("op-1", ActorRole.PlatformOperator, "TICKET-1234 console screenshot", false) },
            Human(a) with { Human = null },
        ];

        foreach (var evidence in invalid)
        {
            var result = await journal.ConfirmFenceAsync(evidence, default);
            Assert.False(result.Accepted, evidence.ToString());
            Assert.NotNull(result.RejectionReason);
        }

        // A VALID confirmation for another incarnation (B) is accepted but fences only B's attempts, never A's.
        var forB = await journal.ConfirmFenceAsync(ProcessDeath(b), default);
        Assert.True(forB.Accepted);
        Assert.Empty(forB.FencedAttempts);

        var c = Incarnation("/workers/c");
        await journal.RegisterIncarnationAsync(c, default);
        var probe = await ProbeAsync(journal, c, operation);
        Assert.Equal(ReservationStatus.AwaitingFence, probe.Status);
        Assert.Null(Assert.Single(probe.History).FencedAt);
    }

    /// <summary>F10: a kill request, an expired lease and silence are not a fence.</summary>
    [Fact]
    public async Task F10_a_kill_request_is_not_a_fence()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);
        var supervisor = new FakeSupervisor(journal, Clock);

        var result = await supervisor.TryConfirmProcessDeathAsync(a, new FakeContainmentUnit { EmptiesWhenKilled = false });
        Clock.Advance(TimeSpan.FromHours(1));

        Assert.Null(result);
        var probe = await ProbeAsync(journal, b, operation);
        Assert.Equal(ReservationStatus.AwaitingFence, probe.Status);
        Assert.Null(Assert.Single(probe.History).FencedAt);
        Assert.Equal(RepeatDecisionKind.AwaitingFence, RepeatSafetyEvaluator.Evaluate(new CurrentWorkflow(true, true, true, null), operation, probe.History, Clock.GetUtcNow(), RepeatSafetyPolicy.Default).Kind);
    }

    [Theory]
    [InlineData(false, 2, 2)]
    [InlineData(true, 2, 1)]
    public async Task The_supervisor_reports_nothing_without_complete_proof(bool ledgerReadable, int ledgered, int drainable)
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);

        var result = await new FakeSupervisor(journal, Clock).TryConfirmProcessDeathAsync(a, new FakeContainmentUnit { LedgerReadable = ledgerReadable, LedgeredConnections = ledgered, DrainableConnections = drainable });

        Assert.Null(result);
        Assert.Equal(ReservationStatus.AwaitingFence, (await ProbeAsync(journal, b, operation)).Status);
    }

    /// <summary>F11: a confirmation fences every unfenced attempt of the incarnation, with one commit time; others are untouched.</summary>
    [Fact]
    public async Task F11_a_fence_covers_the_whole_incarnation_only()
    {
        var (journal, a, b) = await SetUpAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var others = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, first);
        await StartAsync(journal, a, second).ContinueWith(async t => await journal.SideEffectStartingAsync(await t, "submit", TimeSpan.FromSeconds(15), default)).Unwrap();
        var c = Incarnation("/workers/c");
        await journal.RegisterIncarnationAsync(c, default);
        await UnfencedAttemptAsync(journal, c, others);
        Clock.Advance(TimeSpan.FromSeconds(5));

        var result = await new FakeSupervisor(journal, Clock).TryConfirmProcessDeathAsync(a, new FakeContainmentUnit());

        Assert.True(result!.Accepted);
        Assert.Equal(2, result.FencedAttempts.Length);
        var fencedAt = result.FencedAt!.Value;
        Assert.Equal(fencedAt, Assert.Single((await ProbeAsync(journal, b, first)).History).FencedAt);
        Assert.Equal(fencedAt, Assert.Single((await ProbeAsync(journal, b, second)).History).FencedAt);
        Assert.Equal(ReservationStatus.AwaitingFence, (await ProbeAsync(journal, b, others)).Status);
    }

    /// <summary>F12: FencedAt is the journal commit time, never the supervisor's observation, the kill request or the lease expiry.</summary>
    [Fact]
    public async Task F12_the_fence_time_is_the_commit_time()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);
        var observedBySupervisor = Clock.GetUtcNow();
        Clock.Advance(TimeSpan.FromSeconds(42));

        var result = await journal.ConfirmFenceAsync(ProcessDeath(a, observedAt: observedBySupervisor), default);

        Assert.True(result.Accepted);
        var entry = Assert.Single((await ProbeAsync(journal, b, operation)).History);
        Assert.Equal(observedBySupervisor + TimeSpan.FromSeconds(42), entry.FencedAt);
        Assert.Equal(FenceSource.ProcessDeathConfirmed, entry.FenceSource);
        Assert.Equal(entry.FencedAt, RepeatSafetyEvaluator.FenceTime(entry));
        Assert.Equal(SideEffectState.Started, entry.SideEffect); // a fence never changes the side-effect state
        Assert.Null(entry.Outcome);
    }

    [Fact]
    public async Task A_host_reboot_with_a_new_boot_id_confirms_process_death()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);

        var result = await journal.ConfirmFenceAsync(ProcessDeath(a) with { Method = FenceMethod.SupervisorHostRebooted, Drain = null, CurrentBootId = "boot-2" }, default);

        Assert.True(result.Accepted);
        Assert.Equal(ReservationStatus.Reserved, (await ProbeAsync(journal, b, operation)).Status);
    }

    protected static FenceEvidence Human(WorkerIncarnation who, ActorRole role = ActorRole.PlatformOperator, FenceMethod method = FenceMethod.HostPoweredOffOrDestroyed) =>
        new(FenceSource.HumanConfirmed, who.Id, who.Unit, method, T0, null, null, new HumanConfirmation("operator-7", role, "INC-4821: VM deleted in the cloud console", true));

    /// <summary>F14: only a platform operator with a closed-list method, a reference and an attestation can confirm.</summary>
    [Fact]
    public async Task F14_human_confirmation_requires_a_platform_operator_and_complete_evidence()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        await UnfencedAttemptAsync(journal, a, operation);

        Assert.Equal("actor-not-platform-operator", (await journal.ConfirmFenceAsync(Human(a, ActorRole.TenantUser), default)).RejectionReason);
        Assert.Equal("method-not-allowed", (await journal.ConfirmFenceAsync(Human(a, method: FenceMethod.SupervisorUnitEmptyAndDrained), default)).RejectionReason);
        Assert.Equal("drain-not-proven", (await journal.ConfirmFenceAsync(Human(a, method: FenceMethod.DrainVerified), default)).RejectionReason);
        Assert.Equal(ReservationStatus.AwaitingFence, (await ProbeAsync(journal, b, operation)).Status);

        var accepted = await journal.ConfirmFenceAsync(Human(a), default);

        Assert.True(accepted.Accepted);
        var entry = Assert.Single((await ProbeAsync(journal, b, operation)).History);
        Assert.Equal(FenceSource.HumanConfirmed, entry.FenceSource);
        Assert.Equal(accepted.FencedAt, entry.FencedAt);
    }

    [Fact]
    public async Task A_confirmation_for_a_live_incarnation_abandons_and_fences_its_started_attempts()
    {
        var (journal, a, b) = await SetUpAsync();
        var operation = Guid.NewGuid();
        var reservation = await StartAsync(journal, a, operation);
        await journal.SideEffectStartingAsync(reservation, "submit", TimeSpan.FromSeconds(15), default);

        var result = await new FakeSupervisor(journal, Clock).TryConfirmProcessDeathAsync(a, new FakeContainmentUnit());

        Assert.True(result!.Accepted);
        var entry = Assert.Single((await ProbeAsync(journal, b, operation)).History);
        Assert.Equal(AttemptState.Abandoned, entry.State);
        Assert.NotNull(entry.FencedAt);
        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => journal.MarkObservationAsync(reservation, default))).Code);
    }
}
