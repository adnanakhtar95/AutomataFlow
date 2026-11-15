using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutomataFlow.Application.Workflows.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AutomataFlow.Infrastructure.Persistence.Journal;

public sealed class AttemptJournalOptions
{
    public const string SectionName = "Journal";

    /// <summary>Attempt lease. Must cover the longest side-effect step (120 s) plus the margin (checked at Worker startup).</summary>
    public int LeaseSeconds { get; set; } = 180;

    public int LeaseSafetyMarginSeconds { get; set; } = 10;
}

/// <summary>
/// The production attempt journal (docs/PHASE4_PLAN.md §7.2, §7.4; ADR-049, ADR-056, ADR-058, ADR-059), proven by the
/// shared conformance suite. Every operation is ONE transaction that:
/// <list type="number">
/// <item>forces a durable commit (<c>SET LOCAL synchronous_commit = on</c>), whatever the server default;</item>
/// <item>serializes on the operation with a transaction-scoped advisory lock, and only then reads the clock;</item>
/// <item>sweeps lease expiry (Reserved deleted, Started → Abandoned) before deciding anything;</item>
/// <item>fences every write after <c>ReserveAsync</c> on the fencing token, the expected state and the lease.</item>
/// </list>
/// The database enforces the rest: <c>Blocking</c> is derived by a trigger and a partial unique index allows one blocking
/// entry per operation; another trigger keeps history append-only. A commit whose outcome is unknown (connection lost
/// during COMMIT) is reported as <see cref="JournalErrorCodes.Unavailable"/> and never assumed to have succeeded or failed.
/// <para>The clock is the database's <c>clock_timestamp()</c>. A <see cref="TimeProvider"/> override exists only so the
/// conformance suite can drive time; production registration never passes one (architecture-tested).</para>
/// </summary>
public sealed class PostgresAttemptJournal : IAttemptJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider? _clockOverride;
    private readonly ILogger? _logger;

    public PostgresAttemptJournal(NpgsqlDataSource source, IOptions<AttemptJournalOptions> options, ILogger<PostgresAttemptJournal>? logger = null)
        : this(source, options, null, logger)
    {
    }

    /// <summary>Test-only constructor: <paramref name="clockOverride"/> replaces the database clock.</summary>
    internal PostgresAttemptJournal(NpgsqlDataSource source, IOptions<AttemptJournalOptions> options, TimeProvider? clockOverride, ILogger<PostgresAttemptJournal>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clockOverride = clockOverride;
        _logger = logger;
        LeaseLength = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        LeaseSafetyMargin = TimeSpan.FromSeconds(options.Value.LeaseSafetyMarginSeconds);
    }

    /// <summary>Test hook (internal): runs inside the transaction immediately before COMMIT.</summary>
    internal Func<NpgsqlTransaction, Task>? BeforeCommitForTests { get; set; }

    public TimeSpan LeaseLength { get; }

    public TimeSpan LeaseSafetyMargin { get; }

    public JournalCapabilities Capabilities => new(IsDurable: true, SupportsFencing: true, LeaseLength, LeaseSafetyMargin);

    /// <summary>
    /// Startup check (fail closed): the server must flush its WAL (<c>fsync = on</c>). Per-transaction durability is forced
    /// by every journal transaction itself.
    /// </summary>
    public async Task<bool> VerifyServerDurabilityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SHOW fsync", connection);
        return string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken), "on", StringComparison.OrdinalIgnoreCase);
    }

    public Task RegisterIncarnationAsync(WorkerIncarnation incarnation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incarnation);
        return InTransactionAsync(null, async (tx, now) =>
        {
            await ExecuteAsync(tx, """
                INSERT INTO "WorkerIncarnations" ("Id","HostId","BootId","ProcessId","ProcessStartTime","UnitKind","UnitName","ConnectionLedger","RegisteredAt","LastHeartbeatAt")
                VALUES (@id,@host,@boot,@pid,@start,@kind,@unit,@ledger,@now,@now)
                """, cancellationToken,
                ("id", incarnation.Id.Value), ("host", incarnation.HostId), ("boot", incarnation.BootId), ("pid", incarnation.ProcessId),
                ("start", incarnation.ProcessStartTime.ToUniversalTime()), ("kind", incarnation.Unit.Kind.ToString()), ("unit", incarnation.Unit.Name),
                ("ledger", incarnation.ConnectionLedger), ("now", now));
            return true;
        }, cancellationToken);
    }

    public Task HeartbeatAsync(WorkerIncarnationId incarnation, CancellationToken cancellationToken) =>
        InTransactionAsync(null, async (tx, now) =>
        {
            await ExecuteAsync(tx, """UPDATE "WorkerIncarnations" SET "LastHeartbeatAt" = @now WHERE "Id" = @id""", cancellationToken, ("id", incarnation.Value), ("now", now));
            return true;
        }, cancellationToken);

    public Task<(ImmutableArray<AttemptSummary> History, DateTimeOffset Now)> ReadHistoryAsync(Guid organizationId, Guid operationId, CancellationToken cancellationToken) =>
        InTransactionAsync((organizationId, operationId), async (tx, now) =>
            ((await LoadAsync(tx, organizationId, operationId, cancellationToken)).Select(e => e.Summary(operationId)).ToImmutableArray(), now), cancellationToken);

    public Task<AttemptReservationResult> ReserveAsync(Guid organizationId, Guid operationId, ExecutionIntent intent, WorkerIncarnationId incarnation, CancellationToken cancellationToken) =>
        InTransactionAsync((organizationId, operationId), async (tx, now) =>
        {
            var entries = await LoadAsync(tx, organizationId, operationId, cancellationToken);
            var history = entries.Select(e => e.Summary(operationId)).ToImmutableArray();
            var known = await ScalarAsync<bool>(tx, """SELECT EXISTS (SELECT 1 FROM "WorkerIncarnations" WHERE "Id" = @id)""", cancellationToken, ("id", incarnation.Value));
            ReservationStatus? refusal = !known ? ReservationStatus.UnknownIncarnation
                : entries.Any(e => e.State is AttemptState.Reserved or AttemptState.Started) ? ReservationStatus.InFlight
                : entries.Any(e => e.Blocking) ? ReservationStatus.AwaitingFence
                : null;
            if (refusal is not null)
            {
                return new AttemptReservationResult(refusal.Value, null, history, now);
            }

            var attempt = entries.Count == 0 ? 1 : entries.Max(e => e.AttemptNumber) + 1;
            var token = await ScalarAsync<long>(tx, """
                INSERT INTO "AttemptJournal" ("OrganizationId","OperationId","AttemptNumber","Intent","State","IncarnationId","LeaseExpiresAt","Blocking","ExecutionFenced")
                VALUES (@org,@op,@n,@intent,'Reserved',@inc,@lease,true,false)
                RETURNING "FencingToken"
                """, cancellationToken,
                ("org", organizationId), ("op", operationId), ("n", attempt), ("intent", intent.ToString()), ("inc", incarnation.Value), ("lease", now + LeaseLength));
            return new AttemptReservationResult(ReservationStatus.Reserved, new AttemptReservation(organizationId, operationId, attempt, intent, token, incarnation), history, now);
        }, cancellationToken);

    public Task<DateTimeOffset> StartAsync(AttemptReservation reservation, AttemptSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(snapshot);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, now) =>
        {
            await FencedAsync(tx, reservation, AttemptState.Reserved, now, cancellationToken);
            await ExecuteAsync(tx, """
                UPDATE "AttemptJournal" SET "State" = 'Started', "StartedAt" = @now, "SnapshotJson" = @snapshot
                WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n
                """, cancellationToken, Key(reservation, ("now", now), ("snapshot", Jsonb(snapshot))));
            return now;
        }, cancellationToken);
    }

    public Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, now) =>
        {
            var entry = await FencedAsync(tx, reservation, AttemptState.Started, now, cancellationToken);
            if (entry.LeaseExpiresAt - now < stepTimeout + LeaseSafetyMargin)
            {
                throw new AttemptJournalException(JournalErrorCodes.LeaseTooShort, "The remaining lease is shorter than the step timeout plus the safety margin.");
            }

            await ExecuteAsync(tx, """
                UPDATE "AttemptJournal" SET "CheckpointAt" = COALESCE("CheckpointAt", @now), "CheckpointStep" = COALESCE("CheckpointStep", @step)
                WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n
                """, cancellationToken, Key(reservation, ("now", now), ("step", stepId)));
            return true;
        }, cancellationToken);
    }

    public Task<DateTimeOffset> MarkObservationAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, now) =>
        {
            var entry = await FencedAsync(tx, reservation, AttemptState.Started, now, cancellationToken);
            if (entry.Intent != ExecutionIntent.Reconcile)
            {
                throw new AttemptJournalException(JournalErrorCodes.InvalidState, "Only reconcile attempts record an observation time.");
            }

            await ExecuteAsync(tx, """UPDATE "AttemptJournal" SET "ObservedAt" = @now WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n""",
                cancellationToken, Key(reservation, ("now", now)));
            return now;
        }, cancellationToken);
    }

    public Task<DateTimeOffset> RenewLeaseAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, now) =>
        {
            var entry = await FindAsync(tx, reservation, cancellationToken);
            if (entry is null || entry.State is not (AttemptState.Reserved or AttemptState.Started) || entry.LeaseExpiresAt <= now)
            {
                throw new AttemptJournalException(JournalErrorCodes.Fenced, "The reservation is no longer valid (lease lost or fenced).");
            }

            var expires = now + LeaseLength;
            await ExecuteAsync(tx, """UPDATE "AttemptJournal" SET "LeaseExpiresAt" = @lease WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n""",
                cancellationToken, Key(reservation, ("lease", expires)));
            return expires;
        }, cancellationToken);
    }

    public Task<DateTimeOffset> CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(completion);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, now) =>
        {
            var entry = await FindAsync(tx, reservation, cancellationToken)
                ?? throw new AttemptJournalException(JournalErrorCodes.Fenced, "Unknown reservation.");
            if (entry.State is not (AttemptState.Started or AttemptState.Abandoned) || entry.Outcome is not null)
            {
                throw new AttemptJournalException(JournalErrorCodes.InvalidState, $"Cannot complete an entry in state {entry.State}.");
            }

            // In time: Started → Completed. Late (after lease loss): stays Abandoned and only gains information.
            await ExecuteAsync(tx, """
                UPDATE "AttemptJournal" SET
                  "State" = CASE WHEN "State" = 'Started' THEN 'Completed' ELSE "State" END,
                  "Outcome" = @outcome, "ErrorCode" = @code, "ErrorCategory" = @category, "ErrorTransient" = @transient,
                  "CompletionSideEffect" = @sideEffect, "ExecutionFenced" = @fenced, "FinishedAt" = @now, "ReconciliationJson" = @reconciliation
                WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n
                """, cancellationToken, Key(reservation,
                    ("outcome", completion.Outcome.ToString()), ("code", (object?)completion.Error?.Code), ("category", (object?)completion.Error?.Category.ToString()),
                    ("transient", (object?)completion.Error?.IsTransient), ("sideEffect", completion.SideEffect.ToString()), ("fenced", completion.ExecutionFenced),
                    ("now", now), ("reconciliation", completion.Reconciliation is null ? null : Jsonb(completion.Reconciliation))));
            return now;
        }, cancellationToken);
    }

    public Task ReleaseAsync(AttemptReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return InTransactionAsync((reservation.OrganizationId, reservation.OperationId), async (tx, _) =>
        {
            await ExecuteAsync(tx, """
                DELETE FROM "AttemptJournal"
                WHERE "OrganizationId" = @org AND "OperationId" = @op AND "AttemptNumber" = @n AND "FencingToken" = @token AND "State" = 'Reserved'
                """, cancellationToken, Key(reservation, ("token", reservation.FencingToken)));
            return true;
        }, cancellationToken);
    }

    public Task RecordKillRequestAsync(WorkerIncarnationId incarnation, CancellationToken cancellationToken) =>
        InTransactionAsync(null, async (tx, now) =>
        {
            await ExecuteAsync(tx, """
                INSERT INTO "WorkerKillRequests" ("IncarnationId","RequestedAt")
                SELECT @id, @now WHERE EXISTS (SELECT 1 FROM "WorkerIncarnations" WHERE "Id" = @id)
                """, cancellationToken, ("id", incarnation.Value), ("now", now));
            return true;
        }, cancellationToken);

    public async Task<FenceConfirmationResult> ConfirmFenceAsync(FenceEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await ExecuteAsync(tx, "SET LOCAL synchronous_commit TO on", cancellationToken);

            // Lock every operation of the incarnation in a fixed order, so no per-operation call interleaves.
            var operations = new List<(Guid Org, Guid Op)>();
            await using (var reader = await Command(tx, """
                SELECT DISTINCT "OrganizationId", "OperationId" FROM "AttemptJournal" WHERE "IncarnationId" = @id ORDER BY 1, 2
                """, ("id", evidence.Incarnation.Value)).ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    operations.Add((reader.GetGuid(0), reader.GetGuid(1)));
                }
            }

            foreach (var (org, op) in operations)
            {
                await LockAsync(tx, org, op, cancellationToken);
            }

            var now = await NowAsync(tx, cancellationToken);
            var recorded = await LoadIncarnationAsync(tx, evidence.Incarnation, cancellationToken);
            var rejection = FenceEvidenceValidator.Validate(evidence, recorded);
            var fenced = ImmutableArray.CreateBuilder<(Guid, int)>();
            if (rejection is null)
            {
                await ExecuteAsync(tx, """DELETE FROM "AttemptJournal" WHERE "IncarnationId" = @id AND "State" = 'Reserved'""", cancellationToken, ("id", evidence.Incarnation.Value));
                await ExecuteAsync(tx, """UPDATE "AttemptJournal" SET "State" = 'Abandoned', "AbandonedAt" = @now WHERE "IncarnationId" = @id AND "State" = 'Started'""",
                    cancellationToken, ("id", evidence.Incarnation.Value), ("now", now));
                await using var reader = await Command(tx, """
                    UPDATE "AttemptJournal" SET "FencedAt" = @now, "FenceSource" = @source, "FenceEvidenceJson" = @evidence
                    WHERE "IncarnationId" = @id AND "Blocking" AND "State" IN ('Completed', 'Abandoned')
                    RETURNING "OperationId", "AttemptNumber"
                    """, ("id", evidence.Incarnation.Value), ("now", now), ("source", evidence.Source.ToString()), ("evidence", Jsonb(evidence))).ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    fenced.Add((reader.GetGuid(0), reader.GetInt32(1)));
                }
            }

            // Every confirmation is audited, accepted or not (§7.5.6).
            await ExecuteAsync(tx, """
                INSERT INTO "FenceConfirmations" ("IncarnationId","Source","Method","ActorUserId","EvidenceReference","AttestedPersonally","ObservedAt","RecordedAt","Accepted","RejectionReason","FencedAttempts","EvidenceJson")
                VALUES (@id,@source,@method,@actor,@reference,@attested,@observed,@now,@accepted,@reason,@count,@evidence)
                """, cancellationToken,
                ("id", evidence.Incarnation.Value), ("source", evidence.Source.ToString()), ("method", evidence.Method.ToString()), ("actor", (object?)evidence.Human?.ActorUserId),
                ("reference", (object?)evidence.Human?.EvidenceReference), ("attested", evidence.Human?.AttestedPersonally ?? false), ("observed", evidence.ObservedAt.ToUniversalTime()),
                ("now", now), ("accepted", rejection is null), ("reason", (object?)rejection), ("count", fenced.Count), ("evidence", Jsonb(evidence)));
            await CommitAsync(tx, cancellationToken);
            _logger?.LogInformation("Fence confirmation {Accepted} {IncarnationId} {FenceSource} {FenceMethod} {FencedAttempts} {RejectionReason}",
                rejection is null, evidence.Incarnation.Value, evidence.Source, evidence.Method, fenced.Count, rejection);
            return rejection is null
                ? new FenceConfirmationResult(true, null, fenced.ToImmutable(), now)
                : new FenceConfirmationResult(false, rejection, [], null);
        }
        catch (NpgsqlException ex)
        {
            throw new AttemptJournalException("The attempt journal is unavailable.", ex);
        }
    }

    // ----- transaction plumbing -----

    private async Task<T> InTransactionAsync<T>((Guid Org, Guid Op)? operation, Func<NpgsqlTransaction, DateTimeOffset, Task<T>> body, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await ExecuteAsync(tx, "SET LOCAL synchronous_commit TO on", cancellationToken);
            if (operation is { } key)
            {
                await LockAsync(tx, key.Org, key.Op, cancellationToken);
            }

            // The clock is read only AFTER the serialization point (F8).
            var now = await NowAsync(tx, cancellationToken);
            if (operation is { } swept)
            {
                await SweepAsync(tx, swept.Org, swept.Op, now, cancellationToken);
            }

            var result = await body(tx, now);
            if (BeforeCommitForTests is { } hook)
            {
                await hook(tx);
            }

            await CommitAsync(tx, cancellationToken);
            return result;
        }
        catch (NpgsqlException ex)
        {
            throw new AttemptJournalException("The attempt journal is unavailable.", ex);
        }
    }

    /// <summary>A failure during COMMIT leaves the outcome unknown: it is reported as unavailable, never as success or failure.</summary>
    private static async Task CommitAsync(NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        try
        {
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            throw new AttemptJournalException(JournalErrorCodes.Unavailable, "The journal commit outcome is unknown; it is not assumed to have succeeded.");
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _source.OpenConnectionAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            throw new AttemptJournalException("The attempt journal is unavailable.", ex);
        }
    }

    private static Task LockAsync(NpgsqlTransaction tx, Guid org, Guid op, CancellationToken cancellationToken) =>
        ExecuteAsync(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 7))", cancellationToken, ("key", $"journal:{org:N}:{op:N}"));

    private async Task<DateTimeOffset> NowAsync(NpgsqlTransaction tx, CancellationToken cancellationToken) =>
        _clockOverride is { } clock ? clock.GetUtcNow() : await ScalarAsync<DateTimeOffset>(tx, "SELECT clock_timestamp()", cancellationToken);

    private static async Task SweepAsync(NpgsqlTransaction tx, Guid org, Guid op, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await ExecuteAsync(tx, """
            DELETE FROM "AttemptJournal" WHERE "OrganizationId" = @org AND "OperationId" = @op AND "State" = 'Reserved' AND "LeaseExpiresAt" <= @now
            """, cancellationToken, ("org", org), ("op", op), ("now", now));
        await ExecuteAsync(tx, """
            UPDATE "AttemptJournal" SET "State" = 'Abandoned', "AbandonedAt" = @now
            WHERE "OrganizationId" = @org AND "OperationId" = @op AND "State" = 'Started' AND "LeaseExpiresAt" <= @now
            """, cancellationToken, ("org", org), ("op", op), ("now", now));
    }

    private static async Task<Entry> FencedAsync(NpgsqlTransaction tx, AttemptReservation reservation, AttemptState expected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entry = await FindAsync(tx, reservation, cancellationToken);
        if (entry is null || entry.State != expected || entry.LeaseExpiresAt <= now)
        {
            throw new AttemptJournalException(JournalErrorCodes.Fenced, "The reservation is no longer valid (lease lost or fenced).");
        }

        return entry;
    }

    private static async Task<Entry?> FindAsync(NpgsqlTransaction tx, AttemptReservation reservation, CancellationToken cancellationToken) =>
        (await LoadAsync(tx, reservation.OrganizationId, reservation.OperationId, cancellationToken))
            .SingleOrDefault(e => e.AttemptNumber == reservation.AttemptNumber && e.FencingToken == reservation.FencingToken);

    private static async Task<List<Entry>> LoadAsync(NpgsqlTransaction tx, Guid org, Guid op, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        await using var reader = await Command(tx, """
            SELECT "AttemptNumber","Intent","State","FencingToken","LeaseExpiresAt","Blocking","SnapshotJson","StartedAt","CheckpointAt","ObservedAt",
                   "FinishedAt","AbandonedAt","Outcome","ErrorCode","ErrorCategory","ErrorTransient","CompletionSideEffect","ExecutionFenced","FencedAt",
                   "FenceSource","ReconciliationJson"
            FROM "AttemptJournal" WHERE "OrganizationId" = @org AND "OperationId" = @op ORDER BY "AttemptNumber"
            """, ("org", org), ("op", op)).ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? At(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            entries.Add(new Entry(
                reader.GetInt32(0), Enum.Parse<ExecutionIntent>(reader.GetString(1)), Enum.Parse<AttemptState>(reader.GetString(2)), reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5), Text(6) is { } snapshot ? JsonSerializer.Deserialize<AttemptSnapshot>(snapshot, Json) : null,
                At(7), At(8), At(9), At(10), At(11), Text(12) is { } outcome ? Enum.Parse<WorkflowOutcome>(outcome) : null,
                Text(13) is { } code ? new ErrorSummary(code, Enum.Parse<ErrorCategory>(Text(14)!), !reader.IsDBNull(15) && reader.GetBoolean(15)) : null,
                Text(16) is { } side ? Enum.Parse<SideEffectState>(side) : null, reader.GetBoolean(17), At(18),
                Text(19) is { } source ? Enum.Parse<FenceSource>(source) : null,
                Text(20) is { } evidence ? JsonSerializer.Deserialize<ReconciliationEvidence>(evidence, Json) : null));
        }

        return entries;
    }

    private static async Task<WorkerIncarnation?> LoadIncarnationAsync(NpgsqlTransaction tx, WorkerIncarnationId id, CancellationToken cancellationToken)
    {
        await using var reader = await Command(tx, """
            SELECT "HostId","BootId","ProcessId","ProcessStartTime","UnitKind","UnitName","ConnectionLedger" FROM "WorkerIncarnations" WHERE "Id" = @id
            """, ("id", id.Value)).ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkerIncarnation(id, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3),
            new ContainmentUnit(Enum.Parse<ContainmentKind>(reader.GetString(4)), reader.GetString(5)), reader.GetString(6));
    }

    private static (string, object?)[] Key(AttemptReservation reservation, params (string, object?)[] more) =>
        [("org", reservation.OrganizationId), ("op", reservation.OperationId), ("n", reservation.AttemptNumber), .. more];

    private static NpgsqlParameter Jsonb<T>(T value) => new() { Value = JsonSerializer.Serialize(value, Json), NpgsqlDbType = NpgsqlDbType.Jsonb };

    private static NpgsqlCommand Command(NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (name, value) in parameters)
        {
            if (value is NpgsqlParameter p)
            {
                p.ParameterName = name;
                command.Parameters.Add(p);
            }
            else
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }

    private static async Task ExecuteAsync(NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, params (string, object?)[] parameters)
    {
        await using var command = Command(tx, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, params (string, object?)[] parameters)
    {
        await using var command = Command(tx, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetFieldValue<T>(0);
    }

    private sealed record Entry(
        int AttemptNumber, ExecutionIntent Intent, AttemptState State, long FencingToken, DateTimeOffset LeaseExpiresAt, bool Blocking, AttemptSnapshot? Snapshot,
        DateTimeOffset? StartedAt, DateTimeOffset? CheckpointAt, DateTimeOffset? ObservedAt, DateTimeOffset? FinishedAt, DateTimeOffset? AbandonedAt,
        WorkflowOutcome? Outcome, ErrorSummary? Error, SideEffectState? CompletionSideEffect, bool ExecutionFenced, DateTimeOffset? FencedAt,
        FenceSource? FenceSource, ReconciliationEvidence? Reconciliation)
    {
        public SideEffectState SideEffect => CheckpointAt is not null || CompletionSideEffect is SideEffectState.Started ? SideEffectState.Started
            : CompletionSideEffect is SideEffectState.Unknown ? SideEffectState.Unknown
            : SideEffectState.NotStarted;

        public AttemptSummary Summary(Guid operationId) => new(
            operationId, AttemptNumber, Intent, State, Outcome, Error, SideEffect, Snapshot, StartedAt, CheckpointAt, FinishedAt, AbandonedAt,
            ExecutionFenced, FencedAt, FenceSource ?? (ExecutionFenced ? Application.Workflows.Execution.FenceSource.ExecutorEgressRevoked
                : State == AttemptState.Abandoned && SideEffect == SideEffectState.NotStarted ? Application.Workflows.Execution.FenceSource.NoCheckpoint : null),
            Reconciliation);
    }
}
