namespace DemoCrm.Customers;

public enum RegistrationStatus
{
    /// <summary>A new customer was stored.</summary>
    Created,

    /// <summary>The same operation id was already processed with the same data; the original customer is returned.</summary>
    Replayed,

    /// <summary>The operation id was already used with different data.</summary>
    OperationConflict,

    /// <summary>Another customer already has this email address.</summary>
    DuplicateEmail,

    /// <summary>Another customer already has this phone number.</summary>
    DuplicatePhone,
}

/// <param name="Customer">The created, replayed, original or conflicting existing customer.</param>
public sealed record RegistrationOutcome(RegistrationStatus Status, Customer Customer);

public enum CustomerSearchField
{
    Any,
    Email,
    Name,
    Phone,
    Id,
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

/// <summary>Storage seam for the demo portal. Implementations must make <see cref="Register"/> atomic.</summary>
public interface ICustomerStore
{
    int Count { get; }

    int SeededCount { get; }

    /// <summary>Atomically checks idempotency and duplicates, then stores the customer.</summary>
    RegistrationOutcome Register(NewCustomer customer, string? operationId, DateTimeOffset now);

    Customer? Find(int id);

    PagedResult<Customer> List(string? filter, int page, int pageSize);

    IReadOnlyList<Customer> Search(CustomerSearchField field, string term);

    /// <summary>Discards all data and restores the deterministic seed.</summary>
    void Reset();
}
