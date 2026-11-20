using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AutomataFlow.Infrastructure.Jobs;

public sealed record PurgeResult(int Rows, int FilesDeleted, long BytesFreed, int MissingFiles);

/// <summary>
/// Artifact retention (Phase 10): deletes screenshots and downloads older than a cut-off, across all workspaces, as an
/// explicit operator command (<c>AutomataFlow.Api purge-artifacts</c>, scheduled by the operator; never automatic).
/// Reviewed cross-organization write with explicit SQL: it removes artifact rows and their files only; records, results
/// and logs are kept. Rows are deleted first, then files, so a crash never leaves a row pointing at a deleted file
/// (an orphaned file is harmless and is retried on the next run only if it is still referenced, which it is not).
/// </summary>
public sealed class ArtifactRetention(NpgsqlDataSource source, FileSystemArtifactStore store, IOptions<ArtifactStorageOptions> options, TimeProvider clock, ILogger<ArtifactRetention> logger)
{
    public const int MinimumDays = 1;

    public async Task<PurgeResult> PurgeAsync(int olderThanDays, CancellationToken cancellationToken)
    {
        if (olderThanDays < MinimumDays)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), "Artifacts are kept for at least one day.");
        }

        var cutoff = clock.GetUtcNow().AddDays(-olderThanDays);
        var keys = new List<(string Key, long Size)>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM "ExecutionArtifacts" WHERE "CreatedAt" < @cutoff RETURNING "StorageKey", "SizeBytes"
            """, connection))
        {
            delete.Parameters.AddWithValue("cutoff", cutoff);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                keys.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var (deleted, missing, freed) = (0, 0, 0L);
        foreach (var (key, size) in keys)
        {
            if (store.Delete(key))
            {
                deleted++;
                freed += size;
            }
            else
            {
                missing++;
            }
        }

        logger.LogInformation("Artifact retention purged {Rows} rows, {Files} files, {Bytes} bytes older than {Days} days ({Missing} files already missing) under {Root}",
            keys.Count, deleted, freed, olderThanDays, missing, options.Value.RootDirectory);
        return new PurgeResult(keys.Count, deleted, freed, missing);
    }
}
