using AutomataFlow.Application.Workflows.Execution;
using Npgsql;

namespace AutomataFlow.Infrastructure.Persistence.Journal;

/// <summary>A Worker incarnation as a platform operator sees it: identity and liveness, never customer data.</summary>
public sealed record IncarnationView(Guid Id, string HostId, string BootId, int ProcessId, string UnitKind, string UnitName,
    DateTimeOffset RegisteredAt, DateTimeOffset LastHeartbeatAt, int UnfencedAttempts);

/// <param name="Drain">Required for <see cref="FenceMethod.DrainVerified"/>: the supervisor tool's drain report.</param>
public sealed record HumanFenceRequest(Guid IncarnationId, FenceMethod Method, string EvidenceReference, bool AttestedPersonally, string? CurrentBootId, DrainReport? Drain);

/// <summary>
/// Platform-operator fence administration (§7.5.6, ADR-073). Workers are platform infrastructure, so these reads span
/// organizations by design; they expose incarnation identity and counts only. A confirmation is validated and audited by
/// the journal itself (<see cref="IAttemptJournal.ConfirmFenceAsync"/>); this service only assembles the evidence with the
/// unit recorded for the incarnation and the authenticated operator as actor.
/// </summary>
public sealed class FenceAdministration(NpgsqlDataSource source, IAttemptJournal journal, TimeProvider clock)
{
    public async Task<IReadOnlyList<IncarnationView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT w."Id", w."HostId", w."BootId", w."ProcessId", w."UnitKind", w."UnitName", w."RegisteredAt", w."LastHeartbeatAt",
                   (SELECT count(*) FROM "AttemptJournal" a WHERE a."IncarnationId" = w."Id" AND a."Blocking" AND a."State" IN ('Completed', 'Abandoned'))
            FROM "WorkerIncarnations" w ORDER BY w."LastHeartbeatAt" DESC LIMIT 500
            """, connection);
        var result = new List<IncarnationView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new IncarnationView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7), (int)reader.GetInt64(8)));
        }

        return result;
    }

    /// <summary>Records an operator's confirmation. Returns null when the incarnation is unknown.</summary>
    public async Task<FenceConfirmationResult?> ConfirmHumanAsync(HumanFenceRequest request, string actorUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unit = (await ListAsync(cancellationToken)).FirstOrDefault(i => i.Id == request.IncarnationId);
        if (unit is null)
        {
            return null;
        }

        var evidence = new FenceEvidence(
            FenceSource.HumanConfirmed,
            new WorkerIncarnationId(request.IncarnationId),
            new ContainmentUnit(Enum.Parse<ContainmentKind>(unit.UnitKind), unit.UnitName),
            request.Method,
            clock.GetUtcNow(),
            request.Drain,
            request.CurrentBootId,
            new HumanConfirmation(actorUserId, ActorRole.PlatformOperator, request.EvidenceReference, request.AttestedPersonally));
        return await journal.ConfirmFenceAsync(evidence, cancellationToken);
    }
}
