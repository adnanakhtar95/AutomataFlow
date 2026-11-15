using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Infrastructure.Persistence.Journal;
using AutomataFlow.IntegrationTests.Support;
using AutomataFlow.Tests.Journal;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AutomataFlow.IntegrationTests.Journal;

/// <summary>A migrated database plus one data source for the journal tests.</summary>
public sealed class JournalDatabase : IAsyncLifetime
{
    public MigratedDatabase Database { get; } = new();

    public NpgsqlDataSource Source { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Source = NpgsqlDataSource.Create(Database.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Source.DisposeAsync();
        await Database.DisposeAsync();
    }

    internal PostgresAttemptJournal Journal(TimeProvider? clock, TimeSpan lease, TimeSpan margin) =>
        new(Source, Options.Create(new AttemptJournalOptions { LeaseSeconds = (int)lease.TotalSeconds, LeaseSafetyMarginSeconds = (int)margin.TotalSeconds }), clock);
}

/// <summary>Phase 5 step 5: the UNCHANGED shared conformance suite (§7.4, ADR-058) against the PostgreSQL journal.</summary>
public sealed class PostgresJournalConformanceTests(JournalDatabase db) : AttemptJournalConformanceTests, IClassFixture<JournalDatabase>
{
    protected override IAttemptJournal CreateJournal(TimeProvider clock, TimeSpan lease, TimeSpan margin) => db.Journal(clock, lease, margin);
}

/// <summary>PostgreSQL-specific journal properties: real concurrency, the database clock, append-only history, durability, ambiguous commits.</summary>
public sealed class PostgresJournalTests(JournalDatabase db) : IClassFixture<JournalDatabase>
{
    private static readonly AttemptSnapshot Snapshot = new("sha256:" + new string('a', 64), 1, 120, false, true, null);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    private static WorkerIncarnation Incarnation() =>
        new(new WorkerIncarnationId(Guid.NewGuid()), "host-1", "boot-1", 4242, DateTimeOffset.UtcNow, new ContainmentUnit(ContainmentKind.LinuxCgroup, "/w/" + Guid.NewGuid().ToString("N")), "/ledger");

    [Fact]
    public async Task Concurrent_reservations_from_many_connections_admit_exactly_one()
    {
        var journal = db.Journal(null, Lease, Margin);
        var workers = Enumerable.Range(0, 12).Select(_ => Incarnation()).ToList();
        foreach (var worker in workers)
        {
            await journal.RegisterIncarnationAsync(worker, default);
        }

        for (var round = 0; round < 25; round++)
        {
            var org = Guid.NewGuid();
            var operation = Guid.NewGuid();
            using var go = new Barrier(workers.Count);
            var results = await Task.WhenAll(workers.Select((w, i) => Task.Run(async () =>
            {
                go.SignalAndWait();
                return await journal.ReserveAsync(org, operation, i % 2 == 0 ? ExecutionIntent.Normal : ExecutionIntent.Reconcile, w.Id, default);
            })));

            Assert.Equal(1, results.Count(r => r.Status == ReservationStatus.Reserved));
            Assert.All(results.Where(r => r.Status != ReservationStatus.Reserved), r => Assert.Equal(ReservationStatus.InFlight, r.Status));
        }
    }

    [Fact]
    public async Task Without_an_override_every_timestamp_is_the_database_clock()
    {
        var journal = db.Journal(null, Lease, Margin);
        var worker = Incarnation();
        await journal.RegisterIncarnationAsync(worker, default);
        var org = Guid.NewGuid();
        var operation = Guid.NewGuid();

        await using var conn = await db.Source.OpenConnectionAsync();
        await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", conn);
        var before = (DateTimeOffset)(await clock.ExecuteScalarAsync() is DateTime d ? new DateTimeOffset(d, TimeSpan.Zero) : DateTimeOffset.MinValue);

        var reserved = await journal.ReserveAsync(org, operation, ExecutionIntent.Normal, worker.Id, default);
        var started = await journal.StartAsync(reserved.Reservation!, Snapshot, default);
        var finished = await journal.CompleteAsync(reserved.Reservation!, new AttemptCompletion(WorkflowOutcome.Succeeded, null, SideEffectState.NotStarted, true, null), default);

        Assert.True(reserved.Now >= before.AddMilliseconds(-1) && started >= reserved.Now && finished >= started, $"{before:O} {reserved.Now:O} {started:O} {finished:O}");
        Assert.True(Math.Abs((reserved.Now - DateTimeOffset.UtcNow).TotalMinutes) < 5, "the database clock is the server's clock");
        Assert.True(await journal.VerifyServerDurabilityAsync(default));
    }

    [Fact]
    public async Task Renewal_and_fencing_hold_across_journal_instances()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var first = db.Journal(clock, Lease, Margin);
        var second = db.Journal(clock, Lease, Margin); // another Worker process: same database, no shared memory
        var worker = Incarnation();
        await first.RegisterIncarnationAsync(worker, default);
        var org = Guid.NewGuid();
        var operation = Guid.NewGuid();

