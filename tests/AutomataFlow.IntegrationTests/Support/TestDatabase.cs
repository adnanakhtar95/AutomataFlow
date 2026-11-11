using Npgsql;

namespace AutomataFlow.IntegrationTests.Support;

/// <summary>
/// Access to the Docker PostgreSQL server for tests. The server connection string comes from the
/// <c>AUTOMATAFLOW_TEST_DB</c> environment variable (never committed). Tests create and drop their own
/// uniquely named databases and never touch the development database.
/// </summary>
internal static class TestDatabase
{
    public const string EnvironmentVariable = "AUTOMATAFLOW_TEST_DB";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static string RequireServerConnectionString()
    {
        var value = ServerConnectionString;
        if (string.IsNullOrWhiteSpace(value))
        {
            // Fail loudly rather than skip silently: database tests that quietly do not run give false confidence.
            throw new InvalidOperationException(
                $"Set the {EnvironmentVariable} environment variable to a PostgreSQL server connection string " +
                "(start deploy/docker-compose.yml, port 5433) to run database tests.");
        }

        return value;
    }

    /// <summary>Creates an empty, uniquely named database and returns its connection string plus a cleanup action.</summary>
    public static async Task<TemporaryDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var server = RequireServerConnectionString();
        var name = $"automataflow_test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // name is generated locally from a GUID, not user input
            command.CommandText = $"CREATE DATABASE \"{name}\"";
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(server) { Database = name };
        return new TemporaryDatabase(server, name, builder.ConnectionString);
    }
}

internal sealed class TemporaryDatabase(string serverConnectionString, string name, string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // name is generated locally from a GUID, not user input
        command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
