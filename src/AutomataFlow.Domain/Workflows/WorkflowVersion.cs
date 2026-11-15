using System.Text;
using System.Text.Json;
using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Workflows;

/// <summary>
/// One version of a workflow's content (docs/PHASE4_PLAN.md §11, §16 D5). Lifecycle rules only; persistence is Phase 5.
/// <list type="bullet">
/// <item>Lifecycle <see cref="WorkflowVersionStatus.Draft"/> → <see cref="WorkflowVersionStatus.Published"/> →
/// <see cref="WorkflowVersionStatus.Archived"/>, with no transition back and timestamps that never go backwards.</item>
/// <item>The definition, schema version and hash can change only while the version is a draft; a published or archived
/// version is read-only, and edits create a new version.</item>
/// <item>The declared <see cref="SchemaVersion"/> must equal the definition's top-level <c>schemaVersion</c>.</item>
/// </list>
/// <para>
/// A well-formed hash is <b>not</b> proof that the hash belongs to the definition (ADR-061). The only trusted way to use
/// a stored version is <c>ValidatedWorkflow.Load(DefinitionJson, DefinitionHash)</c> in Application, which re-parses,
/// re-validates and recomputes the hash and refuses a mismatch (<c>WorkflowTampered</c>).
/// </para>
/// </summary>
public sealed class WorkflowVersion : IOrganizationOwned
{
    /// <summary>Equal to the parser's definition size limit (one UTF-16 char is at least one UTF-8 byte).</summary>
    public const int MaxDefinitionChars = 262_144;

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

    /// <summary>The user who published the version (audit).</summary>
    public Guid? PublishedBy { get; private set; }

    /// <summary>Attestations the publisher explicitly acknowledged (comma-separated names), or null when there were none.</summary>
    public string? AcknowledgedAttestations { get; private set; }

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

    public void Publish(DateTimeOffset publishedAt, Guid? publishedBy = null, string? acknowledgedAttestations = null)
    {
        if (Status != WorkflowVersionStatus.Draft)
        {
            throw new InvalidOperationException($"Only a draft can be published; this version is {Status}.");
        }

        if (publishedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(publishedAt), "A version cannot be published before it was created.");
        }

        Status = WorkflowVersionStatus.Published;
        PublishedAt = publishedAt;
        PublishedBy = publishedBy;
        AcknowledgedAttestations = string.IsNullOrWhiteSpace(acknowledgedAttestations) ? null : acknowledgedAttestations;
    }

    public void Archive(DateTimeOffset archivedAt)
    {
        if (Status != WorkflowVersionStatus.Published)
        {
            throw new InvalidOperationException($"Only a published version can be archived; this version is {Status}.");
        }

        if (archivedAt < PublishedAt!.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(archivedAt), "A version cannot be archived before it was published.");
        }

        Status = WorkflowVersionStatus.Archived;
        ArchivedAt = archivedAt;
    }

    /// <summary>Same rule as <see cref="DefinitionHashFormat.IsWellFormed"/>.</summary>
    public static bool IsWellFormedHash(string? hash) => DefinitionHashFormat.IsWellFormed(hash);

    private void SetContent(int schemaVersion, string definitionJson, string definitionHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionJson);
        if (definitionJson.Length > MaxDefinitionChars)
        {
            throw new ArgumentException($"A definition cannot exceed {MaxDefinitionChars} characters.", nameof(definitionJson));
        }

        if (!DefinitionHashFormat.IsWellFormed(definitionHash))
        {
            throw new ArgumentException("The definition hash must be 'sha256:' followed by 64 lowercase hex digits.", nameof(definitionHash));
        }

        if (ReadDeclaredSchemaVersion(definitionJson) != schemaVersion)
        {
            throw new ArgumentException("The schema version must equal the definition's top-level 'schemaVersion'.", nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        DefinitionJson = definitionJson;
        DefinitionHash = definitionHash;
    }

    /// <summary>
    /// The definition's single top-level integer <c>schemaVersion</c>, or null when it is missing, duplicated, not an
    /// integer, or the text is not JSON. A consistency check only; full validation is Application's job.
    /// </summary>
    private static long? ReadDeclaredSchemaVersion(string definitionJson)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(definitionJson), new JsonReaderOptions { MaxDepth = 64 });
        long? found = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isSchemaVersion = reader.ValueTextEquals("schemaVersion"u8);
                reader.Read();
                if (isSchemaVersion)
                {
                    if (found is not null || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                    {
                        return null;
                    }

                    found = value;
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return found;
    }
}
