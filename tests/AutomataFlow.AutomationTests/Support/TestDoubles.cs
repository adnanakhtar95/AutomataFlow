using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AutomataFlow.Application.Execution;
using AutomataFlow.Automation.Network.Proxy;
using Microsoft.Extensions.Logging;

namespace AutomataFlow.AutomationTests.Support;

/// <summary>
/// Deterministic DNS for attack tests: fixed answers, mixed answers and rebinding sequences (nth lookup returns
/// the nth answer). Unknown names do not resolve. No real DNS is ever used, so tests run offline.
/// </summary>
public sealed class FakeDnsResolver : IDnsResolver
{
    private readonly ConcurrentDictionary<string, IPAddress[][]> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Lookups { get; } = new();

    public void Set(string host, params string[] addresses) => _answers[host] = [addresses.Select(IPAddress.Parse).ToArray()];

    public void SetSequence(string host, params string[][] answers) =>
        _answers[host] = answers.Select(a => a.Select(IPAddress.Parse).ToArray()).ToArray();

    public void Reset(string host) => _counts.TryRemove(host, out _);

    public int LookupCount(string host) => _counts.GetValueOrDefault(host);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Lookups.Enqueue(host);
        var n = _counts.AddOrUpdate(host, 1, (_, c) => c + 1);
        IReadOnlyList<IPAddress> result = _answers.TryGetValue(host, out var answers) ? answers[Math.Min(n - 1, answers.Length - 1)] : [];
        return Task.FromResult(result);
    }
}

/// <summary>
/// Records every upstream connection the proxy makes and maps fake "public" addresses (e.g. 8.8.8.8:80) to local
/// fixture servers, so public-destination behaviour is testable without the Internet. Unmapped endpoints are
/// connected directly (used for the explicitly allowed local DemoCrm).
/// </summary>
public sealed class RecordingConnector : IUpstreamConnector
{
    private readonly ConcurrentDictionary<IPEndPoint, IPEndPoint> _map = new();
    private readonly DirectUpstreamConnector _direct = new();

    public ConcurrentQueue<IPEndPoint> Attempts { get; } = new();

    public void Map(IPEndPoint publicEndpoint, IPEndPoint localEndpoint) => _map[publicEndpoint] = localEndpoint;

    public Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        Attempts.Enqueue(endpoint);
        return _direct.ConnectAsync(_map.GetValueOrDefault(endpoint, endpoint), cancellationToken);
    }
}

public sealed class InMemoryArtifactStore : IArtifactStore
{
    public ConcurrentQueue<(ArtifactSaveRequest Request, ArtifactDescriptor Descriptor, byte[] Content)> Items { get; } = new();

    public async Task<ArtifactDescriptor> SaveAsync(ArtifactSaveRequest request, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var descriptor = new ArtifactDescriptor(
            Guid.NewGuid(),
            request.Kind,
            $"{request.Scope.OrganizationId:N}/{request.SessionId:N}/{Guid.NewGuid():N}",
            request.FileName,
            request.ContentType,
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DateTimeOffset.UtcNow);
        Items.Enqueue((request, descriptor, bytes));
        return descriptor;
    }
}

public sealed class CollectingLogSink : IExecutionLogSink
{
    public ConcurrentQueue<ExecutionLogEntry> Entries { get; } = new();

    public ValueTask WriteAsync(ExecutionLogEntry entry, CancellationToken cancellationToken)
    {
        Entries.Enqueue(entry);
        return ValueTask.CompletedTask;
    }

    public IEnumerable<ExecutionLogEntry> For(Guid sessionId) => Entries.Where(e => e.SessionId == sessionId);

    /// <summary>Every piece of text an entry carries (message and property values).</summary>
    public IEnumerable<string> AllText() =>
        Entries.SelectMany(e => new[] { e.Message, e.EventType }.Concat(e.Properties.Values.Where(v => v is not null)!))!;
}

/// <summary>Captures everything written through ILogger (messages, scopes and exceptions) for leak checks.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Lines.Enqueue($"scope {category}: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}

public static class Wait
{
    /// <summary>Polls a condition (used only to observe asynchronous side effects in tests).</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
