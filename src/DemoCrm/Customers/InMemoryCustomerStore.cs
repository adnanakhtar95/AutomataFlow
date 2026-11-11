namespace DemoCrm.Customers;

/// <summary>
/// Thread-safe in-memory store. A single lock makes registration (idempotency check, duplicate checks and insert)
/// atomic, which is what lets automated retries be proven free of duplicate side effects.
/// </summary>
public sealed class InMemoryCustomerStore : ICustomerStore
{
    private readonly Lock _gate = new();
    private readonly List<Customer> _customers = [];
    private readonly Dictionary<string, Customer> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Customer> _byPhone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Customer> _byOperation = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;
    private int _seededCount;

    public InMemoryCustomerStore()
    {
        Reset();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _customers.Count;
            }
        }
    }

    public int SeededCount
    {
        get
        {
            lock (_gate)
            {
                return _seededCount;
            }
        }
    }

    public RegistrationOutcome Register(NewCustomer customer, string? operationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var operationKey = string.IsNullOrWhiteSpace(operationId) ? null : operationId.Trim();

        lock (_gate)
        {
            if (operationKey is not null && _byOperation.TryGetValue(operationKey, out var original))
            {
                return string.Equals(original.Email, customer.Email, StringComparison.OrdinalIgnoreCase)
                    ? new RegistrationOutcome(RegistrationStatus.Replayed, original)
                    : new RegistrationOutcome(RegistrationStatus.OperationConflict, original);
            }

            if (_byEmail.TryGetValue(customer.Email, out var sameEmail))
            {
                return new RegistrationOutcome(RegistrationStatus.DuplicateEmail, sameEmail);
            }

            var phoneKey = CustomerInputValidator.PhoneDigits(customer.Phone);
            if (_byPhone.TryGetValue(phoneKey, out var samePhone))
            {
                return new RegistrationOutcome(RegistrationStatus.DuplicatePhone, samePhone);
            }

            var created = new Customer(
                ++_nextId,
                customer.FirstName,
                customer.LastName,
                customer.Email,
                customer.Phone,
                customer.Company,
                customer.Country,
                customer.CustomerType,
                now,
                operationKey,
                IsSeeded: false);
            Add(created);
            return new RegistrationOutcome(RegistrationStatus.Created, created);
        }
    }

    public Customer? Find(int id)
    {
        lock (_gate)
        {
            return _customers.FirstOrDefault(c => c.Id == id);
        }
    }

    public PagedResult<Customer> List(string? filter, int page, int pageSize)
    {
        var size = Math.Clamp(pageSize, 1, 100);
        var term = filter?.Trim();

        lock (_gate)
        {
            var query = string.IsNullOrEmpty(term)
                ? _customers.AsEnumerable()
                : _customers.Where(c => Matches(c, CustomerSearchField.Any, term));
            var ordered = query.OrderBy(c => c.Id).ToList();
            var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)size));
            var currentPage = Math.Clamp(page, 1, totalPages);
            var items = ordered.Skip((currentPage - 1) * size).Take(size).ToList();
            return new PagedResult<Customer>(items, currentPage, size, ordered.Count);
        }
    }

    public IReadOnlyList<Customer> Search(CustomerSearchField field, string term)
    {
        var text = term?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        lock (_gate)
        {
            return _customers.Where(c => Matches(c, field, text)).OrderBy(c => c.Id).ToList();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _customers.Clear();
            _byEmail.Clear();
            _byPhone.Clear();
            _byOperation.Clear();
            _nextId = 0;

            foreach (var seed in SeedData.Create())
            {
                Add(seed with { Id = ++_nextId });
            }

            _seededCount = _customers.Count;
        }
    }

    private void Add(Customer customer)
    {
        _customers.Add(customer);
        _byEmail[customer.Email] = customer;
        _byPhone[CustomerInputValidator.PhoneDigits(customer.Phone)] = customer;
        if (customer.OperationId is not null)
        {
            _byOperation[customer.OperationId] = customer;
        }
    }

    private static bool Matches(Customer customer, CustomerSearchField field, string term)
    {
        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

        return field switch
        {
            CustomerSearchField.Email => Contains(customer.Email),
            CustomerSearchField.Name => Contains(customer.FullName),
            CustomerSearchField.Phone => CustomerInputValidator.PhoneDigits(customer.Phone)
                .Contains(CustomerInputValidator.PhoneDigits(term), StringComparison.Ordinal)
                && CustomerInputValidator.PhoneDigits(term).Length > 0,
            CustomerSearchField.Id => Customer.TryParseId(term, out var id) && customer.Id == id,
            _ => Contains(customer.Email) || Contains(customer.FullName) || Contains(customer.Company)
                || Contains(customer.DisplayId),
        };
    }
}
