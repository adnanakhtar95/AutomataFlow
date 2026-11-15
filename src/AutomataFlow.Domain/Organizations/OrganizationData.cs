using System.Text.RegularExpressions;
using AutomataFlow.Domain.Common;

namespace AutomataFlow.Domain.Organizations;

/// <summary>
/// A named workflow secret of an organization (e.g. a target-site password). Only the Data Protection payload is
/// stored; the value is never returned by the API and is decrypted only inside a Worker attempt (docs/ARCHITECTURE.md §7).
/// </summary>
public sealed partial class OrganizationSecret : IOrganizationOwned
{
    public const int NameMaxLength = 64;

    private OrganizationSecret()
    {
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string ProtectedValue { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static OrganizationSecret Create(Guid organizationId, string name, string protectedValue, DateTimeOffset at)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException("A secret name must be a declared name (letter, then letters and digits, at most 64).", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        return new OrganizationSecret { OrganizationId = organizationId, Name = name, ProtectedValue = protectedValue, CreatedAt = at, UpdatedAt = at };
    }

    public static bool IsValidName(string? name) => name is { Length: > 0 and <= NameMaxLength } && NamePattern().IsMatch(name);

    public void Replace(string protectedValue, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        ProtectedValue = protectedValue;
        UpdatedAt = at < UpdatedAt ? UpdatedAt : at;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex NamePattern();
}

/// <summary>An integration (webhook etc.). Schema only in the MVP; configuration is stored protected.</summary>
public sealed class Integration : IOrganizationOwned
{
    private Integration()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string ProtectedConfig { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Integration Create(Guid organizationId, string type, string name, string protectedConfig, DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedConfig);
        return new Integration { Id = Guid.CreateVersion7(), OrganizationId = organizationId, Type = type, Name = name, ProtectedConfig = protectedConfig, CreatedAt = createdAt };
    }
}
