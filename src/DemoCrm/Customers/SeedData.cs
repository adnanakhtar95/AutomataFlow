namespace DemoCrm.Customers;

/// <summary>Deterministic, entirely fictional seed data (reserved .test domain and 555-01xx style numbers).</summary>
public static class SeedData
{
    private const int Count = 24;

    private static readonly string[] FirstNames =
    [
        "Alice", "Brian", "Chloe", "Daniel", "Elena", "Farid", "Grace", "Hector",
        "Isla", "Jamal", "Keira", "Liam", "Maya", "Noah", "Olivia", "Pedro",
        "Quinn", "Rosa", "Samir", "Tara", "Umar", "Vera", "Wes", "Zoe",
    ];

    private static readonly string[] LastNames =
    [
        "Morgan", "Fletcher", "Nakamura", "Okafor", "Petrova", "Haddad", "Lindqvist", "Alvarez",
        "Brennan", "Carter", "Dubois", "Eriksen", "Fontaine", "Gallagher", "Hartmann", "Ibrahim",
        "Jensen", "Kowalski", "Laurent", "Marchetti", "Novak", "Ortega", "Patel", "Rahman",
    ];

    private static readonly string[] Companies =
    [
        "Brightwater Supplies", "Copperleaf Studio", "Driftwood Logistics", "Evergreen Labs",
        "Foxglove Foods", "Granite Peak Tools", "Harborlight Media", "Ironbark Consulting",
    ];

    private static readonly DateTimeOffset BaseDate = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Seed customers; ids are assigned by the store.</summary>
    public static IEnumerable<Customer> Create()
    {
        for (var i = 0; i < Count; i++)
        {
            var first = FirstNames[i];
            var last = LastNames[i];
            yield return new Customer(
                Id: 0,
                FirstName: first,
                LastName: last,
                Email: $"{first}.{last}@example.test".ToLowerInvariant(),
                Phone: $"+1 555 01{i:00}",
                Company: i % 3 == 2 ? null : Companies[i % Companies.Length],
                Country: CustomerInputValidator.Countries[i % CustomerInputValidator.Countries.Count],
                CustomerType: i % 4 == 0 ? "Premium" : "Standard",
                CreatedAt: BaseDate.AddDays(i),
                OperationId: null,
                IsSeeded: true);
        }
    }
}
