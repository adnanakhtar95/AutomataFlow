using System.Collections.Concurrent;
using AutomataFlow.Infrastructure.Jobs;

namespace AutomataFlow.Worker;

/// <summary>
/// The record attempts this Worker is running, with their cancellation. Two races are closed here:
/// <list type="bullet">
/// <item>an attempt is registered BEFORE its work may start, so a very fast attempt cannot finish (and unregister)
/// before it was registered, which would leave a stale entry the heartbeat renews and cancels forever;</item>
/// <item>cancelling an attempt that finished after the heartbeat took its snapshot is a no-op, never an
/// <see cref="ObjectDisposedException"/> that aborts the rest of the heartbeat iteration. The token source is disposed
/// only when the work has ended AND no cancellation is in progress; cancellation callbacks never run under a lock.</item>
/// </list>
/// </summary>
public sealed class RunningAttempts
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>A snapshot of the running claims.</summary>
    public IReadOnlyList<ClaimedRecord> Snapshot() => [.. _entries.Values.Select(e => e.Claim)];

    public IReadOnlyList<Task> Tasks() => [.. _entries.Values.Select(e => e.Task)];

    /// <summary>
    /// Registers the attempt, then starts <paramref name="work"/> with a token linked to <paramref name="stopping"/>.
    /// The entry is removed when the work ends, whatever the outcome, and <paramref name="finished"/> runs last.
    /// </summary>
    public Task Start(ClaimedRecord claim, Func<CancellationToken, Task> work, Action? finished, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(work);
        var entry = new Entry(claim, CancellationTokenSource.CreateLinkedTokenSource(stopping));
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.Task = Task.Run(async () =>
        {
            await registered.Task;
            try
            {
                await work(entry.Cancellation.Token);
            }
            finally
            {
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(claim.Id, entry));
                entry.Finish();
                finished?.Invoke();
            }
        }, CancellationToken.None);
        _entries[claim.Id] = entry;
        registered.SetResult();
        return entry.Task;
    }

    /// <summary>Cancels the attempt of <paramref name="recordId"/> if it is still running. Returns false when it already finished.</summary>
    public Task<bool> CancelAsync(Guid recordId) =>
        _entries.TryGetValue(recordId, out var entry) ? entry.CancelAsync() : Task.FromResult(false);

    /// <summary>Cancels every running attempt (graceful shutdown).</summary>
    public async Task CancelAllAsync()
    {
        foreach (var id in _entries.Keys)
        {
            await CancelAsync(id);
        }
    }

    private sealed class Entry(ClaimedRecord claim, CancellationTokenSource cancellation)
    {
        private readonly Lock _gate = new();
        private bool _finished;
        private int _cancelling;

        public ClaimedRecord Claim { get; } = claim;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        public async Task<bool> CancelAsync()
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return false;
                }

                _cancelling++;
            }

            try
            {
                await Cancellation.CancelAsync();
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _cancelling--;
                    DisposeIfIdle();
                }
            }
        }

        public void Finish()
        {
            lock (_gate)
            {
                _finished = true;
                DisposeIfIdle();
            }
        }

        private void DisposeIfIdle()
        {
            if (_finished && _cancelling == 0)
            {
                Cancellation.Dispose(); // idempotent
            }
        }
    }
}
