using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.IntegrationTests.Infrastructure;

/// <summary>Applies the real migrations to a throwaway database on the Docker PostgreSQL server.</summary>
public class MigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static AutomataFlowDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AutomataFlowDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task Migrations_apply_to_an_empty_database_and_leave_the_model_in_sync()
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync(ct);
        await using var context = CreateContext(database.ConnectionString);

        await context.Database.MigrateAsync(ct);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(ct));
        Assert.False(context.Database.HasPendingModelChanges(), "The model has changes that are not covered by a migration.");
    }

    [Fact]
    public async Task Organizations_and_members_round_trip_and_slugs_are_unique()
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync(ct);
        await using var context = CreateContext(database.ConnectionString);
        await context.Database.MigrateAsync(ct);

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "demo@example.test", Email = "demo@example.test", CreatedAt = Now };
        var org = Organization.Create("Acme", "acme", Now);
        context.Users.Add(user);
        context.Organizations.Add(org);
        context.OrganizationMembers.Add(OrganizationMember.Create(org.Id, user.Id, OrganizationRole.Owner, Now));
        await context.SaveChangesAsync(ct);

        await using var readContext = CreateContext(database.ConnectionString);
        var member = await readContext.OrganizationMembers.SingleAsync(ct);
        Assert.Equal(org.Id, member.OrganizationId);
        Assert.Equal(OrganizationRole.Owner, member.Role);

        context.Organizations.Add(Organization.Create("Other", "acme", Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Members_must_reference_an_existing_organization_and_user()
    {
        var ct = CancellationToken.None;
        await using var database = await TestDatabase.CreateAsync(ct);
        await using var context = CreateContext(database.ConnectionString);
        await context.Database.MigrateAsync(ct);

        context.OrganizationMembers.Add(OrganizationMember.Create(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Viewer, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
    }
}
