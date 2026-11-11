namespace AutomataFlow.Application.Execution;

public enum ArtifactKind
{
    Screenshot = 0,
    Download = 1,
    Trace = 2,
}

/// <summary>
/// Metadata of a stored execution artifact. Artifacts are customer data: they are only ever served through
/// authorized, organization-scoped endpoints and are never opened or executed by the platform.
/// </summary>
/// <param name="FileName">Sanitized display name; never used as a storage path.</param>
/// <param name="StorageKey">Opaque, store-generated key.</param>
public sealed record ArtifactDescriptor(
    Guid Id,
    ArtifactKind Kind,
    string StorageKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt);
