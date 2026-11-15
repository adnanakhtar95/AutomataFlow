using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AutomataFlow.Application.Network;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Network.Proxy;
using AutomataFlow.AutomationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomataFlow.AutomationTests.Proxy;

/// <summary>
/// H1–H4 (docs/PHASE4_PLAN.md §7.6.7, §13.4; ADR-057, ADR-060): dispatch-horizon races through the REAL egress proxy and
/// revocation barrier. A test-only TCP relay between the proxy and a fake target simulates network transit δ: bytes it
/// accepted before our RST are still delivered after δ, as packets already in the network would be. The target commits
/// a complete request after a processing delay p. <c>FencedAt</c> is taken when the barrier completed (the executor's
/// completion is later, so this is conservative). The real <see cref="RepeatSafetyEvaluator"/> decides eligibility.
/// Times are scaled to seconds so the suite stays fast: contract L = 2 s, skew 0.
/// </summary>
public sealed class DispatchHorizonRaceTests
{
    private const int LatencySeconds = 2;
    private static readonly TimeSpan L = TimeSpan.FromSeconds(LatencySeconds);
    private static readonly RepeatSafetyPolicy Policy = RepeatSafetyPolicy.Create(0, 3, 3);
    private static readonly ReconcilePolicySnapshot Contract = new(EvidenceLevel.Authoritative, MatchKeyKind.OperationId, LatencySeconds, 300);
    private static readonly CurrentWorkflow Current = new(false, true, true, Contract);
    private static readonly Guid Operation = Guid.NewGuid();
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);

    /// <summary>H1: the request's last byte is in the relay when the barrier completes; the target commits after FencedAt.</summary>
    [Fact]
    public async Task H1_a_request_in_transit_at_the_barrier_commits_after_the_fence_and_is_found_after_L()
    {
        await using var run = await RaceRun.StartAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1_000));
        await run.SendAsync(cutAtFraction: 1.0);
        var fencedAt = await run.RevokeAsync();

        // Before H + L every NotFound is too early (the request is still in transit or processing).
        await DelayUntilAsync(fencedAt + TimeSpan.FromMilliseconds(200));
        var early = run.Lookup();
        Assert.False(early.Found);
        Assert.Equal(RepeatDecisionKind.ReconcileTooEarly, Decide(fencedAt, early).Kind);

        await DelayUntilAsync(fencedAt + L + TimeSpan.FromMilliseconds(50));
        var lookup = run.Lookup();

        Assert.True(lookup.Found, "the request committed within the attested latency, so the eligible lookup finds it");
        Assert.True(run.Target.Commits.Single() > fencedAt, "the commit happened AFTER FencedAt: a fence never proves failure");
        Assert.Equal(RepeatDecisionKind.NotNeeded, Decide(fencedAt, lookup).Kind);
        Assert.Single(run.Target.Commits); // POSTs = 1
        Assert.Empty(Checker.Violations(fencedAt, L, run.Target.Commits, [early, lookup]));
    }

    /// <summary>H2: the barrier fires mid-body; the target never receives a complete request and never commits.</summary>
    [Fact]
    public async Task H2_a_request_cut_by_the_barrier_never_commits_and_an_eligible_NotFound_stays_true()
    {
        await using var run = await RaceRun.StartAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(500));
        await run.SendAsync(cutAtFraction: 0.5);
        var fencedAt = await run.RevokeAsync();
        await run.TrySendRestAsync(); // the browser side keeps writing: nothing more may reach the target

        await DelayUntilAsync(fencedAt + L + TimeSpan.FromMilliseconds(50));
        var lookup = run.Lookup();
        Assert.False(lookup.Found);
        Assert.Equal(RepeatDecisionKind.Allowed, Decide(fencedAt, lookup).Kind);

        await Task.Delay(L); // another L: the eligible NotFound is never followed by a commit
        Assert.Empty(run.Target.Commits);
        Assert.True(run.Target.IncompleteRequests >= 1);
        Assert.Empty(Checker.Violations(fencedAt, L, run.Target.Commits, [lookup with { Eligible = true }]));
    }

    /// <summary>H4 (negative control): δ + p exceeds the declared L, so an eligible NotFound is followed by a commit and the checker must say so.</summary>
    [Fact]
    public async Task H4_a_wrong_latency_attestation_is_detected_by_the_invariant_checker()
    {
        await using var run = await RaceRun.StartAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(2_400));
        await run.SendAsync(cutAtFraction: 1.0);
        var fencedAt = await run.RevokeAsync();

        await DelayUntilAsync(fencedAt + L + TimeSpan.FromMilliseconds(50));
        var lookup = run.Lookup();
        Assert.False(lookup.Found);
        var decision = Decide(fencedAt, lookup);
        Assert.Equal(RepeatDecisionKind.Allowed, decision.Kind); // the evaluator trusts the (wrong) contract

        await WaitUntilAsync(() => !run.Target.Commits.IsEmpty, "late commit");
        var violations = Checker.Violations(fencedAt, L, run.Target.Commits, [lookup with { Eligible = true }]);

        Assert.Contains(violations, v => v.Contains("commit after an eligible NotFound", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("later than FencedAt + L", StringComparison.Ordinal));
    }

    /// <summary>H3 (real sockets): randomized barrier timing, δ and p with δ + p ≤ L; run concurrently to stay fast.</summary>
    [Fact]
    public async Task H3_randomized_real_races_never_commit_after_an_eligible_NotFound()
    {
        var random = new Random(4711);
        var cases = Enumerable.Range(0, 24).Select(_ => (
            Delta: TimeSpan.FromMilliseconds(random.Next(0, 500)),
            Processing: TimeSpan.FromMilliseconds(random.Next(0, 1_000)),
            Cut: random.Next(3) == 0 ? 0.2 + (random.NextDouble() * 0.6) : 1.0,
            LookupOffset: TimeSpan.FromMilliseconds(random.Next(0, 2_400)))).ToList();

        var results = await Task.WhenAll(cases.Select(async c =>
        {
            await using var run = await RaceRun.StartAsync(c.Delta, c.Processing);
            await run.SendAsync(c.Cut);
            var fencedAt = await run.RevokeAsync();
            await DelayUntilAsync(fencedAt + c.LookupOffset);
            var observation = run.Lookup();
            observation = observation with { Eligible = !observation.Found && Decide(fencedAt, observation).Kind == RepeatDecisionKind.Allowed };
            await DelayUntilAsync(fencedAt + L + TimeSpan.FromMilliseconds(600)); // every commit has happened by now
            return Checker.Violations(fencedAt, L, run.Target.Commits, [observation]);
        }));

        Assert.All(results, Assert.Empty);
    }

    /// <summary>
    /// H3 (model, 1,000 iterations): the same invariant over a virtual-time model of barrier, relay and target, driving the
    /// real evaluator. With δ + p ≤ L it never fails; with δ + p > L (wrong attestation) the checker must find violations.
    /// </summary>
    [Fact]
    public void H3_model_one_thousand_randomized_races()
    {
        var random = new Random(1115);
        var violationsWithCorrectContract = 0;
        var violationsWithWrongContract = 0;
        for (var i = 0; i < 1_000; i++)
        {
            violationsWithCorrectContract += ModelRun(random, maxTransitPlusProcessing: L).Count;
            violationsWithWrongContract += ModelRun(random, maxTransitPlusProcessing: L * 2).Count;
        }

        Assert.Equal(0, violationsWithCorrectContract);
        Assert.True(violationsWithWrongContract > 0, "the negative control must produce violations");
    }

    private static List<string> ModelRun(Random random, TimeSpan maxTransitPlusProcessing)
    {
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var lastByteAccepted = t0.AddMilliseconds(random.Next(0, 1_000));
        var complete = random.Next(3) != 0;
        var fencedAt = lastByteAccepted.AddMilliseconds(random.Next(0, 200)); // the barrier completes after the last accepted byte
        var transitPlusProcessing = TimeSpan.FromMilliseconds(random.Next(0, (int)maxTransitPlusProcessing.TotalMilliseconds + 1));
        DateTimeOffset? commit = complete ? lastByteAccepted + transitPlusProcessing : null;
        var at = fencedAt.AddMilliseconds(random.Next(0, 6_000));
        var found = commit is { } c && c <= at;
        var observation = new Observation(at, found, false);
        observation = observation with { Eligible = !found && Decide(fencedAt, observation).Kind == RepeatDecisionKind.Allowed };
        var commits = new List<DateTimeOffset>();
        if (commit is { } committed)
        {
            commits.Add(committed);
        }

        return Checker.Violations(fencedAt, L, commits, [observation]);
    }

    // ----- Evaluation and invariant -----

    private static RepeatDecision Decide(DateTimeOffset fencedAt, Observation observation)
    {
        var snapshot = new AttemptSnapshot("sha256:" + new string('c', 64), 1, 120, false, true, Contract);
        AttemptSummary[] history =
        [
            new(Operation, 1, ExecutionIntent.Normal, AttemptState.Completed, WorkflowOutcome.Indeterminate, new ErrorSummary("Timeout", ErrorCategory.Transient, true),
                SideEffectState.Started, snapshot, fencedAt.AddSeconds(-5), fencedAt.AddSeconds(-4), fencedAt, null, true, null, null, null),
            new(Operation, 2, ExecutionIntent.Reconcile, AttemptState.Completed, observation.Found ? WorkflowOutcome.ReconciledFound : WorkflowOutcome.ReconciledNotFound, null,
                SideEffectState.NotStarted, snapshot, observation.At, null, observation.At, null, true, null, null,
                new ReconciliationEvidence(observation.Found ? ReconciliationFinding.Found : ReconciliationFinding.NotFound, observation.At, Contract)),
        ];

        return RepeatSafetyEvaluator.Evaluate(Current, Operation, history, observation.At, Policy);
    }

    private sealed record Observation(DateTimeOffset At, bool Found, bool Eligible);

    /// <summary>The H3 invariant: every commit ≤ FencedAt + L; no commit after an eligible authoritative NotFound.</summary>
    private static class Checker
    {
        public static List<string> Violations(DateTimeOffset fencedAt, TimeSpan latency, IEnumerable<DateTimeOffset> commits, IEnumerable<Observation> observations)
        {
            var violations = new List<string>();
            var commitList = commits.ToList();
            foreach (var commit in commitList.Where(c => c > fencedAt + latency))
            {
                violations.Add($"a commit at H+{(commit - fencedAt).TotalMilliseconds:F0} ms is later than FencedAt + L");
            }

            foreach (var observation in observations.Where(o => o.Eligible && !o.Found))
            {
                if (commitList.Any(c => c > observation.At))
                {
                    violations.Add($"commit after an eligible NotFound observed at H+{(observation.At - fencedAt).TotalMilliseconds:F0} ms");
                }
            }

            return violations;
        }
    }

    // ----- Real proxy, relay and target -----

    private sealed class RaceRun : IAsyncDisposable
    {
        private EgressProxyServer _proxy = null!;
        private const int BodyBytes = 64 * 1024;
        private TcpClient _client = null!;
        private NetworkStream _stream = null!;
        private EgressSession _session = null!;
        private int _sent;

        private RaceRun(CommitTarget target, DelayRelay relay)
        {
            Target = target;
            Relay = relay;
        }

        public CommitTarget Target { get; }

        public DelayRelay Relay { get; }

        public static async Task<RaceRun> StartAsync(TimeSpan transit, TimeSpan processing)
        {
            var target = new CommitTarget(processing);
            var relay = new DelayRelay(target.Endpoint, transit);
            var run = new RaceRun(target, relay);
            var dns = new FakeDnsResolver();
            dns.Set("target.test", "8.8.8.8");
            var proxy = new EgressProxyServer(Options.Create(new EgressProxyOptions { RevocationTimeoutSeconds = 10 }), dns, relay, TestLedgers.Create(),
                new OsConnectionIdentityProvider(), TimeProvider.System, NullLogger<EgressProxyServer>.Instance);
            proxy.Start();
            run._proxy = proxy;
            run._session = proxy.Sessions.Register(Guid.NewGuid(), new EgressPolicy([AllowedHostRule.Parse("https://target.test")]));
            run._client = new TcpClient { NoDelay = true };
            await run._client.ConnectAsync(proxy.Endpoint!);
            run._stream = run._client.GetStream();
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{run._session.Username}:{run._session.Password}"));
            await run._stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.test:443 HTTP/1.1\r\nHost: target.test:443\r\nProxy-Authorization: Basic {auth}\r\n\r\n"));
            Assert.Equal(200, await ReadStatusAsync(run._stream));
            return run;
        }

        /// <summary>Sends the length prefix and the given fraction of the body; returns once the relay accepted all of it.</summary>
        public async Task SendAsync(double cutAtFraction)
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, BodyBytes);
            var partial = (int)(BodyBytes * cutAtFraction);
            await _stream.WriteAsync(prefix);
            await _stream.WriteAsync(new byte[partial]);
            _sent = partial;
            await WaitUntilAsync(() => Relay.AcceptedBytes >= 4 + partial, "relay accepted the request bytes");
        }

        public async Task TrySendRestAsync()
        {
            try
            {
                await _stream.WriteAsync(new byte[BodyBytes - _sent]);
            }
            catch (IOException)
            {
                // The connection was aborted by the barrier: expected.
            }
        }

        /// <summary>Runs the revocation barrier; FencedAt is taken after it completed.</summary>
        public async Task<DateTimeOffset> RevokeAsync()
        {
            var result = await _session.RevokeAsync().WaitAsync(Long);
            Assert.Equal(RevocationResult.Completed, result);
            return DateTimeOffset.UtcNow;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _proxy.DisposeAsync();
            Relay.Dispose();
            Target.Dispose();
        }

        public Observation Lookup()
        {
            var at = DateTimeOffset.UtcNow;
            return new Observation(at, Target.Commits.Any(c => c <= at), false);
        }
    }

    /// <summary>Accepts the proxy's upstream connection and forwards each received chunk to the target after a fixed delay.</summary>
    private sealed class DelayRelay : IUpstreamConnector, IDisposable
    {
        private readonly IPEndPoint _target;
        private readonly TimeSpan _delay;
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private long _accepted;

        public DelayRelay(IPEndPoint target, TimeSpan delay)
        {
            _target = target;
            _delay = delay;
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen();
            _ = Task.Run(AcceptLoopAsync);
        }

        public long AcceptedBytes => Interlocked.Read(ref _accepted);

        public void Dispose() => _listener.Dispose();

        public async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!, cancellationToken);
            return socket;
        }

        private async Task AcceptLoopAsync()
        {
            var fromProxy = await _listener.AcceptAsync();
            var toTarget = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await toTarget.ConnectAsync(_target);
            var queue = Channel.CreateUnbounded<(byte[] Data, long ReleaseAt)>();
            var writer = Task.Run(async () =>
            {
                await foreach (var (data, releaseAt) in queue.Reader.ReadAllAsync())
                {
                    var wait = TimeSpan.FromSeconds((releaseAt - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait);
                    }

                    await toTarget.SendAsync(data);
                }

                toTarget.Shutdown(SocketShutdown.Send);
            });

            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await fromProxy.ReceiveAsync(buffer)) > 0)
                {
                    Interlocked.Add(ref _accepted, read);
                    await queue.Writer.WriteAsync((buffer[..read], Stopwatch.GetTimestamp() + (long)(_delay.TotalSeconds * Stopwatch.Frequency)));
                }
            }
            catch (SocketException)
            {
                // RST from the barrier: stop accepting; what was already accepted is still "in the network".
            }

            queue.Writer.Complete();
            await writer;
        }
    }

    /// <summary>Commits a complete request (4-byte length + body) after the processing delay; incomplete requests never commit.</summary>
    private sealed class CommitTarget : IDisposable
    {
        public void Dispose() => _listener.Dispose();

        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly TimeSpan _processing;
        private int _incomplete;

        public CommitTarget(TimeSpan processing)
        {
            _processing = processing;
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen();
            _ = Task.Run(AcceptAsync);
        }

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndPoint!;

        public ConcurrentQueue<DateTimeOffset> Commits { get; } = new();

        public int IncompleteRequests => Volatile.Read(ref _incomplete);

        private async Task AcceptAsync()
        {
            using var socket = await _listener.AcceptAsync();
            var received = new List<byte>();
            var buffer = new byte[16 * 1024];
            int? expected = null;
            try
            {
                int read;
                while ((read = await socket.ReceiveAsync(buffer)) > 0)
                {
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (expected is null && received.Count >= 4)
                    {
                        expected = BinaryPrimitives.ReadInt32BigEndian(received.ToArray().AsSpan(0, 4));
                    }

                    if (expected is { } length && received.Count >= 4 + length)
                    {
                        // The server keeps processing even if the client goes away: commit after p.
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(_processing);
                            Commits.Enqueue(DateTimeOffset.UtcNow);
                        });
                        return;
                    }
                }
            }
            catch (SocketException)
            {
            }

            Interlocked.Increment(ref _incomplete);
        }
    }

    // ----- Helpers -----

    private static async Task DelayUntilAsync(DateTimeOffset at)
    {
        var wait = at - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Long)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(5);
        }
    }

    private static async Task<int> ReadStatusAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Long);
        var head = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cts.Token) == 1)
        {
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
            {
                break;
            }
        }

        return int.Parse(Encoding.ASCII.GetString(head.ToArray()).AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }
}
