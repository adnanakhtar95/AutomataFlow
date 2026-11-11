using DemoCrm.Customers;

namespace DemoCrm.Tests.Customers;

public class CustomerInputValidatorTests
{
    private static CustomerInput ValidInput() => new()
    {
        FirstName = "Ada",
        LastName = "Lovelace",
        Email = "ada.lovelace@example.test",
        Phone = "+1 555 0100",
        Company = "Analytical Engines",
        Country = "United Kingdom",
        CustomerType = "Premium",
        AcceptTerms = true,
    };

    [Fact]
    public void Valid_input_produces_a_normalized_customer_and_no_failures()
    {
        var input = ValidInput();
        input.FirstName = "  Ada ";
        input.Email = "  Ada.Lovelace@Example.TEST ";
        input.Company = "   ";

        var failures = CustomerInputValidator.Validate(input, out var customer);

        Assert.Empty(failures);
        Assert.NotNull(customer);
        Assert.Equal("Ada", customer.FirstName);
        Assert.Equal("ada.lovelace@example.test", customer.Email);
        Assert.Null(customer.Company);
    }

    [Theory]
    [InlineData("Zoë", "O'Brien")]
    [InlineData("Anne-Marie", "van der Berg")]
    [InlineData("José", "Müller")]
    public void Names_with_letters_apostrophes_hyphens_and_spaces_are_accepted(string first, string last)
    {
        var input = ValidInput();
        input.FirstName = first;
        input.LastName = last;

        Assert.Empty(CustomerInputValidator.Validate(input, out _));
    }

    [Theory]
    [InlineData("+1 (555) 010-1234")]
    [InlineData("555.010.1234")]
    [InlineData("+44 20 7946 0958")]
    public void Common_phone_formats_are_accepted(string phone)
    {
        var input = ValidInput();
        input.Phone = phone;

        Assert.Empty(CustomerInputValidator.Validate(input, out _));
    }

    public static TheoryData<string, Action<CustomerInput>, string> InvalidCases => new()
    {
        { "FirstName", i => i.FirstName = "", "First name is required." },
        { "FirstName", i => i.FirstName = new string('a', 61), "First name must be at most 60 characters." },
        { "LastName", i => i.LastName = "12345", "Last name contains invalid characters." },
        { "LastName", i => i.LastName = "<script>", "Last name contains invalid characters." },
        { "Email", i => i.Email = "", "Email is required." },
        { "Email", i => i.Email = "not-an-email", "Enter a valid email address." },
        { "Email", i => i.Email = "missing@tld", "Enter a valid email address." },
        { "Email", i => i.Email = "two@@example.test", "Enter a valid email address." },
        { "Phone", i => i.Phone = "", "Phone is required." },
        { "Phone", i => i.Phone = "abc", "Enter a valid phone number (7-15 digits)." },
        { "Phone", i => i.Phone = "123456", "Enter a valid phone number (7-15 digits)." },
        { "Phone", i => i.Phone = "1234567890123456", "Enter a valid phone number (7-15 digits)." },
        { "Company", i => i.Company = new string('c', 81), "Company must be at most 80 characters." },
        { "Country", i => i.Country = "", "Country is required." },
        { "Country", i => i.Country = "Atlantis", "Select a country from the list." },
        { "CustomerType", i => i.CustomerType = "", "Customer type is required." },
        { "CustomerType", i => i.CustomerType = "Platinum", "Select a customer type from the list." },
        { "AcceptTerms", i => i.AcceptTerms = false, "You must confirm that the data is accurate." },
        { "OperationId", i => i.OperationId = "not-a-guid", "Operation reference must be a valid GUID." },
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_input_is_rejected_with_the_expected_message(string field, Action<CustomerInput> mutate, string expectedMessage)
    {
        var input = ValidInput();
        mutate(input);

        var failures = CustomerInputValidator.Validate(input, out var customer);

        Assert.Null(customer);
        var failure = Assert.Single(failures);
        Assert.Equal(field, failure.Field);
        Assert.Equal(expectedMessage, failure.Message);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var failures = CustomerInputValidator.Validate(new CustomerInput(), out var customer);

        Assert.Null(customer);
        Assert.Equal(
            ["FirstName", "LastName", "Email", "Phone", "Country", "CustomerType", "AcceptTerms"],
            failures.Select(f => f.Field));
    }

    [Theory]
    [InlineData("+1 (555) 010-1234", "15550101234")]
    [InlineData("555.010.1234", "5550101234")]
    public void PhoneDigits_strips_formatting(string phone, string expected)
    {
        Assert.Equal(expected, CustomerInputValidator.PhoneDigits(phone));
    }
}