        var reserved = await first.ReserveAsync(org, operation, ExecutionIntent.Normal, worker.Id, default);
        await first.StartAsync(reserved.Reservation!, Snapshot, default);
        await second.SideEffectStartingAsync(reserved.Reservation!, "submit", TimeSpan.FromSeconds(30), default);
        clock.Advance(Lease + TimeSpan.FromSeconds(1));

        var other = Incarnation();
        await second.RegisterIncarnationAsync(other, default);
        Assert.Equal(ReservationStatus.AwaitingFence, (await second.ReserveAsync(org, operation, ExecutionIntent.Reconcile, other.Id, default)).Status);
        Assert.Equal(JournalErrorCodes.Fenced, (await Assert.ThrowsAsync<AttemptJournalException>(() => first.RenewLeaseAsync(reserved.Reservation!, default))).Code);
    }

    [Fact]
    public async Task History_is_append_only_in_the_database()
    {
        var journal = db.Journal(null, Lease, Margin);
        var worker = Incarnation();
        await journal.RegisterIncarnationAsync(worker, default);
        var org = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var reserved = await journal.ReserveAsync(org, operation, ExecutionIntent.Normal, worker.Id, default);
        await journal.StartAsync(reserved.Reservation!, Snapshot, default);
        await journal.SideEffectStartingAsync(reserved.Reservation!, "submit", TimeSpan.FromSeconds(30), default);
        await journal.CompleteAsync(reserved.Reservation!, new AttemptCompletion(WorkflowOutcome.Indeterminate, null, SideEffectState.Started, false, null), default);

        await using var conn = await db.Source.OpenConnectionAsync();
        async Task<string> RefusedAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, conn);
            command.Parameters.AddWithValue("org", org);
            command.Parameters.AddWithValue("op", operation);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            return error.SqlState;
        }

        const string Where = """ WHERE "OrganizationId" = @org AND "OperationId" = @op""";
        Assert.Equal("AF001", await RefusedAsync("""DELETE FROM "AttemptJournal" """ + Where));
        Assert.Equal("AF001", await RefusedAsync("""UPDATE "AttemptJournal" SET "CheckpointAt" = NULL""" + Where));
        Assert.Equal("AF001", await RefusedAsync("""UPDATE "AttemptJournal" SET "Outcome" = 'Succeeded'""" + Where));
        Assert.Equal("AF001", await RefusedAsync("""UPDATE "AttemptJournal" SET "State" = 'Started'""" + Where));
        Assert.Equal("AF001", await RefusedAsync("""UPDATE "AttemptJournal" SET "AttemptNumber" = 9""" + Where));

        // Blocking is derived, not writable: claiming "not blocking" for an unfenced checkpointed attempt does not stick.
        await using (var cheat = new NpgsqlCommand("""UPDATE "AttemptJournal" SET "Blocking" = false""" + Where, conn))
        {
            cheat.Parameters.AddWithValue("org", org);
            cheat.Parameters.AddWithValue("op", operation);
            await cheat.ExecuteNonQueryAsync();
        }

        var other = Incarnation();
        await journal.RegisterIncarnationAsync(other, default);
        Assert.Equal(ReservationStatus.AwaitingFence, (await journal.ReserveAsync(org, operation, ExecutionIntent.Reconcile, other.Id, default)).Status);
    }

    [Fact]
    public async Task A_checkpoint_whose_commit_fails_is_reported_unavailable_and_was_not_recorded()
    {
        var journal = db.Journal(null, Lease, Margin);
        var worker = Incarnation();
        await journal.RegisterIncarnationAsync(worker, default);
        var org = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var reserved = await journal.ReserveAsync(org, operation, ExecutionIntent.Normal, worker.Id, default);
        await journal.StartAsync(reserved.Reservation!, Snapshot, default);

        // The connection dies right before COMMIT: the outcome is unknown to the caller, who must not assume success.
        journal.BeforeCommitForTests = async tx =>
        {
            await using var killer = await db.Source.OpenConnectionAsync();
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", killer);
            kill.Parameters.AddWithValue("pid", tx.Connection!.ProcessID);
            await kill.ExecuteScalarAsync();
        };

        var error = await Assert.ThrowsAsync<AttemptJournalException>(() => journal.SideEffectStartingAsync(reserved.Reservation!, "submit", TimeSpan.FromSeconds(30), default));
        Assert.Equal(JournalErrorCodes.Unavailable, error.Code);

        journal.BeforeCommitForTests = null;
        NpgsqlConnection.ClearAllPools();
        var other = Incarnation();
        await journal.RegisterIncarnationAsync(other, default);
        var probe = await journal.ReserveAsync(org, operation, ExecutionIntent.Normal, other.Id, default);
        Assert.Equal(ReservationStatus.InFlight, probe.Status);
        Assert.Null(Assert.Single(probe.History).SideEffectCheckpointAt); // here the commit really did not happen
    }
}
