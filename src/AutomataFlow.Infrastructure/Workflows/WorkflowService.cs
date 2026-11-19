using AutomataFlow.Application.Abstractions;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Publication;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using DomainInput = AutomataFlow.Domain.Workflows.WorkflowInput;

namespace AutomataFlow.Infrastructure.Workflows;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public PlatformMode Mode { get; set; } = PlatformMode.SelfHosted;
}

/// <param name="PublishedVersionId">The newest published version (the one new jobs normally use), or null.</param>
public sealed record WorkflowSummary(Guid Id, string Name, string? Description, int LatestVersion, DateTimeOffset CreatedAt, Guid? PublishedVersionId, int? PublishedVersionNumber);

/// <summary>Result of validating a definition without storing it.</summary>
public sealed record DefinitionValidation(bool Valid, string? DefinitionHash, IReadOnlyList<ServiceError> Errors, IReadOnlyList<ServiceError> Warnings, IReadOnlyList<string> Attestations);

public sealed record VersionSummary(Guid Id, int VersionNumber, WorkflowVersionStatus Status, string DefinitionHash, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, string? AcknowledgedAttestations);

public sealed record VersionDetail(Guid WorkflowId, VersionSummary Version, string DefinitionJson, IReadOnlyList<ServiceError> Warnings);

public sealed record WorkflowDetail(WorkflowSummary Workflow, IReadOnlyList<VersionSummary> Versions);

/// <summary>
/// Workflow use cases (docs/ARCHITECTURE.md §7.1 layer 2). The organization always comes from <see cref="ICurrentUser"/>
/// and is part of every query; a foreign id is indistinguishable from a missing one (NotFound). Every stored definition
/// passed full validation, its hash is stored with it, and it is re-verified whenever it is loaded (ADR-061).
/// </summary>
public sealed class WorkflowService(AutomataFlowDbContext db, ICurrentUser user, IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<WorkflowService> logger)
{
    public const int MaxSearchLength = 64;

