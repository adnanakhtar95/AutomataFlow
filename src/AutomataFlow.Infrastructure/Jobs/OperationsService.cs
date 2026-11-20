using Microsoft.Extensions.Options;
using Npgsql;

namespace AutomataFlow.Infrastructure.Jobs;

/// <param name="ReadyRecords">Pending records due now (waiting for a Worker slot).</param>
/// <param name="ScheduledRetries">Pending records whose next attempt is in the future (retry backoff).</param>
/// <param name="OldestReadyAgeSeconds">How long the oldest due record has been waiting; null when none.</param>
public sealed record QueueView(int ReadyRecords, int ScheduledRetries, int RunningRecords, int AwaitingFence, int NeedsReview, int ActiveJobs, double? OldestReadyAgeSeconds);

public sealed record WorkerView(Guid IncarnationId, string HostId, DateTimeOffset RegisteredAt, DateTimeOffset? LastHeartbeatAt, double? HeartbeatAgeSeconds, bool Fenced);

/// <param name="AttemptsByStatus">Attempts finished in the window, by status.</param>
/// <param name="AverageAttemptSeconds">Mean duration of the attempts finished in the window.</param>
public sealed record ExecutionMetrics(int WindowHours, IReadOnlyDictionary<string, int> AttemptsByStatus, double? AverageAttemptSeconds, double? P95AttemptSeconds, int RecordsCompleted);

public sealed record StorageView(long ArtifactBytes, int ArtifactFiles, long? FreeBytes, long? TotalBytes);

public sealed record OperationsView(DateTimeOffset At, QueueView Queue, IReadOnlyList<WorkerView> Workers, ExecutionMetrics Metrics, StorageView Storage);

/// <summary>
/// Platform-wide operational state for platform operators (Phase 10): queue depth, Worker liveness, execution metrics and
/// artifact storage. Reviewed cross-organization read (like the Worker's claim queries): explicit SQL that returns
/// COUNTS and timings only, never record contents, names or ids of customer data. Platform-operator policy only.
/// </summary>
public sealed class OperationsService(NpgsqlDataSource source, IOptions<ArtifactStorageOptions> artifacts, TimeProvider clock)
{
    public const int MetricsWindowHours = 24;

    public async Task<OperationsView> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return new OperationsView(clock.GetUtcNow(), await QueueAsync(connection, cancellationToken), await WorkersAsync(connection, cancellationToken),
            await MetricsAsync(connection, cancellationToken), Storage());
    }

    private static async Task<QueueView> QueueAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
              count(*) FILTER (WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now()),
              count(*) FILTER (WHERE "Status" = 'Pending' AND "NextAttemptAt" > now()),
              count(*) FILTER (WHERE "Status" = 'Running'),
              count(*) FILTER (WHERE "Status" = 'AwaitingFence'),
              count(*) FILTER (WHERE "Status" = 'NeedsReview'),
              (SELECT count(*) FROM "Jobs" WHERE "Status" IN ('Pending', 'Running')),
              EXTRACT(EPOCH FROM now() - min("NextAttemptAt") FILTER (WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now()))
            FROM "JobRecords"
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new QueueView((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2), (int)reader.GetInt64(3), (int)reader.GetInt64(4), (int)reader.GetInt64(5),
            reader.IsDBNull(6) ? null : (double)reader.GetDecimal(6));
    }

    private static async Task<List<WorkerView>> WorkersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT i."Id", i."HostId", i."RegisteredAt", i."LastHeartbeatAt", EXTRACT(EPOCH FROM now() - i."LastHeartbeatAt"),
                   EXISTS (SELECT 1 FROM "FenceConfirmations" f WHERE f."IncarnationId" = i."Id" AND f."Accepted")
            FROM "WorkerIncarnations" i
            WHERE i."RegisteredAt" > now() - interval '7 days'
            ORDER BY i."RegisteredAt" DESC
            LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var workers = new List<WorkerView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            workers.Add(new WorkerView(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : (double)reader.GetDecimal(4), reader.GetBoolean(5)));
        }

        return workers;
    }

    private static async Task<ExecutionMetrics> MetricsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand($"""
            SELECT "Status", count(*) FROM "JobAttempts"
            WHERE "FinishedAt" > now() - interval '{MetricsWindowHours} hours' GROUP BY "Status"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                byStatus[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }

        await using var timing = new NpgsqlCommand($"""
            SELECT avg(EXTRACT(EPOCH FROM "FinishedAt" - "StartedAt")),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM "FinishedAt" - "StartedAt")),
                   (SELECT count(*) FROM "JobRecords" WHERE "CompletedAt" > now() - interval '{MetricsWindowHours} hours')
            FROM "JobAttempts" WHERE "FinishedAt" > now() - interval '{MetricsWindowHours} hours'
            """, connection);
        await using var timings = await timing.ExecuteReaderAsync(cancellationToken);
        await timings.ReadAsync(cancellationToken);
        return new ExecutionMetrics(MetricsWindowHours, byStatus, timings.IsDBNull(0) ? null : (double)timings.GetDecimal(0), timings.IsDBNull(1) ? null : timings.GetDouble(1),
            (int)timings.GetInt64(2));
    }

    private StorageView Storage()
    {
        var root = artifacts.Value.RootDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new StorageView(0, 0, null, null);
        }

        long bytes = 0;
        var files = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            bytes += file.Length;
            files++;
        }

        try
        {
            var drive = new DriveInfo(Path.GetFullPath(root));
            return new StorageView(bytes, files, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new StorageView(bytes, files, null, null);
        }
    }
}
