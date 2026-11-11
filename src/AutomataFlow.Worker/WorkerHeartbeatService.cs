namespace AutomataFlow.Worker;

/// <summary>
/// Phase 1 skeleton: proves the Worker process hosts, logs and shuts down gracefully.
/// Job claiming, execution, retries and cancellation arrive in Phase 5.
/// </summary>
public sealed class WorkerHeartbeatService(ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker started");

        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogDebug("Worker heartbeat");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Worker stopping");
    }
}
