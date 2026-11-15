using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.Infrastructure.Security;

/// <summary>
/// Encryption at rest for organization secrets and sensitive record values (ASP.NET Core Data Protection). Every purpose
/// includes the organization id, so a payload copied into another organization's row cannot be decrypted there.
/// </summary>
public sealed class ValueProtection(IDataProtectionProvider provider)
{
    private const string SecretPurpose = "organization-secret.v1";
    private const string RecordPurpose = "record-input.v1";

    public string ProtectSecret(Guid organizationId, string value) => Protector(SecretPurpose, organizationId).Protect(value);

    public string UnprotectSecret(Guid organizationId, string payload) => Protector(SecretPurpose, organizationId).Unprotect(payload);

    public string ProtectRecord(Guid organizationId, string json) => Protector(RecordPurpose, organizationId).Protect(json);

    public string UnprotectRecord(Guid organizationId, string payload) => Protector(RecordPurpose, organizationId).Unprotect(payload);

    private IDataProtector Protector(string purpose, Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        return provider.CreateProtector("AutomataFlow", purpose, organizationId.ToString("N"));
    }
}

public sealed record SecretSummary(string Name, DateTimeOffset UpdatedAt);

/// <summary>Organization secrets: write-only through the API (values are never returned), names listed for authoring.</summary>
public sealed class SecretService(AutomataFlowDbContext db, ValueProtection protection, TimeProvider clock)
{
    public const int MaxValueLength = 4_096;

    public async Task<IReadOnlyList<SecretSummary>> ListAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.OrganizationSecrets.AsNoTracking().Where(s => s.OrganizationId == organizationId)
            .OrderBy(s => s.Name).Select(s => new SecretSummary(s.Name, s.UpdatedAt)).ToListAsync(cancellationToken);

    /// <summary>Creates or replaces a secret. False when the name or value is invalid.</summary>
    public async Task<bool> SetAsync(Guid organizationId, string name, string value, CancellationToken cancellationToken)
    {
        if (!OrganizationSecret.IsValidName(name) || string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var payload = protection.ProtectSecret(organizationId, value);
        var existing = await db.OrganizationSecrets.SingleOrDefaultAsync(s => s.OrganizationId == organizationId && s.Name == name, cancellationToken);
        if (existing is null)
        {
            db.OrganizationSecrets.Add(OrganizationSecret.Create(organizationId, name, payload, now));
        }
        else
        {
            existing.Replace(payload, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid organizationId, string name, CancellationToken cancellationToken) =>
        await db.OrganizationSecrets.Where(s => s.OrganizationId == organizationId && s.Name == name).ExecuteDeleteAsync(cancellationToken) == 1;
}

/// <summary>
/// The engine's <see cref="ISecretProvider"/>: reads the secret of the attempt's own organization only and decrypts it
/// in memory for the attempt (never logged, never persisted elsewhere).
/// </summary>
public sealed class OrganizationSecretProvider(AutomataFlowDbContext db, ValueProtection protection) : ISecretProvider
{
    public async Task<string> GetAsync(ExecutionScope scope, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var payload = await db.OrganizationSecrets.AsNoTracking()
            .Where(s => s.OrganizationId == scope.OrganizationId && s.Name == name)
            .Select(s => s.ProtectedValue)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("The secret is not defined for this organization.");
        return protection.UnprotectSecret(scope.OrganizationId, payload);
    }
}
