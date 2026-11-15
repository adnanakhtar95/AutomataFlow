using System.Collections.Immutable;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>A Worker incarnation id: a random GUID generated at process start, never reused (§7.5.1).</summary>
public readonly record struct WorkerIncarnationId(Guid Value);

public enum ContainmentKind
{
    /// <summary>Linux cgroup v2 unit (container cgroup or systemd scope).</summary>
    LinuxCgroup,

    /// <summary>Windows Job Object created by the supervisor.</summary>
    WindowsJobObject,
}

/// <summary>The containment unit holding every process that can act for an incarnation's attempts.</summary>
public sealed record ContainmentUnit(ContainmentKind Kind, string Name);

/// <summary>Registered in the journal at Worker startup, before any reservation (§7.5.1).</summary>
public sealed record WorkerIncarnation(
    WorkerIncarnationId Id,
    string HostId,
    string BootId,
    int ProcessId,
    DateTimeOffset ProcessStartTime,
    ContainmentUnit Unit,
    string ConnectionLedger);

/// <summary>Journal properties; production requires both (§7.4, ADR-058).</summary>
public sealed record JournalCapabilities(bool IsDurable, bool SupportsFencing);

/// <summary>A reserved attempt slot with its fencing token (§7.2).</summary>
public sealed record AttemptReservation(Guid OrganizationId, Guid OperationId, int AttemptNumber, ExecutionIntent Intent, long FencingToken, WorkerIncarnationId Incarnation);

public enum ReservationStatus
{
    Reserved,

    /// <summary>Another attempt is Reserved or Started with a valid lease.</summary>
    InFlight,

    /// <summary>An attempt with a side-effect checkpoint is not fenced: nothing may be admitted (S1b, R13).</summary>
    AwaitingFence,

    /// <summary>The caller's incarnation is not registered.</summary>
    UnknownIncarnation,
}

/// <summary>The atomic reservation result: always the history and the journal clock's now.</summary>
public sealed record AttemptReservationResult(ReservationStatus Status, AttemptReservation? Reservation, ImmutableArray<AttemptSummary> History, DateTimeOffset Now);

/// <summary>What the executor reports when an attempt ends (journal assigns <c>FinishedAt</c>).</summary>
public sealed record AttemptCompletion(WorkflowOutcome Outcome, ErrorSummary? Error, SideEffectState SideEffect, bool ExecutionFenced, ReconciliationEvidence? Reconciliation);

/// <summary>Result of <see cref="IAttemptJournal.ConfirmFenceAsync"/>.</summary>
public sealed record FenceConfirmationResult(bool Accepted, string? RejectionReason, ImmutableArray<(Guid OperationId, int AttemptNumber)> FencedAttempts, DateTimeOffset? FencedAt);

/// <summary>Stable journal error codes.</summary>
public static class JournalErrorCodes
{
    /// <summary>The reservation's lease was lost or its fencing token is stale.</summary>
    public const string Fenced = "JournalFenced";

    /// <summary>The remaining lease is shorter than the step timeout plus the safety margin.</summary>
    public const string LeaseTooShort = "CheckpointLeaseTooShort";

    public const string Unavailable = "JournalUnavailable";

    public const string InvalidState = "JournalInvalidState";
}

/// <summary>A journal write was refused (fenced, lease too short, wrong state) or the journal is unavailable.</summary>
public sealed class AttemptJournalException : Exception
{
    public AttemptJournalException()
        : this(JournalErrorCodes.Unavailable, "The attempt journal is unavailable.")
    {
    }

    public AttemptJournalException(string message)
        : this(JournalErrorCodes.Unavailable, message)
    {
    }

    public AttemptJournalException(string message, Exception innerException)
        : base(message, innerException) => Code = JournalErrorCodes.Unavailable;

    public AttemptJournalException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// The attempt journal (docs/PHASE4_PLAN.md §7.2, §7.5; ADR-049, ADR-056, ADR-058, ADR-059). The only source of attempt
/// history. Every timestamp comes from the journal's own clock. Phase 4 defines the contract and proves it with the
/// shared conformance suite against a test-only in-memory journal; there is <b>no production implementation in Phase 4</b>
/// (Phase 5: PostgreSQL). A host without a durable, fencing journal must fail closed at startup.
/// </summary>
public interface IAttemptJournal
{
    JournalCapabilities Capabilities { get; }

    /// <summary>Registers a Worker incarnation before it may reserve anything (§7.5.1).</summary>
    Task RegisterIncarnationAsync(WorkerIncarnation incarnation, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically: marks lease-expired Started entries Abandoned and drops expired Reserved ones; refuses when an entry of
    /// the operation is Reserved, Started (valid lease) or unfenced; otherwise creates a Reserved entry with a fencing token
    /// and a lease, bound to <paramref name="incarnation"/>. Always returns the history and the journal clock's now.
    /// </summary>
    Task<AttemptReservationResult> ReserveAsync(Guid organizationId, Guid operationId, ExecutionIntent intent, WorkerIncarnationId incarnation, CancellationToken cancellationToken);

    /// <summary>Reserved → Started with the definition snapshot and <c>StartedAt</c>. Fenced.</summary>
    Task StartAsync(AttemptReservation reservation, AttemptSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Durably records "side effect starting at <paramref name="stepId"/>" (<c>SideEffectCheckpointAt</c>). Accepted only if
    /// the entry is still Started with this fencing token and the remaining lease ≥ <paramref name="stepTimeout"/> + the
    /// lease safety margin. Returns only after the write is durable; the handler runs only after it returns.
    /// </summary>
    Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken);

    /// <summary>Reconcile attempts: records and returns <c>ObservedAt</c> immediately before the reconcile section. Fenced.</summary>
    Task<DateTimeOffset> MarkObservationAsync(AttemptReservation reservation, CancellationToken cancellationToken);

    /// <summary>
    /// Started → Completed. Also accepted after lease loss as a LATE completion of an Abandoned entry, which only adds
    /// information (outcome, fence); it never admits anything.
    /// </summary>
    Task CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken);

    /// <summary>Reserved → removed (refusals before <c>StartAsync</c>); a released reservation never becomes an attempt.</summary>
    Task ReleaseAsync(AttemptReservation reservation, CancellationToken cancellationToken);

    /// <summary>Diagnostic only: the supervisor requested a kill. It fences nothing (§7.5.3).</summary>
    Task RecordKillRequestAsync(WorkerIncarnationId incarnation, CancellationToken cancellationToken);

    /// <summary>
    /// Validates external fence evidence against the incarnation recorded for the attempts (§7.5); on success fences EVERY
    /// unfenced attempt of that incarnation with <c>FencedAt</c> = journal time at commit.
    /// </summary>
    Task<FenceConfirmationResult> ConfirmFenceAsync(FenceEvidence evidence, CancellationToken cancellationToken);
}
