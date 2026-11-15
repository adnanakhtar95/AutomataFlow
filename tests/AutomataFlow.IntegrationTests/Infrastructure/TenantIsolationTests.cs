using AutomataFlow.Domain.Common;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutomataFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Phase 5 step 1 (docs/ARCHITECTURE.md §7.1): the database-side tenant isolation layers. The query filter and the write
/// guard are backstops; the composite organization-scoped foreign keys make a cross-organization reference impossible
/// even for a buggy application layer.
/// </summary>
public sealed class TenantIsolationTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Definition = """{"schemaVersion":1,"name":"tenant-test"}""";
    private static readonly string Hash = "sha256:" + new string('b', 64);

    private sealed record Tenant(Guid Organization, Guid User, Workflow Workflow, WorkflowVersion Version, Job Job, JobRecord Record);

    private async Task<Tenant> SeedTenantAsync(string slug)
    {
        var org = Organization.Create(slug, slug + "-" + Guid.NewGuid().ToString("N")[..8], Now);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = slug + Guid.NewGuid().ToString("N") + "@example.test", Email = slug + "@example.test", CreatedAt = Now };
        await using var context = db.Context(org.Id);
        context.Organizations.Add(org);
        context.Users.Add(user);
        context.OrganizationMembers.Add(OrganizationMember.Create(org.Id, user.Id, OrganizationRole.Owner, Now));
        var workflow = Workflow.Create(org.Id, "register", null, Now);
        var version = WorkflowVersion.CreateDraft(org.Id, workflow.Id, workflow.NextVersionNumber(), 1, Definition, Hash, Now);
        version.Publish(Now);
        var job = Job.Create(org.Id, workflow.Id, version.Id, JobOptions.Default, "{}", user.Id, "key-1", "demo.csv", Now);
        var record = JobRecord.CreatePending(org.Id, job.Id, 1, """{"email":"a@example.test"}""", false, Now);
        context.AddRange(workflow, version, job, record);
        context.OrganizationSecrets.Add(OrganizationSecret.Create(org.Id, "crmPassword", "protected-payload", Now));
        await context.SaveChangesAsync();
        return new Tenant(org.Id, user.Id, workflow, version, job, record);
    }

    [Fact]
    public async Task The_query_filter_backstop_hides_every_other_organizations_rows()
    {
        var a = await SeedTenantAsync("alpha");
        var b = await SeedTenantAsync("bravo");

        await using var asB = db.Context(b.Organization);
        Assert.Null(await asB.Workflows.SingleOrDefaultAsync(w => w.Id == a.Workflow.Id));
        Assert.Null(await asB.WorkflowVersions.SingleOrDefaultAsync(v => v.Id == a.Version.Id));
        Assert.Null(await asB.Jobs.SingleOrDefaultAsync(j => j.Id == a.Job.Id));
        Assert.Null(await asB.JobRecords.SingleOrDefaultAsync(r => r.Id == a.Record.Id));
        Assert.Empty(await asB.OrganizationSecrets.Where(s => s.OrganizationId == a.Organization).ToListAsync());
        Assert.All(await asB.Jobs.ToListAsync(), j => Assert.Equal(b.Organization, j.OrganizationId));
        Assert.All(await asB.OrganizationMembers.ToListAsync(), m => Assert.Equal(b.Organization, m.OrganizationId));

        // No scope at all: nothing is visible (fail closed), not everything.
        await using var unscoped = db.Context(null);
        Assert.Empty(await unscoped.Jobs.ToListAsync());

        // Control: with the backstop OFF the rows exist, so the filter (and later the application layer) is what hides them.
        await using var unfiltered = db.Context(b.Organization, queryFiltersDisabled: true);
        Assert.NotNull(await unfiltered.Jobs.SingleOrDefaultAsync(j => j.Id == a.Job.Id));
    }

    [Fact]
    public async Task The_write_guard_refuses_rows_of_another_organization_or_without_a_scope()
    {
        var a = await SeedTenantAsync("charlie");
        var b = await SeedTenantAsync("delta");

        await using (var asB = db.Context(b.Organization))
        {
            asB.Workflows.Add(Workflow.Create(a.Organization, "smuggled", null, Now));
            await Assert.ThrowsAsync<TenantIsolationException>(() => asB.SaveChangesAsync());
        }

        await using (var unscoped = db.Context(null))
        {
            unscoped.Workflows.Add(Workflow.Create(a.Organization, "unscoped", null, Now));
            await Assert.ThrowsAsync<TenantIsolationException>(() => unscoped.SaveChangesAsync());
        }

        // Modifying a loaded row of A through B's unit of work (filters off to load it) is refused too.
        await using (var asB = db.Context(b.Organization, queryFiltersDisabled: true))
        {
            var foreign = await asB.Jobs.SingleAsync(j => j.Id == a.Job.Id);
            foreign.RequestCancellation();
            await Assert.ThrowsAsync<TenantIsolationException>(() => asB.SaveChangesAsync());
        }

        await using var check = db.Context(a.Organization);
        Assert.False((await check.Jobs.SingleAsync(j => j.Id == a.Job.Id)).CancelRequested);
        Assert.DoesNotContain(await check.Workflows.Select(w => w.Name).ToListAsync(), n => n is "smuggled" or "unscoped");
    }

    [Fact]
    public async Task Composite_foreign_keys_make_cross_organization_references_impossible_in_the_database()
    {
        var a = await SeedTenantAsync("echo");
        var b = await SeedTenantAsync("foxtrot");

        // B's own row that points at A's job: the write guard is satisfied (the row is B's), the database is not.
        await using (var asB = db.Context(b.Organization))
        {
            asB.JobRecords.Add(JobRecord.CreatePending(b.Organization, a.Job.Id, 2, "{}", false, Now));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => asB.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)error.InnerException!).SqlState);
        }

        await using (var asB = db.Context(b.Organization))
        {
            asB.Jobs.Add(Job.Create(b.Organization, a.Workflow.Id, a.Version.Id, JobOptions.Default, "{}", b.User, null, null, Now));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => asB.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)error.InnerException!).SqlState);
        }
    }

    [Fact]
    public async Task Idempotency_keys_and_operation_ids_are_unique_per_organization_only()
    {
        var a = await SeedTenantAsync("golf");
        var b = await SeedTenantAsync("hotel");

        // Same key in another organization: allowed (seeded with "key-1" for both).
        await using (var asA = db.Context(a.Organization))
        {
            asA.Jobs.Add(Job.Create(a.Organization, a.Workflow.Id, a.Version.Id, JobOptions.Default, "{}", a.User, "key-1", null, Now));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => asA.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)error.InnerException!).SqlState);
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""UPDATE "JobRecords" SET "OperationId" = @op WHERE "Id" = @id""", conn);
        cmd.Parameters.AddWithValue("op", a.Record.OperationId);
        cmd.Parameters.AddWithValue("id", b.Record.Id);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync()); // another organization may share the value

        await using var dup = new NpgsqlCommand("""INSERT INTO "JobRecords" ("Id","OrganizationId","JobId","RowNumber","OperationId","InputJson","InputProtected","Status","NextIntent","AttemptCount","ReconcileFailures","CreatedAt") VALUES (@id,@org,@job,7,@op,'{}',false,'Pending','Normal',0,0,now())""", conn);
        dup.Parameters.AddWithValue("id", Guid.NewGuid());
        dup.Parameters.AddWithValue("org", a.Organization);
        dup.Parameters.AddWithValue("job", a.Job.Id);
        dup.Parameters.AddWithValue("op", a.Record.OperationId);
        var dupError = await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, dupError.SqlState);
    }

    [Fact]
    public async Task The_journal_allows_only_one_blocking_entry_per_operation()
    {
        var a = await SeedTenantAsync("india");
        var incarnation = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using (var reg = new NpgsqlCommand("""
            INSERT INTO "WorkerIncarnations" ("Id","HostId","BootId","ProcessId","ProcessStartTime","UnitKind","UnitName","ConnectionLedger","RegisteredAt","LastHeartbeatAt")
            VALUES (@id,'h','b',1,now(),'LinuxCgroup','/u','/l',now(),now())
            """, conn))
        {
            reg.Parameters.AddWithValue("id", incarnation);
            await reg.ExecuteNonQueryAsync();
        }

        async Task InsertAsync(int attempt, bool blocking)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AttemptJournal" ("OrganizationId","OperationId","AttemptNumber","Intent","State","IncarnationId","LeaseExpiresAt","Blocking","ExecutionFenced")
                VALUES (@org,@op,@n,'Normal',@state,@inc,now(),@blocking,false)
                """, conn);
            cmd.Parameters.AddWithValue("org", a.Organization);
            cmd.Parameters.AddWithValue("op", operation);
            cmd.Parameters.AddWithValue("n", attempt);
            cmd.Parameters.AddWithValue("state", blocking ? "Started" : "Completed");
            cmd.Parameters.AddWithValue("inc", incarnation);
            cmd.Parameters.AddWithValue("blocking", blocking);
            await cmd.ExecuteNonQueryAsync();
        }

        await InsertAsync(1, blocking: false);
        await InsertAsync(2, blocking: false);
        await InsertAsync(3, blocking: true);
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(4, blocking: true));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("UX_AttemptJournal_OneBlocking", error.ConstraintName);
    }

    [Fact]
    public async Task A_malformed_definition_hash_is_refused_by_the_database()
    {
        var a = await SeedTenantAsync("juliet");
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""UPDATE "WorkflowVersions" SET "DefinitionHash" = 'sha256:XYZ' WHERE "Id" = @id""", conn);
        cmd.Parameters.AddWithValue("id", a.Version.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public void A_unit_of_work_cannot_switch_organizations()
    {
        var scope = new TenantScope();
        scope.Enter(Guid.NewGuid());
        Assert.Throws<TenantIsolationException>(() => scope.Enter(Guid.NewGuid()));
    }

    [Fact]
    public void Every_organization_owned_entity_except_the_explicit_sql_journal_has_the_query_filter_backstop()
    {
        using var context = db.Context(null);
        var owned = context.Model.GetEntityTypes().Where(e => typeof(IOrganizationOwned).IsAssignableFrom(e.ClrType)).ToList();
        Assert.True(owned.Count >= 12, $"expected the full Phase 5 model, found {owned.Count} organization-owned entities");
        foreach (var entity in owned)
        {
            var filtered = entity.GetDeclaredQueryFilters().Count > 0;
            Assert.Equal(!AutomataFlowDbContext.UnfilteredOrganizationOwned.Contains(entity.ClrType), filtered);
        }
    }
}
