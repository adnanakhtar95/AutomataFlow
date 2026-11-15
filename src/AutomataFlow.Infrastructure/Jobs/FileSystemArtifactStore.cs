using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AutomataFlow.Application.Execution;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Infrastructure.Jobs;

public sealed class ArtifactStorageOptions
{
    public const string SectionName = "Artifacts";

    /// <summary>Root directory on a volume shared by the Worker (writes) and the Api (reads). Never served statically.</summary>
    public string RootDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Artifact storage on disk (ADR-008). Keys are generated here only — organization / job / record / attempt / random id —
/// so a key always starts with its organization and never contains a client-chosen name. Reads validate the key's shape
/// and that the resolved path stays under the root.
/// </summary>
public sealed partial class FileSystemArtifactStore(IOptions<ArtifactStorageOptions> options, TimeProvider clock) : IArtifactStore
{
    private string Root => Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.RootDirectory)
        ? throw new InvalidOperationException("Artifacts:RootDirectory is not configured.")
        : options.Value.RootDirectory);

    public async Task<ArtifactDescriptor> SaveAsync(ArtifactSaveRequest request, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        var id = Guid.NewGuid();
        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        extension = ExtensionPattern().IsMatch(extension) ? extension : string.Empty;
        var key = string.Join('/', request.Scope.OrganizationId.ToString("N"), (request.Scope.JobId ?? Guid.Empty).ToString("N"),
            (request.Scope.JobRecordId ?? Guid.Empty).ToString("N"), request.Scope.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), id.ToString("N") + extension);
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        long size;
        string sha;
        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81_920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            size = file.Length;
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        File.Move(temp, path);
        return new ArtifactDescriptor(id, request.Kind, key, request.FileName, request.ContentType, size, sha, clock.GetUtcNow());
    }

    /// <summary>Opens a stored artifact by its key. The caller must already have authorized access to the artifact row.</summary>
    public Stream OpenRead(string storageKey) => new FileStream(PathFor(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read);

    private string PathFor(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("Invalid artifact key.", nameof(key));
        }

        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new ArgumentException("Invalid artifact key.", nameof(key));
    }

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}/[0-9a-f]{32}/[0-9]{1,6}/[0-9a-f]{32}(\\.[a-z0-9]{1,8})?$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^\\.[a-z0-9]{1,8}$")]
    private static partial Regex ExtensionPattern();
}
