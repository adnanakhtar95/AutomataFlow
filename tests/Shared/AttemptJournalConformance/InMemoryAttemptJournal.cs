using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Execution;
using FenceSourceKind = AutomataFlow.Application.Workflows.Execution.FenceSource;

namespace AutomataFlow.Tests.Journal;

/// <summary>A controllable journal clock for tests.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public void Set(DateTimeOffset value)
    {
        lock (_gate)
        {
            _now = value;
        }
    }
}

/// <summary>
/// TEST-ONLY in-memory implementation of <see cref="IAttemptJournal"/> (ADR-058: never in a production assembly;
/// <c>IsDurable = false</c>). One lock makes every operation atomic, which is what the PostgreSQL journal achieves with a
/// unique partial index and conditional writes in Phase 5. It is proven by the shared conformance suite.
/// </summary>
public class InMemoryAttemptJournal(TimeProvider clock, TimeSpan? leaseLength = null, TimeSpan? leaseSafetyMargin = null) : IAttemptJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Org, Guid Op), List<Entry>> _entries = [];
    private readonly Dictionary<WorkerIncarnationId, WorkerIncarnation> _incarnations = [];
    private readonly List<(WorkerIncarnationId Incarnation, DateTimeOffset At)> _killRequests = [];
    private long _nextToken;

    public TimeSpan LeaseLength { get; } = leaseLength ?? TimeSpan.FromMinutes(5);

    public TimeSpan LeaseSafetyMargin { get; } = leaseSafetyMargin ?? TimeSpan.FromSeconds(10);

    public JournalCapabilities Capabilities { get; } = new(IsDurable: false, SupportsFencing: true);

    /// <summary>Diagnostics recorded with a fence (kill-request time, supervisor observation time); never fence times.</summary>
    public IReadOnlyList<(WorkerIncarnationId Incarnation, DateTimeOffset At)> KillRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _killRequests];
            }
        }
    }

    public Task RegisterIncarnationAsync(WorkerIncarnation incarnation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _incarnations[incarnation.Id] = incarnation;
        }

        return Task.CompletedTask;
    }

    public virtual Task<AttemptReservationResult> ReserveAsync(Guid organizationId, Guid operationId, ExecutionIntent intent, WorkerIncarnationId incarnation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var entries = EntriesFor(organizationId, operationId);
            Sweep(entries, now);

            ReservationStatus? refusal = !_incarnations.ContainsKey(incarnation) ? ReservationStatus.UnknownIncarnation
                : entries.Any(e => e.State is AttemptState.Reserved or AttemptState.Started) ? ReservationStatus.InFlight
                : entries.Any(e => e.IsUnfenced) ? ReservationStatus.AwaitingFence
                : null;
            var history = entries.Select(e => e.ToSummary(operationId)).ToImmutableArray();
            if (refusal is not null)
            {
                return Task.FromResult(new AttemptReservationResult(refusal.Value, null, history, now));
            }

            var entry = new Entry
            {
                AttemptNumber = entries.Count == 0 ? 1 : entries.Max(e => e.AttemptNumber) + 1,
                Intent = intent,
                State = AttemptState.Reserved,
                Incarnation = incarnation,
                Token = ++_nextToken,
                LeaseExpiresAt = now + LeaseLength,
            };
            entries.Add(entry);
            var reservation = new AttemptReservation(organizationId, operationId, entry.AttemptNumber, intent, entry.Token, incarnation);
            return Task.FromResult(new AttemptReservationResult(ReservationStatus.Reserved, reservation, history, now));
        }
    }

    public virtual Task<DateTimeOffset> StartAsync(AttemptReservation reservation, AttemptSnapshot snapshot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = Fenced(reservation, AttemptState.Reserved, out var now);
            entry.State = AttemptState.Started;
            entry.StartedAt = now;
            entry.Snapshot = snapshot;
            return Task.FromResult(now);
        }
    }

    public virtual Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = Fenced(reservation, AttemptState.Started, out var now);
            if (entry.LeaseExpiresAt - now < stepTimeout + LeaseSafetyMargin)
            {
                throw new AttemptJournalException(JournalErrorCodes.LeaseTooShort, "The remaining lease is shorter than the step timeout plus the safety margin.");
            }

            entry.CheckpointAt ??= now;
            entry.CheckpointStep ??= stepId;
        }

        return Task.CompletedTask;
    }

    public virtual Task<DateTimeOffset> MarkObservationAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = Fenced(reservation, AttemptState.Started, out var now);
            if (entry.Intent != ExecutionIntent.Reconcile)
            {
                throw new AttemptJournalException(JournalErrorCodes.InvalidState, "Only reconcile attempts record an observation time.");
            }

            entry.ObservedAt = now;
            return Task.FromResult(now);
        }
    }

    public virtual Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var entries = EntriesFor(reservation.OrganizationId, reservation.OperationId);
            Sweep(entries, now);
            var entry = entries.SingleOrDefault(e => e.AttemptNumber == reservation.AttemptNumber && e.Token == reservation.FencingToken)
                ?? throw new AttemptJournalException(JournalErrorCodes.Fenced, "Unknown reservation.");

            if (entry.State is not (AttemptState.Started or AttemptState.Abandoned) || entry.Outcome is not null)
            {
                throw new AttemptJournalException(JournalErrorCodes.InvalidState, $"Cannot complete an entry in state {entry.State}.");
            }

            // In time: Started → Completed. Late (after lease loss): the entry stays Abandoned and only gains information.
            if (entry.State == AttemptState.Started)
            {
                entry.State = AttemptState.Completed;
            }

            entry.Outcome = completion.Outcome;
            entry.Error = completion.Error;
            entry.CompletionSideEffect = completion.SideEffect;
            entry.ExecutionFenced = completion.ExecutionFenced;
            entry.FinishedAt = now;
            entry.Reconciliation = completion.Reconciliation;
            return Task.FromResult(now);
        }
    }

    public virtual Task ReleaseAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entries = EntriesFor(reservation.OrganizationId, reservation.OperationId);
            entries.RemoveAll(e => e.AttemptNumber == reservation.AttemptNumber && e.Token == reservation.FencingToken && e.State == AttemptState.Reserved);
        }

        return Task.CompletedTask;
    }

    public Task RecordKillRequestAsync(WorkerIncarnationId incarnation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _killRequests.Add((incarnation, clock.GetUtcNow()));
        }

        return Task.CompletedTask;
    }

    public virtual Task<FenceConfirmationResult> ConfirmFenceAsync(FenceEvidence evidence, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _incarnations.TryGetValue(evidence.Incarnation, out var recorded);
            var rejection = FenceEvidenceValidator.Validate(evidence, recorded);
            if (rejection is not null)
            {
                return Task.FromResult(new FenceConfirmationResult(false, rejection, [], null));
            }

            // The incarnation is proven gone: its live entries can no longer act. Commit time is the fence time.
            var now = clock.GetUtcNow();
            var fenced = ImmutableArray.CreateBuilder<(Guid, int)>();
            foreach (var ((_, op), entries) in _entries)
            {
                entries.RemoveAll(e => e.Incarnation == evidence.Incarnation && e.State == AttemptState.Reserved);
                foreach (var entry in entries.Where(e => e.Incarnation == evidence.Incarnation))
                {
                    if (entry.State == AttemptState.Started)
                    {
                        entry.State = AttemptState.Abandoned;
                        entry.AbandonedAt = now;
                    }

                    if (entry.IsUnfenced)
                    {
                        entry.FencedAt = now;
                        entry.FenceSource = evidence.Source;
                        entry.FenceEvidence = evidence;
                        fenced.Add((op, entry.AttemptNumber));
                    }
                }
            }

            return Task.FromResult(new FenceConfirmationResult(true, null, fenced.ToImmutable(), now));
        }
    }

    /// <summary>Test inspection: the evidence stored with a fence (audit fields).</summary>
    public FenceEvidence? EvidenceFor(Guid organizationId, Guid operationId, int attemptNumber)
    {
        lock (_gate)
        {
            return EntriesFor(organizationId, operationId).Single(e => e.AttemptNumber == attemptNumber).FenceEvidence;
        }
    }

    /// <summary>The history as the evaluator sees it (sweeps lease expiry first, like every journal operation).</summary>
    public ImmutableArray<AttemptSummary> History(Guid organizationId, Guid operationId)
    {
        lock (_gate)
        {
            var entries = EntriesFor(organizationId, operationId);
            Sweep(entries, clock.GetUtcNow());
            return [.. entries.Select(e => e.ToSummary(operationId))];
        }
    }

    private List<Entry> EntriesFor(Guid organizationId, Guid operationId)
    {
        if (!_entries.TryGetValue((organizationId, operationId), out var entries))
        {
            entries = [];
            _entries[(organizationId, operationId)] = entries;
        }

        return entries;
    }

    /// <summary>Lease expiry: Started → Abandoned (AbandonedAt = now); expired Reserved entries were never attempts.</summary>
    private static void Sweep(List<Entry> entries, DateTimeOffset now)
    {
        entries.RemoveAll(e => e.State == AttemptState.Reserved && e.LeaseExpiresAt <= now);
        foreach (var entry in entries.Where(e => e.State == AttemptState.Started && e.LeaseExpiresAt <= now))
        {
            entry.State = AttemptState.Abandoned;
            entry.AbandonedAt = now;
        }
    }

    /// <summary>Every write after ReserveAsync is fenced: same token, expected state, lease still valid (journal clock).</summary>
    private Entry Fenced(AttemptReservation reservation, AttemptState expected, out DateTimeOffset now)
    {
        now = clock.GetUtcNow();
        var entries = EntriesFor(reservation.OrganizationId, reservation.OperationId);
        Sweep(entries, now);
        var entry = entries.SingleOrDefault(e => e.AttemptNumber == reservation.AttemptNumber && e.Token == reservation.FencingToken);
        if (entry is null || entry.State != expected || entry.LeaseExpiresAt <= now)
        {
            throw new AttemptJournalException(JournalErrorCodes.Fenced, "The reservation is no longer valid (lease lost or fenced).");
        }

        return entry;
    }

    private sealed class Entry
    {
        public int AttemptNumber { get; init; }

        public ExecutionIntent Intent { get; init; }

        public AttemptState State { get; set; }

        public WorkerIncarnationId Incarnation { get; init; }

        public long Token { get; init; }

        public DateTimeOffset LeaseExpiresAt { get; init; }

        public AttemptSnapshot? Snapshot { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? CheckpointAt { get; set; }

        public string? CheckpointStep { get; set; }

        public DateTimeOffset? ObservedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public DateTimeOffset? AbandonedAt { get; set; }

        public WorkflowOutcome? Outcome { get; set; }

        public ErrorSummary? Error { get; set; }

        public SideEffectState? CompletionSideEffect { get; set; }

        public bool ExecutionFenced { get; set; }

        public DateTimeOffset? FencedAt { get; set; }

        public FenceSourceKind? FenceSource { get; set; }

        public FenceEvidence? FenceEvidence { get; set; }

        public ReconciliationEvidence? Reconciliation { get; set; }

        /// <summary>Started if a checkpoint exists or the completion says so.</summary>
        public SideEffectState SideEffect => CheckpointAt is not null || CompletionSideEffect is SideEffectState.Started ? SideEffectState.Started
            : CompletionSideEffect is SideEffectState.Unknown ? SideEffectState.Unknown
            : SideEffectState.NotStarted;

        public bool IsUnfenced => SideEffect != SideEffectState.NotStarted
            && State is AttemptState.Completed or AttemptState.Abandoned
            && !ExecutionFenced && FencedAt is null;

        public AttemptSummary ToSummary(Guid operationId) => new(
            operationId, AttemptNumber, Intent, State, Outcome, Error, SideEffect, Snapshot, StartedAt, CheckpointAt, FinishedAt, AbandonedAt,
            ExecutionFenced, FencedAt, FenceSource ?? (ExecutionFenced ? FenceSourceKind.ExecutorEgressRevoked
                : State == AttemptState.Abandoned && SideEffect == SideEffectState.NotStarted ? FenceSourceKind.NoCheckpoint : null),
            Reconciliation);
    }
}
