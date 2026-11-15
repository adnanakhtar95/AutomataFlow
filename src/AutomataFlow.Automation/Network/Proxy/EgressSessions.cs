using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AutomataFlow.Application.Network;

namespace AutomataFlow.Automation.Network.Proxy;

/// <summary>One decision of the egress proxy, recorded per browser session.</summary>
/// <param name="Target">Host (or IP) and port as requested by the browser; no paths or queries.</param>
/// <param name="Address">The pinned address the proxy connected to (allowed) or the address that caused denial.</param>
public sealed record EgressEvent(
    DateTimeOffset Timestamp,
    Guid SessionId,
    bool Allowed,
    string Reason,
    string Method,
    string Target,
    string? Address);

public enum EgressAdmission
{
    Admitted,
    Revoked,
    TooManyConnections,
}

/// <summary>Outcome of the revocation barrier (ADR-060). Only <see cref="Completed"/> may ever establish a fence.</summary>
public enum RevocationResult
{
    Completed,
    TimedOut,
}

/// <summary>
/// A registered browser session at the egress proxy: its random proxy credentials, its own policy, counters,
/// decisions, its connections and its upstream write gate. Revocation is a BARRIER (docs/PHASE4_PLAN.md §7.6.2):
/// <list type="number">
/// <item>B1 (Lr): under the session lock, mark revoked (closes connection admission AND upstream write admission at once),
/// then remove the credentials and cancel <see cref="RevocationToken"/>;</item>
/// <item>B2: wait until every upstream write admitted before Lr has returned;</item>
/// <item>B3 (Ta): close every upstream socket abortively (linger 0, RST) and every browser-side socket;</item>
/// <item>B4: wait until every connection handler of the session has finished;</item>
/// <item>B5: <see cref="RevocationResult.Completed"/>, or <see cref="RevocationResult.TimedOut"/> if B2/B4 did not finish in time.</item>
/// </list>
/// Cancellation and credential removal alone are never a fence.
/// </summary>
public sealed class EgressSession : IDisposable
{
    private readonly EgressSessionRegistry _registry;
    private readonly ConcurrentQueue<EgressEvent> _events = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<long, ConnectionState> _connections = [];
    private readonly CancellationTokenSource _revocation = new();
    private readonly int _maxConnections;
    private long _bytes;
    private bool _revoked;
    private Task<RevocationResult>? _barrier;

    internal EgressSession(EgressSessionRegistry registry, Guid sessionId, EgressPolicy policy, string username, string password, int maxConnections)
    {
        _registry = registry;
        SessionId = sessionId;
        Policy = policy;
        Username = username;
        Password = password;
        _maxConnections = maxConnections;
        Gate = new UpstreamWriteGate(_lock);
    }

    public Guid SessionId { get; }

    public EgressPolicy Policy { get; }

    /// <summary>Random per-session proxy user name (not secret on its own).</summary>
    public string Username { get; }

    /// <summary>Random per-session proxy password (128-bit). Never logged, persisted or shared.</summary>
    public string Password { get; }

    public bool IsRevoked
    {
        get
        {
            lock (_lock)
            {
                return _revoked;
            }
        }
    }

    public long BytesTransferred => Interlocked.Read(ref _bytes);

    public int ActiveConnections
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    public IReadOnlyCollection<EgressEvent> Events => _events;

    /// <summary>Cancelled at barrier step B1. Wakes reads, DNS and connects only; it is never a safety mechanism (W3).</summary>
    public CancellationToken RevocationToken => _revocation.Token;

    internal UpstreamWriteGate Gate { get; }

    public event EventHandler<EgressEvent>? Decision;

    internal void Record(EgressEvent egressEvent)
    {
        _events.Enqueue(egressEvent);
        Decision?.Invoke(this, egressEvent);
    }

    /// <summary>Adds transferred bytes and reports whether the session is still within its quota.</summary>
    internal bool AddBytes(long count, long quota) => Interlocked.Add(ref _bytes, count) <= quota;

