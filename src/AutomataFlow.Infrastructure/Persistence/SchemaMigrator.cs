using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.Infrastructure.Persistence;

/// <summary>
/// The `migrate` step of a deployment (ADR-078): applies pending migrations as the schema OWNER, then makes sure the
/// application role can read but never rewrite the migration history (the init script's default privileges grant it
/// data access to every table the owner creates, including <c>__EFMigrationsHistory</c>).
/// </summary>
public static partial class SchemaMigrator
{
    public const string DefaultApplicationRole = "automataflow_app";

    public static async Task<IReadOnlyList<string>> MigrateAsync(AutomataFlowDbContext db, string? applicationRole, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);

        var role = string.IsNullOrWhiteSpace(applicationRole) ? DefaultApplicationRole : applicationRole;
        if (!RoleName().IsMatch(role))
        {
            throw new InvalidOperationException("Database:ApplicationRole must be a plain role name (letters, digits, underscores).");
        }

        // The role may not exist (development databases use a single role): then there is nothing to restrict.
#pragma warning disable EF1002 // the role name is validated above (identifier pattern), never user input
        await db.Database.ExecuteSqlRawAsync($"""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN
                REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON "__EFMigrationsHistory" FROM "{role}";
              END IF;
            END $$;
            """, cancellationToken);
#pragma warning restore EF1002
        return pending;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}
