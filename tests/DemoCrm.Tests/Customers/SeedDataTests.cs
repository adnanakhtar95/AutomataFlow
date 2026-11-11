using DemoCrm.Customers;

namespace DemoCrm.Tests.Customers;

public class SeedDataTests
{
    private static List<Customer> Seed() => SeedData.Create().ToList();

    [Fact]
    public void Seed_is_deterministic()
    {
        Assert.Equal(Seed(), Seed());
    }

    [Fact]
    public void Seed_uses_only_reserved_fictional_email_domains_and_unique_keys()
    {
        var seed = Seed();

        Assert.Equal(24, seed.Count);
        Assert.All(seed, c => Assert.EndsWith("@example.test", c.Email, StringComparison.Ordinal));
        Assert.Equal(seed.Count, seed.Select(c => c.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(seed.Count, seed.Select(c => CustomerInputValidator.PhoneDigits(c.Phone)).Distinct().Count());
    }

    [Fact]
    public void Seed_data_passes_the_registration_validation_rules()
    {
        foreach (var customer in Seed())
        {
            var input = new CustomerInput
            {
                FirstName = customer.FirstName,
                LastName = customer.LastName,
                Email = customer.Email,
                Phone = customer.Phone,
                Company = customer.Company,
                Country = customer.Country,
                CustomerType = customer.CustomerType,
                AcceptTerms = true,
            };

            Assert.Empty(CustomerInputValidator.Validate(input, out _));
        }
    }
}