    /// <summary>
    /// Connection admission, atomic with revocation (closes gap G1): if revocation began (B1) the connection is
    /// refused; otherwise its browser-side socket and handler are registered so the barrier closes and awaits them.
    /// </summary>
    internal EgressAdmission TryAdmit(long connectionId, Socket browserSide, out ConnectionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(browserSide);
        lease = null;
        lock (_lock)
        {
            if (_revoked)
            {
                return EgressAdmission.Revoked;
            }

            if (_connections.Count >= _maxConnections)
            {
                return EgressAdmission.TooManyConnections;
            }

            var state = new ConnectionState(browserSide);
            _connections[connectionId] = state;
            lease = new ConnectionLease(this, connectionId, state);
            return EgressAdmission.Admitted;
        }
    }

    /// <summary>Registers an upstream socket with its connection; refused (false) once revocation began.</summary>
    internal bool TrackUpstream(long connectionId, Socket upstream)
    {
        lock (_lock)
        {
            if (_revoked || !_connections.TryGetValue(connectionId, out var state))
            {
                return false;
            }

            state.AddUpstream(upstream);
            return true;
        }
    }

    internal void Release(long connectionId, ConnectionState state)
    {
        lock (_lock)
        {
            _connections.Remove(connectionId);
        }

        state.Done.TrySetResult();
    }

    /// <summary>The revocation barrier with the registry's default timeout.</summary>
    public Task<RevocationResult> RevokeAsync() => RevokeAsync(_registry.RevocationTimeout);

    /// <summary>
    /// Runs the barrier (B1-B5). Idempotent: concurrent and repeated calls share one barrier. B1's critical section
    /// completes synchronously before this method returns its task.
    /// </summary>
    public Task<RevocationResult> RevokeAsync(TimeSpan timeout)
    {
        Task drained;
        List<ConnectionState> snapshot;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_barrier is not null)
            {
                return _barrier;
            }

            // B1 (Lr): connection admission and upstream write admission close in the same critical section.
            _revoked = true;
            drained = Gate.CloseWhileHoldingLock();
            snapshot = [.. _connections.Values];
            _barrier = CompleteBarrierAsync(timeout, drained, snapshot, started.Task, _registry.Hooks);
        }

        // Rest of B1, synchronously and outside the lock: no new authentication, wake every waiting read/DNS/connect.
        _registry.Remove(this);
        _revocation.Cancel();
        started.TrySetResult();
        return _barrier;
    }

    /// <summary>Best-effort revocation without waiting (B1 synchronously). Never used to establish a fence.</summary>
    public void Dispose() => _ = RevokeAsync();

    private static async Task<RevocationResult> CompleteBarrierAsync(TimeSpan timeout, Task drained, List<ConnectionState> snapshot, Task b1Done, EgressTestHooks? hooks)
    {
        // B2-B5 start only after B1 (including credential removal and cancellation) has fully happened.
        await b1Done.ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(timeout);

        // B2: every write admitted before Lr returns. An admitted send is never cancelled (W3).
        var writesDrained = await WaitAsync(drained, deadline.Token).ConfigureAwait(false);

        if (hooks is not null)
        {
            // Test seam only: lets a test force handler cleanup to run before B3 (the D1 race), deterministically.
            await hooks.AtAsync(EgressHookPoint.BeforeAbort, 0).ConfigureAwait(false);
        }

        // B3 (Ta): abort. Upstream sockets abortively (RST; unsent send-queue data discarded), browser sides closed.
        // Done even when B2 timed out (defence in depth), but then the result is TimedOut and no fence follows.
        foreach (var state in snapshot)
        {
            state.AbortAll();
        }

        // B4: every handler of the session has finished.
        var handlersDone = await WaitAsync(Task.WhenAll(snapshot.Select(s => s.Done.Task)), deadline.Token).ConfigureAwait(false);

        // B5.
        return writesDrained && handlersDone ? RevocationResult.Completed : RevocationResult.TimedOut;
    }

    private static async Task<bool> WaitAsync(Task task, CancellationToken deadline)
    {
        try
        {
            await task.WaitAsync(deadline).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One admitted browser connection: its browser-side socket, its upstream sockets and its handler.</summary>
    internal sealed class ConnectionState(Socket browserSide)
    {
        private readonly List<Socket> _upstreams = [];

        public Socket BrowserSide { get; } = browserSide;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddUpstream(Socket upstream)
        {
            lock (_upstreams)
            {
                _upstreams.Add(upstream);
            }
        }

        public void AbortAll()
        {
            Socket[] upstreams;
            lock (_upstreams)
            {
                upstreams = [.. _upstreams];
            }

            foreach (var upstream in upstreams)
            {
                UpstreamSockets.Abort(upstream);
            }

            try
            {
                BrowserSide.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // already closed
            }
        }
    }
}

/// <summary>Held by a connection handler for its whole lifetime; disposing it signals handler completion to the barrier (B4).</summary>
internal sealed class ConnectionLease : IDisposable
{
    private readonly EgressSession _session;
    private readonly EgressSession.ConnectionState _state;
    private int _released;

    public ConnectionLease(EgressSession session, long connectionId, EgressSession.ConnectionState state)
    {
        _session = session;
        ConnectionId = connectionId;
        _state = state;
    }

    public long ConnectionId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _session.Release(ConnectionId, _state);
        }
    }
}

