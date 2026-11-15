using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Workflows;

/// <summary>
/// One version of a workflow's content (docs/PHASE4_PLAN.md §11, §16 D5). Lifecycle rules only; persistence is Phase 5.
/// <list type="bullet">
/// <item>Lifecycle <see cref="WorkflowVersionStatus.Draft"/> → <see cref="WorkflowVersionStatus.Published"/> →
/// <see cref="WorkflowVersionStatus.Archived"/>, with no transition back.</item>
/// <item>The definition, schema version and hash can change only while the version is a draft; a published or archived
/// version is read-only, and edits create a new version.</item>
/// </list>
/// The entity checks the hash <em>format</em>. That the hash belongs to the definition is established by the
/// Application layer (<c>ValidatedWorkflow.Create</c>) before a definition is set, and re-verified on every load.
/// </summary>
public sealed class WorkflowVersion : IOrganizationOwned
{
    private const string HashPrefix = "sha256:";
    private const int HashLength = 71;

    private WorkflowVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid WorkflowId { get; private set; }

    /// <summary>1, 2, 3, … within a workflow.</summary>
    public int VersionNumber { get; private set; }

    /// <summary>The definition format version (<c>schemaVersion</c>).</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>The definition as stored (never rewritten); identity is the hash of its canonical form.</summary>
    public string DefinitionJson { get; private set; } = string.Empty;

    public string DefinitionHash { get; private set; } = string.Empty;

    public WorkflowVersionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public static WorkflowVersion CreateDraft(
        Guid organizationId,
        Guid workflowId,
        int versionNumber,
        int schemaVersion,
        string definitionJson,
        string definitionHash,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (workflowId == Guid.Empty)
        {
            throw new ArgumentException("A workflow is required.", nameof(workflowId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);

        var version = new WorkflowVersion
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            WorkflowId = workflowId,
            VersionNumber = versionNumber,
            Status = WorkflowVersionStatus.Draft,
            CreatedAt = createdAt,
        };
        version.SetContent(schemaVersion, definitionJson, definitionHash);
        return version;
    }

    /// <summary>Replaces the content of a draft. Published and archived versions are immutable.</summary>
    public void UpdateDraft(int schemaVersion, string definitionJson, string definitionHash)
    {
        if (Status != WorkflowVersionStatus.Draft)
        {
            throw new InvalidOperationException($"A {Status} workflow version is immutable; create a new version instead.");
        }

        SetContent(schemaVersion, definitionJson, definitionHash);
    }

    public void Publish(DateTimeOffset publishedAt)
    {
        if (Status != WorkflowVersionStatus.Draft)
        {
            throw new InvalidOperationException($"Only a draft can be published; this version is {Status}.");
        }

        Status = WorkflowVersionStatus.Published;
        PublishedAt = publishedAt;
    }

    public void Archive(DateTimeOffset archivedAt)
    {
        if (Status != WorkflowVersionStatus.Published)
        {
            throw new InvalidOperationException($"Only a published version can be archived; this version is {Status}.");
        }

        Status = WorkflowVersionStatus.Archived;
        ArchivedAt = archivedAt;
    }

    public static bool IsWellFormedHash(string? hash)
    {
        if (hash is not { Length: HashLength } || !hash.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in hash.AsSpan(HashPrefix.Length))
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private void SetContent(int schemaVersion, string definitionJson, string definitionHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionJson);
        if (!IsWellFormedHash(definitionHash))
        {
            throw new ArgumentException("The definition hash must be 'sha256:' followed by 64 lowercase hex digits.", nameof(definitionHash));
        }

        SchemaVersion = schemaVersion;
        DefinitionJson = definitionJson;
        DefinitionHash = definitionHash;
    }
}
