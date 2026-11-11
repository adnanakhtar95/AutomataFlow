namespace AutomataFlow.Application.Execution;

/// <summary>Stores execution artifacts under organization-scoped keys (implemented by Infrastructure in Phase 5).</summary>
public interface IArtifactStore
{
    Task<ArtifactDescriptor> SaveAsync(ArtifactSaveRequest request, Stream content, CancellationToken cancellationToken);
}

/// <param name="FileName">Sanitized display name supplied by the caller; the store must generate its own storage key.</param>
public sealed record ArtifactSaveRequest(
    ExecutionScope Scope,
    Guid SessionId,
    ArtifactKind Kind,
    string FileName,
    string ContentType);
