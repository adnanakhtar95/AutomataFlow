using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Domain.Organizations;

namespace AutomataFlow.Application.Workflows.Publication;

/// <summary>How the platform is deployed (mirrors the engine's execution modes; the Api cannot reference Automation).</summary>
public enum PlatformMode
{
    Development,
    SelfHosted,

    /// <summary>Untrusted multi-tenant hosting: attestations are restricted (ADR-045, PHASE4_PLAN risks, limitation 1).</summary>
    Hosted,
}

/// <summary>
/// Claims an author makes that the engine trusts but cannot verify (docs/WORKFLOWS.md §9 limitation 1). A wrong claim
/// can cause duplicate side effects, so who may publish them is restricted.
/// </summary>
public enum Attestation
{
    /// <summary><c>idempotency.guarantee = targetEnforced</c>: the target deduplicates by the operation id.</summary>
    TargetEnforcedIdempotency,

    /// <summary><c>reconciliation.evidence = authoritative</c>: the cited commit-latency contract holds.</summary>
    AuthoritativeEvidence,

    /// <summary><c>reconciliation.matchKey = businessKey</c>: the business key is unique at the target.</summary>
    BusinessKeyMatch,
}

public enum PublicationDecision
{
    Allowed,

    /// <summary>The caller's role may not publish workflows at all.</summary>
    RoleTooLow,

    /// <summary>Hosted mode: attestations may be published only by an Owner.</summary>
    AttestationRequiresOwner,

    /// <summary>The definition carries attestations and the publisher did not explicitly acknowledge them.</summary>
    AttestationNotAcknowledged,
}

public static class PublicationPolicy
{
    /// <summary>Every attestation the definition makes, in a stable order.</summary>
    public static ImmutableArray<Attestation> AttestationsOf(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var found = ImmutableArray.CreateBuilder<Attestation>();
        if (definition.AllSteps().Any(s => s.Idempotency is { Guarantee: IdempotencyGuarantee.TargetEnforced }))
        {
            found.Add(Attestation.TargetEnforcedIdempotency);
        }

        if (definition.Reconciliation is { Evidence: EvidenceLevel.Authoritative })
        {
            found.Add(Attestation.AuthoritativeEvidence);
        }

        if (definition.Reconciliation is { MatchKey: MatchKeyKind.BusinessKey })
        {
            found.Add(Attestation.BusinessKeyMatch);
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Admins and Owners publish. A definition with attestations needs an explicit acknowledgement by the publisher (recorded
    /// with the version), and in <see cref="PlatformMode.Hosted"/> only an Owner may publish it.
    /// </summary>
    public static PublicationDecision Decide(PlatformMode mode, OrganizationRole role, ImmutableArray<Attestation> attestations, bool acknowledged)
    {
        if (role < OrganizationRole.Admin)
        {
            return PublicationDecision.RoleTooLow;
        }

        if (attestations.IsDefaultOrEmpty)
        {
            return PublicationDecision.Allowed;
        }

        if (mode == PlatformMode.Hosted && role < OrganizationRole.Owner)
        {
            return PublicationDecision.AttestationRequiresOwner;
        }

        return acknowledged ? PublicationDecision.Allowed : PublicationDecision.AttestationNotAcknowledged;
    }
}
