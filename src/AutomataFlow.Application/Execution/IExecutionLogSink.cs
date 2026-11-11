namespace AutomataFlow.Application.Execution;

/// <summary>Persists execution events (implemented by Infrastructure in Phase 5).</summary>
public interface IExecutionLogSink
{
    ValueTask WriteAsync(ExecutionLogEntry entry, CancellationToken cancellationToken);
}
