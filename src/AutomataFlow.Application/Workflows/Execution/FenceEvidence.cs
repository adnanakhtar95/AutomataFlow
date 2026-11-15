using System.Collections.Immutable;

namespace AutomataFlow.Application.Workflows.Execution;

/// <summary>The closed list of confirmation methods (§7.5.3, §7.5.6). Nothing else can be expressed.</summary>
public enum FenceMethod
{
    /// <summary>Supervisor: the unit reported empty (no live task) <b>and</b> the socket drain succeeded.</summary>
    SupervisorUnitEmptyAndDrained,

    /// <summary>Supervisor: the host's boot id differs from the incarnation's (no process or kernel socket survives).</summary>
    SupervisorHostRebooted,

    /// <summary>Operator: the VM or machine is confirmed off or deleted in the hypervisor or cloud console.</summary>
    HostPoweredOffOrDestroyed,

    /// <summary>Operator: the boot id has changed, checked by the operator.</summary>
    HostRebootedVerified,

    /// <summary>Operator: the unit is verified empty AND a supervisor drain report shows every ledgered socket safe.</summary>
    DrainVerified,
}

public enum ActorRole
{
    /// <summary>A tenant (organization) user: can never confirm fences.</summary>
    TenantUser,

    /// <summary>Platform infrastructure operator.</summary>
    PlatformOperator,
}

/// <summary>The supervisor's socket drain report (§7.6.5): complete enumeration, every ledgered upstream socket safe.</summary>
public sealed record DrainReport(bool EnumerationComplete, int LedgeredConnections, int SafeConnections, bool OwnershipAmbiguous)
{
    /// <summary>True only for a complete, unambiguous drain in which every ledgered connection reached a safe state.</summary>
    public bool ProvesNoFurtherTransmission => EnumerationComplete && !OwnershipAmbiguous && SafeConnections == LedgeredConnections;
}

/// <summary>Who confirmed a human fence, with what, and the operator's explicit attestation (§7.5.6).</summary>
public sealed record HumanConfirmation(string ActorUserId, ActorRole Role, string EvidenceReference, bool AttestedPersonally);

/// <summary>
/// External fence evidence for one Worker incarnation (§7.5). <see cref="ObservedAt"/> is the supervisor's observation
/// time: a diagnostic only, never a fence time (the journal sets <c>FencedAt</c> at commit).
/// </summary>
public sealed record FenceEvidence(
    FenceSource Source,
    WorkerIncarnationId Incarnation,
    ContainmentUnit Unit,
    FenceMethod Method,
    DateTimeOffset ObservedAt,
    DrainReport? Drain,
    string? CurrentBootId,
    HumanConfirmation? Human);

/// <summary>
/// The journal's validation of fence evidence (§7.5.3, §7.5.6, §7.5.7; F9, F14, E40, E41). Pure: the journal supplies the
/// incarnation recorded for the attempts. Fails closed: anything not positively proven is rejected.
/// </summary>
public static class FenceEvidenceValidator
{
    public const int MinEvidenceReferenceChars = 10;
    public const int MaxEvidenceReferenceChars = 1_000;

    private static readonly ImmutableHashSet<FenceMethod> HumanMethods =
        [FenceMethod.HostPoweredOffOrDestroyed, FenceMethod.HostRebootedVerified, FenceMethod.DrainVerified];

    /// <summary>Null when the evidence is acceptable; otherwise the reason it is rejected.</summary>
    public static string? Validate(FenceEvidence evidence, WorkerIncarnation? recorded)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (recorded is null || recorded.Id != evidence.Incarnation)
        {
            return "unknown-incarnation";
        }

        if (recorded.Unit != evidence.Unit)
        {
            return "unit-mismatch";
        }

        return evidence.Source switch
        {
            FenceSource.ProcessDeathConfirmed => ValidateProcessDeath(evidence, recorded),
            FenceSource.HumanConfirmed => ValidateHuman(evidence, recorded),

            // Phase 7 sources need an equivalent drain proof that is not specified yet; the executor and abandonment
            // sources are established by completion / abandonment, never by an external confirmation.
            _ => "source-not-accepted",
        };
    }

    private static string? ValidateProcessDeath(FenceEvidence evidence, WorkerIncarnation recorded) => evidence.Method switch
    {
        FenceMethod.SupervisorUnitEmptyAndDrained => evidence.Drain is { ProvesNoFurtherTransmission: true } ? null : "drain-not-proven",
        FenceMethod.SupervisorHostRebooted => BootChanged(evidence, recorded) ? null : "boot-id-unchanged",
        _ => "method-not-allowed",
    };

    private static string? ValidateHuman(FenceEvidence evidence, WorkerIncarnation recorded)
    {
        if (evidence.Human is not { } human || string.IsNullOrWhiteSpace(human.ActorUserId))
        {
            return "actor-required";
        }

        if (human.Role != ActorRole.PlatformOperator)
        {
            return "actor-not-platform-operator";
        }

        if (!human.AttestedPersonally)
        {
            return "attestation-required";
        }

        if (human.EvidenceReference is null || human.EvidenceReference.Trim().Length < MinEvidenceReferenceChars || human.EvidenceReference.Length > MaxEvidenceReferenceChars)
        {
            return "evidence-reference-invalid";
        }

        if (!HumanMethods.Contains(evidence.Method))
        {
            return "method-not-allowed";
        }

        return evidence.Method switch
        {
            FenceMethod.DrainVerified => evidence.Drain is { ProvesNoFurtherTransmission: true } ? null : "drain-not-proven",
            FenceMethod.HostRebootedVerified => BootChanged(evidence, recorded) ? null : "boot-id-unchanged",
            _ => null,
        };
    }

    private static bool BootChanged(FenceEvidence evidence, WorkerIncarnation recorded) =>
        !string.IsNullOrWhiteSpace(evidence.CurrentBootId) && !string.Equals(evidence.CurrentBootId, recorded.BootId, StringComparison.Ordinal);
}
