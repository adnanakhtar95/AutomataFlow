namespace AutomataFlow.Application.Execution;

public enum ExecutionLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// One structured execution event. <paramref name="Message"/> and <paramref name="Properties"/> must already be
/// redacted: they never contain credentials, sensitive input values or URL query values.
/// </summary>
public sealed record ExecutionLogEntry(
    ExecutionScope Scope,
    Guid SessionId,
    DateTimeOffset Timestamp,
    ExecutionLogLevel Level,
    string EventType,
    string Message,
    IReadOnlyDictionary<string, string?> Properties);
