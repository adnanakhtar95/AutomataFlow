using System.Text.RegularExpressions;

namespace DemoCrm.Customers;

/// <summary>Server-side validation rules for the registration form. Messages are stable so automation can assert on them.</summary>
public static partial class CustomerInputValidator
{
    public const int NameMaxLength = 60;
    public const int EmailMaxLength = 120;
    public const int CompanyMaxLength = 80;

    public static readonly IReadOnlyList<string> Countries =
        ["Australia", "Canada", "Germany", "India", "United Kingdom", "United States"];

    public static readonly IReadOnlyList<string> CustomerTypes = ["Standard", "Premium"];

    public static IReadOnlyList<ValidationFailure> Validate(CustomerInput input, out NewCustomer? customer)
    {
        ArgumentNullException.ThrowIfNull(input);

        var failures = new List<ValidationFailure>();

        var firstName = (input.FirstName ?? string.Empty).Trim();
        var lastName = (input.LastName ?? string.Empty).Trim();
        var email = (input.Email ?? string.Empty).Trim();
        var phone = (input.Phone ?? string.Empty).Trim();
        var company = string.IsNullOrWhiteSpace(input.Company) ? null : input.Company.Trim();
        var country = (input.Country ?? string.Empty).Trim();
        var customerType = (input.CustomerType ?? string.Empty).Trim();

        ValidateName(failures, nameof(CustomerInput.FirstName), "First name", firstName);
        ValidateName(failures, nameof(CustomerInput.LastName), "Last name", lastName);

        if (email.Length == 0)
        {
            failures.Add(new(nameof(CustomerInput.Email), "Email is required."));
        }
        else if (email.Length > EmailMaxLength || !EmailPattern().IsMatch(email))
        {
            failures.Add(new(nameof(CustomerInput.Email), "Enter a valid email address."));
        }

        if (phone.Length == 0)
        {
            failures.Add(new(nameof(CustomerInput.Phone), "Phone is required."));
        }
        else if (!PhonePattern().IsMatch(phone) || PhoneDigits(phone).Length is < 7 or > 15)
        {
            failures.Add(new(nameof(CustomerInput.Phone), "Enter a valid phone number (7-15 digits)."));
        }

        if (company is { Length: > CompanyMaxLength })
        {
            failures.Add(new(nameof(CustomerInput.Company), $"Company must be at most {CompanyMaxLength} characters."));
        }

        if (country.Length == 0)
        {
            failures.Add(new(nameof(CustomerInput.Country), "Country is required."));
        }
        else if (!Countries.Contains(country, StringComparer.Ordinal))
        {
            failures.Add(new(nameof(CustomerInput.Country), "Select a country from the list."));
        }

        if (customerType.Length == 0)
        {
            failures.Add(new(nameof(CustomerInput.CustomerType), "Customer type is required."));
        }
        else if (!CustomerTypes.Contains(customerType, StringComparer.Ordinal))
        {
            failures.Add(new(nameof(CustomerInput.CustomerType), "Select a customer type from the list."));
        }

        if (!input.AcceptTerms)
        {
            failures.Add(new(nameof(CustomerInput.AcceptTerms), "You must confirm that the data is accurate."));
        }

        var operationId = input.OperationId?.Trim();
        if (!string.IsNullOrEmpty(operationId) && !Guid.TryParse(operationId, out _))
        {
            failures.Add(new(nameof(CustomerInput.OperationId), "Operation reference must be a valid GUID."));
        }

        customer = failures.Count == 0
            ? new NewCustomer(firstName, lastName, email.ToLowerInvariant(), phone, company, country, customerType)
            : null;
        return failures;
    }

    /// <summary>Digits only; used to detect duplicate phone numbers regardless of formatting.</summary>
    public static string PhoneDigits(string phone) => new(phone.Where(char.IsAsciiDigit).ToArray());

    private static void ValidateName(List<ValidationFailure> failures, string field, string label, string value)
    {
        if (value.Length == 0)
        {
            failures.Add(new(field, $"{label} is required."));
        }
        else if (value.Length > NameMaxLength)
        {
            failures.Add(new(field, $"{label} must be at most {NameMaxLength} characters."));
        }
        else if (!NamePattern().IsMatch(value))
        {
            failures.Add(new(field, $"{label} contains invalid characters."));
        }
    }

    [GeneratedRegex(@"^[\p{L}][\p{L} '\-\.]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+?[0-9 ()\-\.]+$")]
    private static partial Regex PhonePattern();
}
