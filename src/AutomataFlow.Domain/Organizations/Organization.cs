using System.Text.RegularExpressions;

namespace AutomataFlow.Domain.Organizations;

/// <summary>The tenant root. Not itself organization-owned data.</summary>
public sealed partial class Organization
{
    public const int NameMaxLength = 200;
    public const int SlugMaxLength = 63;

    private Organization()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Lowercase URL-safe identifier, unique across the platform.</summary>
    public string Slug { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Organization Create(string name, string slug, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var trimmedName = name.Trim();
        if (trimmedName.Length > NameMaxLength)
        {
            throw new ArgumentException($"Organization name cannot exceed {NameMaxLength} characters.", nameof(name));
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();
        if (normalizedSlug.Length > SlugMaxLength || !SlugPattern().IsMatch(normalizedSlug))
        {
            throw new ArgumentException(
                "Slug must be 1-63 characters of lowercase letters, digits and single hyphens, and cannot start or end with a hyphen.",
                nameof(slug));
        }

        return new Organization
        {
            Id = Guid.CreateVersion7(),
            Name = trimmedName,
            Slug = normalizedSlug,
            CreatedAt = createdAt,
        };
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
