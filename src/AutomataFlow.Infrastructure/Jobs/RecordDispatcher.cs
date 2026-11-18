using AutomataFlow.Domain.Jobs;
using Npgsql;

namespace AutomataFlow.Infrastructure.Jobs;

/// <summary>A record claimed by a Worker incarnation.</summary>
public sealed record ClaimedRecord(Guid Id, Guid OrganizationId, Guid JobId, Guid OperationId, RecordIntent NextIntent, DateTimeOffset? AwaitingFenceSince);

/// <summary>
/// The durable queue (docs/ARCHITECTURE.md §5.1): PostgreSQL is the source of truth. Claims use
/// <c>FOR UPDATE SKIP LOCKED</c> with a lease renewed by the Worker's heartbeat; expired claims are recovered.
/// These are the reviewed cross-organization queries of the Worker (§7.1 layer 3): they only move records between
/// queue states and never read customer values; all processing then runs inside the record's own organization scope.
/// </summary>
public sealed class RecordDispatcher(NpgsqlDataSource source)
{
    /// <summary>
    /// Claims up to <paramref name="max"/> due records. Each candidate job row is locked (SKIP LOCKED), so per-job
    /// concurrency (<c>Options.MaxConcurrency</c>) holds across all Workers.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedRecord>> ClaimAsync(Guid incarnation, int max, TimeSpan lease, CancellationToken cancellationToken)
    {
        var claimed = new List<ClaimedRecord>();
        if (max <= 0)
        {
            return claimed;
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var jobs = new List<(Guid Org, Guid Job, int Free)>();
        await using (var select = new NpgsqlCommand("""
            SELECT j."OrganizationId", j."Id",
                   j."Options_MaxConcurrency" - (SELECT count(*) FROM "JobRecords" x WHERE x."OrganizationId" = j."OrganizationId" AND x."JobId" = j."Id" AND x."Status" = 'Running')
            FROM "Jobs" j
            WHERE j."Status" IN ('Pending', 'Running')
              AND EXISTS (SELECT 1 FROM "JobRecords" r WHERE r."OrganizationId" = j."OrganizationId" AND r."JobId" = j."Id"
                          AND r."Status" IN ('Pending', 'AwaitingFence') AND r."NextAttemptAt" <= now())
            ORDER BY j."CreatedAt"
            LIMIT 20
            FOR UPDATE OF j SKIP LOCKED
            """, connection, tx))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                jobs.Add((reader.GetGuid(0), reader.GetGuid(1), (int)reader.GetInt64(2)));
            }
        }

