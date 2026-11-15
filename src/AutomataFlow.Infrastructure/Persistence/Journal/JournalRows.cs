using AutomataFlow.Domain.Common;

namespace AutomataFlow.Infrastructure.Persistence.Journal;

/// <summary>
/// One attempt-journal entry (docs/PHASE4_PLAN.md §7.2). Written only by <c>PostgresAttemptJournal</c> with explicit
/// SQL; EF maps it for the schema only. <see cref="Blocking"/> is true while the entry is Reserved or Started, or has a
/// side effect and is unfenced; a partial unique index on it enforces one in-flight-or-unfenced attempt per operation.
/// </summary>
public sealed class AttemptJournalRow : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }

    public Guid OperationId { get; set; }

    public int AttemptNumber { get; set; }

    public string Intent { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public Guid IncarnationId { get; set; }

    public long FencingToken { get; set; }

    public DateTimeOffset LeaseExpiresAt { get; set; }

    public bool Blocking { get; set; }

    public string? SnapshotJson { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CheckpointAt { get; set; }

    public string? CheckpointStep { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? AbandonedAt { get; set; }

    public string? Outcome { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorCategory { get; set; }

    public bool? ErrorTransient { get; set; }

    public string? CompletionSideEffect { get; set; }

    public bool ExecutionFenced { get; set; }

    public DateTimeOffset? FencedAt { get; set; }

    public string? FenceSource { get; set; }

    public string? FenceEvidenceJson { get; set; }

    public string? ReconciliationJson { get; set; }
}

/// <summary>A registered Worker incarnation (§7.5.1). Platform data, not tenant data.</summary>
public sealed class WorkerIncarnationRow
{
    public Guid Id { get; set; }

    public string HostId { get; set; } = string.Empty;

    public string BootId { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public DateTimeOffset ProcessStartTime { get; set; }

    public string UnitKind { get; set; } = string.Empty;

    public string UnitName { get; set; } = string.Empty;

    public string ConnectionLedger { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset LastHeartbeatAt { get; set; }
}

/// <summary>Diagnostic only: a supervisor kill request. It fences nothing (§7.5.3).</summary>
public sealed class KillRequestRow
{
    public long Id { get; set; }

    public Guid IncarnationId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>Audit trail of every <c>ConfirmFenceAsync</c> call, accepted or rejected (§7.5.6).</summary>
public sealed class FenceConfirmationRow
{
    public long Id { get; set; }

    public Guid IncarnationId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string? ActorUserId { get; set; }

    public string? EvidenceReference { get; set; }

    public bool AttestedPersonally { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public bool Accepted { get; set; }

    public string? RejectionReason { get; set; }

    public int FencedAttempts { get; set; }

    public string EvidenceJson { get; set; } = string.Empty;
}