    /// <summary>Workflows of the caller's organization by name; <paramref name="search"/> matches name or description (case-insensitive, literal).</summary>
    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(string? search, int skip, int take, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var query = db.Workflows.AsNoTracking().Where(w => w.OrganizationId == org);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search.Trim()[..Math.Min(search.Trim().Length, MaxSearchLength)]) + "%";
            query = query.Where(w => EF.Functions.ILike(w.Name, pattern, "\\") || (w.Description != null && EF.Functions.ILike(w.Description, pattern, "\\")));
        }

        var rows = await query.OrderBy(w => w.Name).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
            .Select(w => new
            {
                Workflow = w,
                Published = db.WorkflowVersions.Where(v => v.OrganizationId == org && v.WorkflowId == w.Id && v.Status == WorkflowVersionStatus.Published)
                    .OrderByDescending(v => v.VersionNumber).Select(v => new { v.Id, v.VersionNumber }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => Summary(r.Workflow, r.Published?.Id, r.Published?.VersionNumber))];
    }

    /// <summary>Full validation of a definition (the same rules as saving a draft) without storing anything.</summary>
    public static DefinitionValidation ValidateDefinition(string definitionJson)
    {
        var result = ValidatedWorkflow.CreateForPublication(definitionJson ?? string.Empty);
        var errors = result.Errors.Select(e => new ServiceError(e.Path, e.Code, e.Message)).ToList();
        if (result.Workflow is not { } validated)
        {
            return new DefinitionValidation(false, null, errors, [], []);
        }

        return new DefinitionValidation(true, validated.DefinitionHash, errors, Warnings(validated), [.. PublicationPolicy.AttestationsOf(validated.Definition).Select(a => a.ToString())]);
    }

    public async Task<ServiceResult<WorkflowDetail>> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.OrganizationId == org && w.Id == workflowId, cancellationToken);
        if (workflow is null)
        {
            return ServiceResult<WorkflowDetail>.NotFound();
        }

        var versions = await db.WorkflowVersions.AsNoTracking().Where(v => v.OrganizationId == org && v.WorkflowId == workflowId)
            .OrderBy(v => v.VersionNumber).ToListAsync(cancellationToken);
        var published = versions.Where(v => v.Status == WorkflowVersionStatus.Published).MaxBy(v => v.VersionNumber);
        return ServiceResult<WorkflowDetail>.Ok(new WorkflowDetail(Summary(workflow, published?.Id, published?.VersionNumber), [.. versions.Select(Summary)]));
    }

    public async Task<ServiceResult<VersionDetail>> GetVersionAsync(Guid workflowId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(workflowId, versionId, tracked: false, cancellationToken);
        return version is null ? ServiceResult<VersionDetail>.NotFound() : ServiceResult<VersionDetail>.Ok(new VersionDetail(workflowId, Summary(version), version.DefinitionJson, []));
    }

    /// <summary>Creates a workflow with its first draft. The definition must validate completely.</summary>
    public async Task<ServiceResult<VersionDetail>> CreateAsync(string name, string? description, string definitionJson, CancellationToken cancellationToken)
    {
        var validated = Validate(definitionJson, out var errors);
        if (validated is null)
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Invalid, [.. errors]);
        }

        Workflow workflow;
        try
        {
            workflow = Workflow.Create(user.OrganizationId, name, description, clock.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Invalid, new ServiceError("name", "invalid", "The workflow name is invalid."));
        }

        var version = NewDraft(workflow, validated, definitionJson);
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        if (!await SaveAsync(cancellationToken))
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Conflict, new ServiceError("name", "duplicate", "A workflow with this name already exists."));
        }

        return ServiceResult<VersionDetail>.Ok(new VersionDetail(workflow.Id, Summary(version), version.DefinitionJson, Warnings(validated)));
    }

    /// <summary>Adds a new draft version to an existing workflow (published versions are never edited).</summary>
    public async Task<ServiceResult<VersionDetail>> AddVersionAsync(Guid workflowId, string definitionJson, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.OrganizationId == org && w.Id == workflowId, cancellationToken);
        if (workflow is null)
        {
            return ServiceResult<VersionDetail>.NotFound();
        }

        var validated = Validate(definitionJson, out var errors);
        if (validated is null)
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Invalid, [.. errors]);
        }

        var version = NewDraft(workflow, validated, definitionJson);
        db.WorkflowVersions.Add(version);
        if (!await SaveAsync(cancellationToken))
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Conflict, new ServiceError(null, "concurrent-change", "The workflow changed concurrently; retry."));
        }

        return ServiceResult<VersionDetail>.Ok(new VersionDetail(workflowId, Summary(version), version.DefinitionJson, Warnings(validated)));
    }

    public async Task<ServiceResult<VersionDetail>> UpdateDraftAsync(Guid workflowId, Guid versionId, string definitionJson, CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(workflowId, versionId, tracked: true, cancellationToken);
        if (version is null)
        {
            return ServiceResult<VersionDetail>.NotFound();
        }

        if (version.Status != WorkflowVersionStatus.Draft)
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Conflict, new ServiceError(null, "version-immutable", $"A {version.Status} version is immutable; add a new version instead."));
        }

        var validated = Validate(definitionJson, out var errors);
        if (validated is null)
        {
            return ServiceResult<VersionDetail>.Fail(ServiceStatus.Invalid, [.. errors]);
        }

        version.UpdateDraft(validated.Definition.SchemaVersion, definitionJson, validated.DefinitionHash);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<VersionDetail>.Ok(new VersionDetail(workflowId, Summary(version), version.DefinitionJson, Warnings(validated)));
    }

    /// <summary>Publishes a draft after re-verifying its stored hash, applying the publication policy (role, hosted-mode attestations).</summary>
    public async Task<ServiceResult<VersionSummary>> PublishAsync(Guid workflowId, Guid versionId, bool acknowledgeAttestations, CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(workflowId, versionId, tracked: true, cancellationToken);
        if (version is null)
        {
            return ServiceResult<VersionSummary>.NotFound();
        }

        if (version.Status != WorkflowVersionStatus.Draft)
        {
            return ServiceResult<VersionSummary>.Fail(ServiceStatus.Conflict, new ServiceError(null, "not-a-draft", "Only a draft can be published."));
        }

        var loaded = ValidatedWorkflow.Load(version.DefinitionJson, version.DefinitionHash);
        if (loaded.Workflow is not { } validated)
        {
            logger.LogWarning("Stored draft failed hash verification {WorkflowVersionId}", version.Id);
            return ServiceResult<VersionSummary>.Fail(ServiceStatus.Conflict, new ServiceError(null, EngineErrorCodes.WorkflowTampered, "The stored definition does not match its hash."));
        }

        var attestations = PublicationPolicy.AttestationsOf(validated.Definition);
        switch (PublicationPolicy.Decide(platform.Value.Mode, user.Role, attestations, acknowledgeAttestations))
        {
            case PublicationDecision.RoleTooLow:
                return ServiceResult<VersionSummary>.Fail(ServiceStatus.Forbidden, new ServiceError(null, "role-too-low", "Publishing requires the Admin or Owner role."));
            case PublicationDecision.AttestationRequiresOwner:
                return ServiceResult<VersionSummary>.Fail(ServiceStatus.Forbidden, new ServiceError(null, "attestation-requires-owner", "In hosted mode, only an Owner may publish idempotency or evidence attestations."));
            case PublicationDecision.AttestationNotAcknowledged:
                return ServiceResult<VersionSummary>.Fail(ServiceStatus.Invalid, new ServiceError("acknowledgeAttestations", "attestation-not-acknowledged",
                    "This definition makes claims the engine cannot verify (" + string.Join(", ", attestations) + "); acknowledge them explicitly to publish."));
        }

        var now = clock.GetUtcNow();
        version.Publish(now, user.UserId, attestations.IsEmpty ? null : string.Join(",", attestations));
        var position = 0;
        foreach (var input in validated.Definition.Inputs)
        {
            db.WorkflowInputs.Add(DomainInput.Create(version.OrganizationId, version.Id, input.Name, WorkflowJsonNames.InputTypes.First(p => p.Value == input.Type).Key, input.Required, input.Sensitive, position++));
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Workflow version published {WorkflowId} {WorkflowVersionId} {DefinitionHash} {Attestations}", workflowId, version.Id, version.DefinitionHash, version.AcknowledgedAttestations);
        return ServiceResult<VersionSummary>.Ok(Summary(version));
    }

    public async Task<ServiceResult<VersionSummary>> ArchiveAsync(Guid workflowId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(workflowId, versionId, tracked: true, cancellationToken);
        if (version is null)
        {
            return ServiceResult<VersionSummary>.NotFound();
        }

        if (version.Status != WorkflowVersionStatus.Published)
        {
            return ServiceResult<VersionSummary>.Fail(ServiceStatus.Conflict, new ServiceError(null, "not-published", "Only a published version can be archived."));
        }

        version.Archive(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<VersionSummary>.Ok(Summary(version));
    }

    private async Task<WorkflowVersion?> FindVersionAsync(Guid workflowId, Guid versionId, bool tracked, CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var query = db.WorkflowVersions.Where(v => v.OrganizationId == org && v.WorkflowId == workflowId && v.Id == versionId);
        return await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken);
    }

    private WorkflowVersion NewDraft(Workflow workflow, ValidatedWorkflow validated, string definitionJson) =>
        WorkflowVersion.CreateDraft(workflow.OrganizationId, workflow.Id, workflow.NextVersionNumber(), validated.Definition.SchemaVersion, definitionJson, validated.DefinitionHash, clock.GetUtcNow());

    private static ValidatedWorkflow? Validate(string definitionJson, out IReadOnlyList<ServiceError> errors)
    {
        var result = ValidatedWorkflow.CreateForPublication(definitionJson ?? string.Empty);
        errors = [.. result.Errors.Select(e => new ServiceError(e.Path, e.Code, e.Message))];
        return result.Workflow;
    }

    private static IReadOnlyList<ServiceError> Warnings(ValidatedWorkflow validated) => [.. validated.Warnings.Select(w => new ServiceError(w.Path, w.Code, w.Message))];

    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private static WorkflowSummary Summary(Workflow w, Guid? publishedId, int? publishedNumber) => new(w.Id, w.Name, w.Description, w.LatestVersion, w.CreatedAt, publishedId, publishedNumber);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static VersionSummary Summary(WorkflowVersion v) => new(v.Id, v.VersionNumber, v.Status, v.DefinitionHash, v.CreatedAt, v.PublishedAt, v.AcknowledgedAttestations);
}