/// <summary>
/// The upstream write gate (docs/PHASE4_PLAN.md §7.6.1). Every operation that creates an upstream connection or puts
/// payload bytes into an upstream socket runs between a successful <see cref="TryEnter"/> and its <see cref="Exit"/>.
/// The gate shares the session lock, so write admission and revocation (B1) are totally ordered: a write admitted before
/// Lr completes before the barrier aborts the sockets (B2), and no write is admitted after Lr.
/// </summary>
internal sealed class UpstreamWriteGate
{
    private readonly Lock _lock;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _admitted;

    public UpstreamWriteGate(Lock sessionLock) => _lock = sessionLock;

    public int Admitted
    {
        get
        {
            lock (_lock)
            {
                return _admitted;
            }
        }
    }

    /// <summary>The ONLY admission check for upstream payload and upstream connection creation (W1).</summary>
    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _admitted++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_lock)
        {
            _admitted--;
            if (_closed && _admitted == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>Called by the session inside its B1 critical section (the caller holds the session lock).</summary>
    public Task CloseWhileHoldingLock()
    {
        _closed = true;
        if (_admitted == 0)
        {
            _drained.TrySetResult();
        }

        return _drained.Task;
    }
}

/// <summary>Maps random proxy credentials to browser sessions and their policies.</summary>
public sealed class EgressSessionRegistry
{
    private readonly ConcurrentDictionary<string, EgressSession> _sessions = new(StringComparer.Ordinal);

    public EgressSessionRegistry(int maxConnectionsPerSession = 32, TimeSpan? revocationTimeout = null)
    {
        MaxConnectionsPerSession = maxConnectionsPerSession;
        RevocationTimeout = revocationTimeout ?? TimeSpan.FromSeconds(10);
    }

    public int Count => _sessions.Count;

    public int MaxConnectionsPerSession { get; }

    public TimeSpan RevocationTimeout { get; }

    /// <summary>Deterministic interleaving hooks for tests; always null in production.</summary>
    internal EgressTestHooks? Hooks { get; set; }

    public EgressSession Register(Guid sessionId, EgressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var session = new EgressSession(this, sessionId, policy, "af-" + RandomToken(12), RandomToken(16), MaxConnectionsPerSession);
        _sessions[session.Username] = session;
        return session;
    }

    /// <summary>
    /// Constant-time password check; unknown, revoked or wrong credentials return <c>null</c>. A successful check is
    /// NOT admission: <see cref="EgressSession.TryAdmit"/> re-checks revocation under the session lock.
    /// </summary>
    public EgressSession? Authenticate(string? username, string? password)
    {
        if (username is null || password is null || !_sessions.TryGetValue(username, out var session) || session.IsRevoked)
        {
            // Still hash something so timing does not reveal whether the user name exists.
            _ = CryptographicOperations.FixedTimeEquals(Hash(password ?? string.Empty), Hash(string.Empty));
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(Hash(password), Hash(session.Password)) ? session : null;
    }

    internal void Remove(EgressSession session) => _sessions.TryRemove(new KeyValuePair<string, EgressSession>(session.Username, session));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string RandomToken(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
