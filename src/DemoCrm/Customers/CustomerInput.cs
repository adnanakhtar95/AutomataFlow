namespace DemoCrm.Customers;

/// <summary>Raw values as submitted by the registration form.</summary>
public sealed class CustomerInput
{
    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string? Company { get; set; }

    public string Country { get; set; } = string.Empty;

    public string CustomerType { get; set; } = string.Empty;

    public bool AcceptTerms { get; set; }

    /// <summary>Optional client-supplied operation identifier (GUID) making the submission idempotent.</summary>
    public string? OperationId { get; set; }
}

/// <summary>Validated, normalized values ready to be stored.</summary>
public sealed record NewCustomer(
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string? Company,
    string Country,
    string CustomerType);

public sealed record ValidationFailure(string Field, string Message);
