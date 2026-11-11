using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutomataFlow.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. The connection string comes from the environment variable
/// <c>ConnectionStrings__Default</c>; nothing is hard-coded or committed.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AutomataFlowDbContext>
{
    public AutomataFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set the ConnectionStrings__Default environment variable before running dotnet ef (see deploy/.env.example).");
        }

        var options = new DbContextOptionsBuilder<AutomataFlowDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AutomataFlowDbContext).Assembly.FullName))
            .Options;

        return new AutomataFlowDbContext(options);
    }
}