        foreach (var (org, job, free) in jobs)
        {
            var take = Math.Min(free, max - claimed.Count);
            if (take <= 0)
            {
                continue;
            }

            await using var claim = new NpgsqlCommand("""
                UPDATE "JobRecords" SET "Status" = 'Running', "LockedBy" = @worker, "LeaseExpiresAt" = now() + @lease
                WHERE "Id" IN (
                    SELECT "Id" FROM "JobRecords"
                    WHERE "OrganizationId" = @org AND "JobId" = @job AND "Status" IN ('Pending', 'AwaitingFence') AND "NextAttemptAt" <= now()
                    ORDER BY "NextAttemptAt", "RowNumber"
                    LIMIT @take
                    FOR UPDATE SKIP LOCKED)
                RETURNING "Id", "OrganizationId", "JobId", "OperationId", "NextIntent", "AwaitingFenceSince"
                """, connection, tx);
            claim.Parameters.AddWithValue("worker", incarnation);
            claim.Parameters.AddWithValue("lease", lease);
            claim.Parameters.AddWithValue("org", org);
            claim.Parameters.AddWithValue("job", job);
            claim.Parameters.AddWithValue("take", take);
            await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add(new ClaimedRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), Enum.Parse<RecordIntent>(reader.GetString(4)),
                        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
                }
            }

            await using var start = new NpgsqlCommand("""
                UPDATE "Jobs" SET "Status" = 'Running', "StartedAt" = COALESCE("StartedAt", now()) WHERE "OrganizationId" = @org AND "Id" = @job AND "Status" = 'Pending'
                """, connection, tx);
            start.Parameters.AddWithValue("org", org);
            start.Parameters.AddWithValue("job", job);
            await start.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <summary>Extends this Worker's claims; returns the ids it no longer holds (claim lost: stop processing them).</summary>
    public async Task<IReadOnlySet<Guid>> RenewAsync(Guid incarnation, IReadOnlyCollection<Guid> records, TimeSpan lease, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var renew = new NpgsqlCommand("""
            UPDATE "JobRecords" SET "LeaseExpiresAt" = now() + @lease
            WHERE "Id" = ANY(@ids) AND "LockedBy" = @worker AND "Status" = 'Running' AND "LeaseExpiresAt" > now()
            RETURNING "Id"
            """, connection);
        renew.Parameters.AddWithValue("lease", lease);
        renew.Parameters.AddWithValue("ids", records.ToArray());
        renew.Parameters.AddWithValue("worker", incarnation);
        var held = new HashSet<Guid>();
        await using (var reader = await renew.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                held.Add(reader.GetGuid(0));
            }
        }

        return records.Where(r => !held.Contains(r)).ToHashSet();
    }

    /// <summary>
    /// Returns records whose claim expired (their Worker died or froze) to the queue. Safe: whatever the dead Worker did is
    /// in the attempt journal, and the next attempt's admission is decided from it (an unfenced attempt keeps blocking).
    /// </summary>
    public async Task<int> RecoverExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var recover = new NpgsqlCommand("""
            UPDATE "JobRecords" SET "Status" = 'Pending', "LockedBy" = NULL, "LeaseExpiresAt" = NULL, "NextAttemptAt" = now()
            WHERE "Status" = 'Running' AND "LeaseExpiresAt" < now()
            """, connection);
        return await recover.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Defense in depth: finalizes any running job none of whose records can still run (a finalization missed by a crash
    /// between the last record and its job update). The candidate query is only a hint: each job is then locked in its own
    /// transaction (SKIP LOCKED) and re-checked by <see cref="JobFinalizer.FinalizeSql"/> in a LATER statement, whose fresh
    /// snapshot sees a requeue that committed in between, so a requeued Pending record is never stranded in a finished job.
    /// </summary>
    public async Task<int> FinalizeIdleJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var candidates = new List<(Guid Org, Guid Job)>();
        await using (var select = new NpgsqlCommand("""
            SELECT j."OrganizationId", j."Id" FROM "Jobs" j
            WHERE j."Status" IN ('Pending', 'Running')
              AND NOT EXISTS (SELECT 1 FROM "JobRecords" r WHERE r."OrganizationId" = j."OrganizationId" AND r."JobId" = j."Id"
                              AND r."Status" IN ('Pending', 'Running', 'AwaitingFence'))
            LIMIT 100
            """, connection))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        if (AfterSweepCandidates is { } between)
        {
            await between();
        }

        var finalized = 0;
        foreach (var (org, job) in candidates)
        {
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockJob = new NpgsqlCommand("""
                SELECT 1 FROM "Jobs" WHERE "OrganizationId" = @org AND "Id" = @job FOR UPDATE SKIP LOCKED
                """, connection, tx))
            {
                lockJob.Parameters.AddWithValue("org", org);
                lockJob.Parameters.AddWithValue("job", job);
                if (await lockJob.ExecuteScalarAsync(cancellationToken) is null)
                {
                    continue; // another writer holds it and finalizes it itself if appropriate
                }
            }

            finalized += await JobFinalizer.TryFinalizeAsync(connection, tx, org, job, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        return finalized;
    }

    /// <summary>Test seam: runs after the sweep chose its candidates and before it locks any job (the race window).</summary>
    internal Func<Task>? AfterSweepCandidates { get; set; }

    /// <summary>Which of these jobs have a cancellation request (polled by the heartbeat).</summary>
    public async Task<IReadOnlySet<Guid>> CancelRequestedAsync(IReadOnlyCollection<Guid> jobs, CancellationToken cancellationToken)
    {
        var cancelled = new HashSet<Guid>();
        if (jobs.Count == 0)
        {
            return cancelled;
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""SELECT "Id" FROM "Jobs" WHERE "Id" = ANY(@ids) AND "CancelRequested" """, connection);
        query.Parameters.AddWithValue("ids", jobs.ToArray());
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancelled.Add(reader.GetGuid(0));
        }

        return cancelled;
    }
}
