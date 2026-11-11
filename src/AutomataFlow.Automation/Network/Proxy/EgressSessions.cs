using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>
/// A registered browser session at the egress proxy: its random proxy credentials, its own policy, counters and
/// decisions. Disposing it revokes the credentials and closes the session's open connections.
/// </summary>
public sealed class EgressSession : IDisposable
{
    private readonly EgressSessionRegistry _registry;
    private readonly ConcurrentQueue<EgressEvent> _events = new();
    private readonly ConcurrentDictionary<long, IDisposable> _connections = new();
    private long _bytes;
    private int _activeConnections;

    internal EgressSession(EgressSessionRegistry registry, Guid sessionId, EgressPolicy policy, string username, string password)
    {
        _registry = registry;
        SessionId = sessionId;
        Policy = policy;
        Username = username;
        Password = password;
    }

    public Guid SessionId { get; }

    public EgressPolicy Policy { get; }

    /// <summary>Random per-session proxy user name (not secret on its own).</summary>
    public string Username { get; }

    /// <summary>Random per-session proxy password (128-bit). Never logged.</summary>
    public string Password { get; }

    public bool IsRevoked { get; private set; }

    public long BytesTransferred => Interlocked.Read(ref _bytes);

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public IReadOnlyCollection<EgressEvent> Events => _events;

    public event EventHandler<EgressEvent>? Decision;

    internal void Record(EgressEvent egressEvent)
    {
        _events.Enqueue(egressEvent);
        Decision?.Invoke(this, egressEvent);
    }

    /// <summary>Adds transferred bytes and reports whether the session is still within its quota.</summary>
    internal bool AddBytes(long count, long quota) => Interlocked.Add(ref _bytes, count) <= quota;

    internal bool TryOpenConnection(int maxConnections)
    {
        if (Interlocked.Increment(ref _activeConnections) <= maxConnections)
        {
            return true;
        }

        Interlocked.Decrement(ref _activeConnections);
        return false;
    }

    internal void Track(long connectionId, IDisposable connection) => _connections[connectionId] = connection;

    internal void CloseConnection(long connectionId)
    {
        if (_connections.TryRemove(connectionId, out _))
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    public void Dispose()
    {
        if (IsRevoked)
        {
            return;
        }

        IsRevoked = true;
        _registry.Remove(this);
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }
}

/// <summary>Maps random proxy credentials to browser sessions and their policies.</summary>
public sealed class EgressSessionRegistry
{
    private readonly ConcurrentDictionary<string, EgressSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public EgressSession Register(Guid sessionId, EgressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var session = new EgressSession(this, sessionId, policy, "af-" + RandomToken(12), RandomToken(16));
        _sessions[session.Username] = session;
        return session;
    }

    /// <summary>Constant-time password check; unknown, revoked or wrong credentials return <c>null</c>.</summary>
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
