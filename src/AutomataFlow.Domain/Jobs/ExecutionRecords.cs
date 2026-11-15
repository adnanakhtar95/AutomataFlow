using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Jobs;

/// <summary>One structured execution event of an attempt (docs/ARCHITECTURE.md §6). Values are already redacted.</summary>
public sealed class ExecutionLog : IOrganizationOwned
{
    public const int MessageMaxLength = 2_000;

    private ExecutionLog()
    {
    }

    public long Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobAttemptId { get; private set; }

    public int Sequence { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public string Level { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string? StepId { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public string? PropertiesJson { get; private set; }

    public static ExecutionLog Create(Guid organizationId, Guid jobAttemptId, int sequence, DateTimeOffset timestamp, string level, string eventType, string? stepId, string message, string? propertiesJson)
    {
        if (organizationId == Guid.Empty || jobAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Organization and attempt are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(message);
        return new ExecutionLog
        {
            OrganizationId = organizationId,
            JobAttemptId = jobAttemptId,
            Sequence = sequence,
            Timestamp = timestamp,
            Level = level,
            EventType = eventType,
            StepId = stepId,
            Message = message.Length > MessageMaxLength ? message[..MessageMaxLength] : message,
            PropertiesJson = propertiesJson,
        };
    }
}

/// <summary>A stored artifact (screenshot, download, result file). Content is served only via an authorized endpoint.</summary>
public sealed class ExecutionArtifact : IOrganizationOwned
{
    private ExecutionArtifact()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobAttemptId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    /// <summary>Opaque storage key; always prefixed by the organization id.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static ExecutionArtifact Create(Guid id, Guid organizationId, Guid jobAttemptId, string kind, string storageKey, string fileName, string contentType, long sizeBytes, string sha256, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || jobAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Id, organization and attempt are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (!storageKey.StartsWith(organizationId.ToString("N"), StringComparison.Ordinal))
        {
            throw new ArgumentException("An artifact storage key must start with its organization id.", nameof(storageKey));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        return new ExecutionArtifact
        {
            Id = id,
            OrganizationId = organizationId,
            JobAttemptId = jobAttemptId,
            Kind = kind,
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            CreatedAt = createdAt,
        };
    }
}

/// <summary>A metered quantity, written from day one and billed later (docs/ARCHITECTURE.md §7).</summary>
public sealed class UsageRecord : IOrganizationOwned
{
    private UsageRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobId { get; private set; }

    public string Metric { get; private set; } = string.Empty;

    public long Quantity { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public static UsageRecord Create(Guid organizationId, Guid jobId, string metric, long quantity, DateTimeOffset recordedAt)
    {
        if (organizationId == Guid.Empty || jobId == Guid.Empty)
        {
            throw new ArgumentException("Organization and job are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        return new UsageRecord { Id = Guid.CreateVersion7(), OrganizationId = organizationId, JobId = jobId, Metric = metric, Quantity = quantity, RecordedAt = recordedAt };
    }
}

/// <summary>Usage metric names.</summary>
public static class UsageMetrics
{
    public const string AttemptsStarted = "attempts.started";
    public const string RecordsImported = "records.imported";
}
