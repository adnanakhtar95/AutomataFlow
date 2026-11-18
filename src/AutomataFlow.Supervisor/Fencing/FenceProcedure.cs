using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Fencing;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Logging;
using AppDrainReport = AutomataFlow.Application.Workflows.Execution.DrainReport;

namespace AutomataFlow.Supervisor.Fencing;

/// <summary>An independent scan of the container's processes after the kill (ADR-075).</summary>
/// <param name="Complete">False when the process table could not be read completely (fail closed).</param>
/// <param name="UnitTasks">Tasks that belong to the unit (Worker uid, unit user or PID namespace, or the unit wrapper).</param>
/// <param name="ForeignTasks">Tasks that are neither the unit nor PID 1 nor the supervisor and its ancestors.</param>
public sealed record UnitScan(bool Complete, IReadOnlyList<int> UnitTasks, IReadOnlyList<int> ForeignTasks, string? Failure = null);

/// <summary>Control of the containment unit. The Linux implementation acts on real processes; tests script it.</summary>
public interface IUnitControl
{
    /// <summary>SIGKILL to the unit wrapper and every unit task currently visible. A kill request is never proof.</summary>
    void KillAll();

    /// <summary>SIGTERM to the unit's init (graceful stop: the Worker cancels its attempts with fenced completions).</summary>
    void Terminate();

    /// <summary>True once the unit wrapper has exited and was reaped (the kernel's ordering guarantee, step 3).</summary>
    bool TryReapWrapper();

    UnitScan Scan();
}

public sealed record SupervisorPolicy(TimeSpan FenceConfirmTimeout, TimeSpan PollInterval, DrainPolicy Drain)
{
    public static SupervisorPolicy Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100), DrainPolicy.Default);
}

public enum FenceResultKind
{
    /// <summary>The journal accepted a validated ProcessDeathConfirmed fence.</summary>
    Fenced,

    /// <summary>The unit is proven gone and no incarnation was ever registered: nothing could have executed.</summary>
    NothingToFence,

    /// <summary>No fence: proof missing, drain not complete, or the journal refused. Attempts stay AwaitingFence.</summary>
    NotFenced,
}

public sealed record FenceOutcome(FenceResultKind Kind, string? Reason, Automation.Network.Fencing.DrainReport? Drain = null, FenceConfirmationResult? Confirmation = null);

/// <summary>
/// The process-death fence (docs/PHASE4_PLAN.md §7.5.3 with ADR-075): kill the whole unit, PROVE it is empty (wrapper
/// reaped AND an independent process scan finds no unit task and no foreign process), drain every upstream socket, and
/// only then report <c>ProcessDeathConfirmed</c>. Every missing proof returns <see cref="FenceResultKind.NotFenced"/>; the
/// journal then keeps the attempts AwaitingFence (→ NeedsReview after FenceWaitSeconds). Never a retry, never a guess.
/// </summary>
public sealed class FenceProcedure(
    IAttemptJournal journal,
    IUnitControl unit,
    Func<string, LedgerReadResult> readLedger,
    ISocketTableReader table,
    ISocketDestroyer destroyer,
    SupervisorPolicy policy,
    TimeProvider clock,
    ILogger logger)
{
    /// <param name="incarnation">The incarnation registered for this unit, or null if the Worker never registered.</param>
    /// <param name="ledgerDirectory">The unit's ledger directory; the incarnation's ledger must be its only file.</param>
    public async Task<FenceOutcome> RunAsync(WorkerIncarnation? incarnation, string ledgerDirectory, CancellationToken cancellationToken)
    {
        if (incarnation is not null)
        {
            try
            {
                await journal.RecordKillRequestAsync(incarnation.Id, cancellationToken); // diagnostic only, fences nothing
            }
            catch (AttemptJournalException)
            {
                logger.LogWarning("Kill request could not be recorded {IncarnationId}", incarnation.Id.Value);
            }
        }

        var proof = await ProveUnitEmptyAsync(cancellationToken);
        if (proof is not null)
        {
            return new FenceOutcome(FenceResultKind.NotFenced, proof);
        }

        if (incarnation is null)
        {
            return new FenceOutcome(FenceResultKind.NothingToFence, "no-incarnation-registered");
        }

        var ledgerProblem = LedgerProblem(incarnation.ConnectionLedger, ledgerDirectory);
        if (ledgerProblem is not null)
        {
            return new FenceOutcome(FenceResultKind.NotFenced, ledgerProblem);
        }

        var ledger = readLedger(incarnation.ConnectionLedger);
        var drain = await new ConnectionDrain(table, destroyer, policy.Drain, d => Task.Delay(d, clock, cancellationToken))
            .RunAsync(DrainPlatform.LinuxNamespace, ledger);
        if (!drain.IsDrained)
        {
            return new FenceOutcome(FenceResultKind.NotFenced, "drain:" + drain.Reason, drain);
        }

        // Only a complete, unambiguous drain of every ledgered connection is ever reported (F9).
        var evidence = new FenceEvidence(FenceSource.ProcessDeathConfirmed, incarnation.Id, incarnation.Unit, FenceMethod.SupervisorUnitEmptyAndDrained,
            clock.GetUtcNow(), new AppDrainReport(EnumerationComplete: true, drain.LedgerEntries, drain.LedgerEntries, OwnershipAmbiguous: false), null, null);
        var confirmation = await journal.ConfirmFenceAsync(evidence, cancellationToken);
        return confirmation.Accepted
            ? new FenceOutcome(FenceResultKind.Fenced, null, drain, confirmation)
            : new FenceOutcome(FenceResultKind.NotFenced, "journal-rejected:" + confirmation.RejectionReason, drain, confirmation);
    }

    /// <summary>Null when the unit is PROVEN empty; otherwise why it is not.</summary>
    private async Task<string?> ProveUnitEmptyAsync(CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + policy.FenceConfirmTimeout;
        string reason = "unit-not-empty";
        var reaped = false;
        while (true)
        {
            unit.KillAll();
            reaped |= unit.TryReapWrapper();
            var scan = unit.Scan();
            if (!scan.Complete)
            {
                reason = "process-scan-incomplete:" + scan.Failure;
            }
            else if (!reaped)
            {
                reason = "wrapper-not-reaped";
            }
            else if (scan.UnitTasks.Count > 0)
            {
                reason = "unit-not-empty";
            }
            else if (scan.ForeignTasks.Count > 0)
            {
                // A live process outside the unit could hold sockets in this namespace: wait for it to go, never fence past it.
                reason = "foreign-process-in-container";
            }
            else
            {
                return null;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return reason;
            }

            await Task.Delay(policy.PollInterval, clock, cancellationToken);
        }
    }

    private static string? LedgerProblem(string ledgerPath, string ledgerDirectory)
    {
        if (string.IsNullOrWhiteSpace(ledgerPath) || string.IsNullOrWhiteSpace(ledgerDirectory))
        {
            return "ledger-not-recorded";
        }

        var file = Path.GetFullPath(ledgerPath);
        var directory = Path.GetFullPath(ledgerDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(file), directory, StringComparison.Ordinal))
        {
            return "ledger-outside-unit-directory";
        }

        try
        {
            var others = Directory.EnumerateFileSystemEntries(directory).Where(e => !string.Equals(Path.GetFullPath(e), file, StringComparison.Ordinal)).ToList();
            return others.Count == 0 ? null : "unexpected-ledger-files";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "ledger-directory-unreadable";
        }
    }
}
