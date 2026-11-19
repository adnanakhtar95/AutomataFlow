using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using AutomataFlow.Api.Health;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Persistence;
using AutomataFlow.IntegrationTests.Api;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutomataFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Phase 7: the production database roles of deploy/postgres/init (ADR-078). The REAL init script's SQL is applied (with
/// unique role and database names, since roles are cluster-wide), migrations run as the owner, and the Api then works
/// end to end through the application role, which cannot change the schema.
/// </summary>
public sealed class DatabaseRolesTests
{
    private static string InitScriptSql()
    {
        var script = File.ReadAllText(Path.Combine(PlatformApi.RepositoryRoot, "deploy", "postgres", "init", "10-automataflow-roles.sh")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = script.IndexOf("<<'SQL'\n", StringComparison.Ordinal) + "<<'SQL'\n".Length;
        return script[start..script.LastIndexOf("\nSQL", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task The_Api_works_through_the_application_role_which_cannot_change_the_schema()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (owner, app, db) = ($"af_owner_{suffix}", $"af_app_{suffix}", $"af_roles_{suffix}");
        var (ownerPassword, appPassword) = (Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var sql = InitScriptSql()
            .Replace("automataflow_owner", owner, StringComparison.Ordinal)
            .Replace("automataflow_app", app, StringComparison.Ordinal)
            .Replace(":'owner_pw'", $"'{ownerPassword}'", StringComparison.Ordinal)
            .Replace(":'app_pw'", $"'{appPassword}'", StringComparison.Ordinal)
            .Replace(":\"db\"", $"\"{db}\"", StringComparison.Ordinal);
        var parts = sql.Split($"\\connect \"{db}\"\n");
        Assert.Equal(2, parts.Length);

        var server = TestDatabase.RequireServerConnectionString();
        try
        {
            await ExecuteAsync(server, parts[0]);
            await ExecuteAsync(new NpgsqlConnectionStringBuilder(server) { Database = db }.ConnectionString, parts[1]);

            var ownerConnection = new NpgsqlConnectionStringBuilder(server) { Database = db, Username = owner, Password = ownerPassword }.ConnectionString;
            var appConnection = new NpgsqlConnectionStringBuilder(server) { Database = db, Username = app, Password = appPassword }.ConnectionString;
            await using (var context = new AutomataFlowDbContext(new DbContextOptionsBuilder<AutomataFlowDbContext>().UseNpgsql(ownerConnection).Options, new TenantScope()))
            {
                await SchemaMigrator.MigrateAsync(context, app, CancellationToken.None); // as the owner, exactly like the `migrate` service
            }

            // The application role: data access works for the whole Api surface used by customers.
            await using (var factory = new ApiFactory(appConnection, settings: new Dictionary<string, string?> { ["Auth:PermitsPerMinute"] = "1000" }))
            {
                var client = factory.CreateClient();
                var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = $"roles-{suffix}@example.test", password = PlatformApi.Password, organizationName = "Roles Ltd", organizationSlug = $"roles-{suffix}" });
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
                var token = JsonNode.Parse(await register.Content.ReadAsStringAsync())!["accessToken"]!.GetValue<string>();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var version = await JobApiTests.PublishedVersionAsync(client, "roles");
                var created = await JobApiTests.UploadAsync(client, version, JobApiTests.Header + "\n" + JobApiTests.Row(1) + "\n");
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var jobId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<Guid>();
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/jobs/{jobId}/cancel", null)).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/secrets/crmPassword", new { value = "s3cret-value" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/overview")).StatusCode);
            }

            // ... and it cannot change the schema or the migration history.
            await using var connection = new NpgsqlConnection(appConnection);
            await connection.OpenAsync();
            foreach (var ddl in new[] { "CREATE TABLE af_intruder (id int)", "DROP TABLE \"Jobs\"", "ALTER TABLE \"Jobs\" ADD COLUMN x int", "DELETE FROM \"__EFMigrationsHistory\"", "CREATE ROLE af_escalate" })
            {
                await using var command = new NpgsqlCommand(ddl, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE); DROP ROLE IF EXISTS \"{app}\"; DROP ROLE IF EXISTS \"{owner}\";");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var statement in sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.TrimEnd(';')).Where(s => s.Length > 0))
        {
#pragma warning disable CA2100 // the repository's own init script with locally generated names
            await using var command = new NpgsqlCommand(statement, connection);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>Phase 7: the Data Protection key ring encrypted at rest with a certificate (ADR-079), through the real Infrastructure wiring.</summary>
public sealed class KeyRingProtectionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("af-keyring-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Certificate(string name, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AutomataFlow test key ring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static IDataProtector Protector(string keys, Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";
        settings["DataProtection:KeysDirectory"] = keys;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    [Fact]
    public void Keys_are_written_encrypted_and_only_the_certificate_holder_can_use_them()
    {
        var keys = Path.Combine(_root.FullName, "keys");
        var certificate = Certificate("keyring.pfx", "pw-1");
        var withCertificate = new Dictionary<string, string?> { ["DataProtection:CertificatePath"] = certificate, ["DataProtection:CertificatePassword"] = "pw-1" };

        var payload = Protector(keys, new(withCertificate)).Protect("workspace secret");

        var xml = string.Concat(Directory.GetFiles(keys, "*.xml").Select(File.ReadAllText));
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<value>", xml, StringComparison.Ordinal); // no plaintext master key on disk

        Assert.Equal("workspace secret", Protector(keys, new(withCertificate)).Unprotect(payload)); // e.g. the Worker
        Assert.ThrowsAny<CryptographicException>(() => Protector(keys, []).Unprotect(payload)); // a copy of the volume alone
    }

    [Fact]
    public void A_rotated_certificate_still_reads_keys_of_the_previous_one()
    {
        var keys = Path.Combine(_root.FullName, "keys");
        var (old, current) = (Certificate("old.pfx", "old-pw"), Certificate("new.pfx", "new-pw"));
        var payload = Protector(keys, new() { ["DataProtection:CertificatePath"] = old, ["DataProtection:CertificatePassword"] = "old-pw" }).Protect("value");

        var rotated = Protector(keys, new()
        {
            ["DataProtection:CertificatePath"] = current,
            ["DataProtection:CertificatePassword"] = "new-pw",
            ["DataProtection:PreviousCertificatePath"] = old,
            ["DataProtection:PreviousCertificatePassword"] = "old-pw",
        });
        Assert.Equal("value", rotated.Unprotect(payload));
    }

    [Fact]
    public void A_wrong_certificate_password_stops_startup_without_echoing_the_password()
    {
        var certificate = Certificate("keyring.pfx", "right");
        var error = Assert.Throws<InvalidOperationException>(() => Protector(Path.Combine(_root.FullName, "keys"),
            new() { ["DataProtection:CertificatePath"] = certificate, ["DataProtection:CertificatePassword"] = "wrong-password-xyz" }));
        Assert.DoesNotContain("wrong-password-xyz", error.ToString(), StringComparison.Ordinal);
    }
}

public sealed class TrustedProxiesTests
{
    [Fact]
    public void Parses_cidr_lists_and_refuses_invalid_entries()
    {
        Assert.Empty(TrustedProxies.Parse(null));
        Assert.Equal(2, TrustedProxies.Parse("172.30.10.0/24, 10.0.0.0/8").Count);
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("172.30.10.0/24, not-a-network"));
    }
}
