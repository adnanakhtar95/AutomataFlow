using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.Worker;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>
/// The Worker's running-attempt bookkeeping (found by the Phase 6 end-to-end run: "Heartbeat failed
/// ObjectDisposedException" when an attempt finished between the heartbeat's snapshot and its cancellation).
/// </summary>
public sealed class RunningAttemptsTests
{
    private static ClaimedRecord Claim() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RecordIntent.Normal, null);

    [Fact]
    public async Task Cancelling_an_attempt_that_finished_after_the_snapshot_is_a_no_op_not_an_exception()
    {
        var attempts = new RunningAttempts();
        var claim = Claim();
        var task = attempts.Start(claim, _ => Task.CompletedTask, null, CancellationToken.None);
        var snapshot = attempts.Snapshot(); // may or may not still contain the claim
        await task;

        Assert.All(snapshot, c => Assert.Equal(claim.Id, c.Id));
        Assert.False(await attempts.CancelAsync(claim.Id)); // previously: ObjectDisposedException
        Assert.Equal(0, attempts.Count);
    }

    [Fact]
    public async Task An_attempt_is_registered_before_its_work_runs_so_a_fast_attempt_never_leaves_a_stale_entry()
    {
        var attempts = new RunningAttempts();
        var seenRegistered = new List<bool>();
        var tasks = new List<Task>();
        for (var i = 0; i < 500; i++)
        {
            var claim = Claim();
            tasks.Add(attempts.Start(claim, _ =>
            {
                lock (seenRegistered)
                {
                    seenRegistered.Add(attempts.Snapshot().Any(c => c.Id == claim.Id));
                }

                return Task.CompletedTask; // finishes immediately
            }, null, CancellationToken.None));
        }

        await Task.WhenAll(tasks);
        Assert.All(seenRegistered, Assert.True);
        Assert.Equal(0, attempts.Count);
    }

    [Fact]
    public async Task A_running_attempt_is_cancelled_through_its_token_and_then_removed()
    {
        var attempts = new RunningAttempts();
        var claim = Claim();
        var started = new TaskCompletionSource();
        var finished = 0;
        var task = attempts.Start(claim, async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, () => Interlocked.Increment(ref finished), CancellationToken.None);
        await started.Task;

        Assert.True(await attempts.CancelAsync(claim.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal((0, 1), (attempts.Count, finished));
    }

    [Fact]
    public async Task Concurrent_cancellation_and_completion_never_throw()
    {
        for (var i = 0; i < 300; i++)
        {
            var attempts = new RunningAttempts();
            var claim = Claim();
            var task = attempts.Start(claim, async _ => await Task.Yield(), null, CancellationToken.None);
            var cancels = Enumerable.Range(0, 4).Select(_ => Task.Run(() => attempts.CancelAsync(claim.Id))).ToArray();
            await Task.WhenAll(cancels); // must not throw ObjectDisposedException
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Stopping_the_Worker_cancels_every_running_attempt()
    {
        using var stopping = new CancellationTokenSource();
        var attempts = new RunningAttempts();
        var tasks = Enumerable.Range(0, 3).Select(_ => attempts.Start(Claim(), token => Task.Delay(Timeout.Infinite, token), null, stopping.Token)).ToList();

        await attempts.CancelAllAsync();
        foreach (var task in tasks)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.Equal(0, attempts.Count);
    }
}
