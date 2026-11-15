using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.IntegrationTests.Support;

/// <summary>A throwaway database with all migrations applied, shared by the tests of one class.</summary>
public sealed class MigratedDatabase : IAsyncLifetime
{
    private TemporaryDatabase? _database;

    public string ConnectionString => _database!.ConnectionString;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(CancellationToken.None);
        await using var context = Context(null);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    /// <summary>A context bound to <paramref name="organization"/> (or unscoped), optionally with the query-filter backstop off.</summary>
    public AutomataFlowDbContext Context(Guid? organization, bool queryFiltersDisabled = false)
    {
        var scope = new TenantScope();
        if (organization is { } org)
        {
            scope.Enter(org);
        }

        var options = new DbContextOptionsBuilder<AutomataFlowDbContext>().UseNpgsql(ConnectionString).Options;
        return new AutomataFlowDbContext(options, scope) { QueryFiltersDisabled = queryFiltersDisabled };
    }
}
