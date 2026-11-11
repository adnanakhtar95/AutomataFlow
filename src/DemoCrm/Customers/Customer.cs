namespace DemoCrm.Customers;

/// <summary>A fictional customer record. All data in DemoCrm is invented.</summary>
public sealed record Customer(
    int Id,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string? Company,
    string Country,
    string CustomerType,
    DateTimeOffset CreatedAt,
    string? OperationId,
    bool IsSeeded)
{
    /// <summary>Human-facing identifier, e.g. <c>C-000012</c>.</summary>
    public string DisplayId => FormatId(Id);

    public string FullName => $"{FirstName} {LastName}";

    public static string FormatId(int id) => $"C-{id:000000}";

    /// <summary>Accepts <c>C-000012</c>, <c>c-12</c> or <c>12</c>.</summary>
    public static bool TryParseId(string? text, out int id)
    {
        id = 0;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith("C-", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
