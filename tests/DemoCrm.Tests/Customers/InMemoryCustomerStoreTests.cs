using DemoCrm.Customers;

namespace DemoCrm.Tests.Customers;

public class InMemoryCustomerStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static NewCustomer NewCustomer(string email = "new.person@example.test", string phone = "+1 555 9000") =>
        new("New", "Person", email, phone, "Test Co", "Canada", "Standard");

    [Fact]
    public void Store_starts_with_the_deterministic_seed()
    {
        var store = new InMemoryCustomerStore();

        Assert.Equal(24, store.Count);
        Assert.Equal(24, store.SeededCount);
        Assert.Equal("C-000001", store.Find(1)!.DisplayId);
        Assert.True(store.Find(1)!.IsSeeded);
    }

    [Fact]
    public void Register_creates_a_customer_with_the_next_id()
    {
        var store = new InMemoryCustomerStore();

        var outcome = store.Register(NewCustomer(), operationId: null, Now);

        Assert.Equal(RegistrationStatus.Created, outcome.Status);
        Assert.Equal(25, outcome.Customer.Id);
        Assert.False(outcome.Customer.IsSeeded);
        Assert.Equal(Now, outcome.Customer.CreatedAt);
        Assert.Equal(25, store.Count);
        Assert.Equal(24, store.SeededCount);
    }

    [Fact]
    public void Duplicate_email_is_detected_case_insensitively_and_names_the_existing_customer()
    {
        var store = new InMemoryCustomerStore();
        var seeded = store.Find(1)!;

        var outcome = store.Register(NewCustomer(email: seeded.Email.ToUpperInvariant()), null, Now);

        Assert.Equal(RegistrationStatus.DuplicateEmail, outcome.Status);
        Assert.Equal(seeded.Id, outcome.Customer.Id);
        Assert.Equal(24, store.Count);
    }

    [Theory]
    [InlineData("+1 555 0105")]
    [InlineData("1-555-0105")]
    [InlineData("(1) 555.0105")]
    public void Duplicate_phone_is_detected_regardless_of_formatting(string phone)
    {
        var store = new InMemoryCustomerStore();

        var outcome = store.Register(NewCustomer(phone: phone), null, Now);

        Assert.Equal(RegistrationStatus.DuplicatePhone, outcome.Status);
        Assert.Equal(24, store.Count);
    }

    [Fact]
    public void Replaying_an_operation_returns_the_original_customer_without_creating_another()
    {
        var store = new InMemoryCustomerStore();
        var operation = Guid.NewGuid().ToString("D");
        var first = store.Register(NewCustomer(), operation, Now);

        var second = store.Register(NewCustomer(), operation, Now.AddMinutes(1));

        Assert.Equal(RegistrationStatus.Created, first.Status);
        Assert.Equal(RegistrationStatus.Replayed, second.Status);
        Assert.Equal(first.Customer.Id, second.Customer.Id);
        Assert.Equal(25, store.Count);
    }

    [Fact]
    public void Reusing_an_operation_id_with_different_data_is_a_conflict()
    {
        var store = new InMemoryCustomerStore();
        var operation = Guid.NewGuid().ToString("D");
        store.Register(NewCustomer(), operation, Now);

        var outcome = store.Register(NewCustomer(email: "someone.else@example.test", phone: "+1 555 9001"), operation, Now);

        Assert.Equal(RegistrationStatus.OperationConflict, outcome.Status);
        Assert.Equal(25, store.Count);
    }

    [Fact]
    public void Same_data_with_a_different_operation_id_is_a_duplicate_not_a_replay()
    {
        var store = new InMemoryCustomerStore();
        store.Register(NewCustomer(), Guid.NewGuid().ToString("D"), Now);

        var outcome = store.Register(NewCustomer(), Guid.NewGuid().ToString("D"), Now);

        Assert.Equal(RegistrationStatus.DuplicateEmail, outcome.Status);
    }

    [Fact]
    public void Concurrent_registrations_of_one_email_create_exactly_one_customer()
    {
        var store = new InMemoryCustomerStore();

        var outcomes = Enumerable.Range(0, 50)
            .AsParallel().WithDegreeOfParallelism(16)
            .Select(i => store.Register(NewCustomer(phone: $"+1 555 {7000 + i}"), Guid.NewGuid().ToString("D"), Now))
            .ToList();

        Assert.Equal(1, outcomes.Count(o => o.Status == RegistrationStatus.Created));
        Assert.Equal(49, outcomes.Count(o => o.Status == RegistrationStatus.DuplicateEmail));
        Assert.Equal(25, store.Count);
    }

    [Fact]
    public void Concurrent_replays_of_one_operation_create_exactly_one_customer()
    {
        var store = new InMemoryCustomerStore();
        var operation = Guid.NewGuid().ToString("D");

        var outcomes = Enumerable.Range(0, 40)
            .AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => store.Register(NewCustomer(), operation, Now))
            .ToList();

        Assert.Equal(1, outcomes.Count(o => o.Status == RegistrationStatus.Created));
        Assert.Equal(39, outcomes.Count(o => o.Status == RegistrationStatus.Replayed));
        Assert.Equal(25, store.Count);
    }

    [Fact]
    public void Reset_discards_registrations_and_restores_the_seed_and_ids()
    {
        var store = new InMemoryCustomerStore();
        var operation = Guid.NewGuid().ToString("D");
        store.Register(NewCustomer(), operation, Now);

        store.Reset();

        Assert.Equal(24, store.Count);
        Assert.Null(store.Find(25));
        var again = store.Register(NewCustomer(), operation, Now);
        Assert.Equal(RegistrationStatus.Created, again.Status);
        Assert.Equal(25, again.Customer.Id);
    }

    [Fact]
    public void List_pages_in_id_order_and_clamps_the_page_number()
    {
        var store = new InMemoryCustomerStore();

        var first = store.List(null, 1, 10);
        var last = store.List(null, 99, 10);

        Assert.Equal(24, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(Enumerable.Range(1, 10), first.Items.Select(c => c.Id));
        Assert.Equal(3, last.Page);
        Assert.Equal(Enumerable.Range(21, 4), last.Items.Select(c => c.Id));
    }

    [Fact]
    public void List_filter_matches_name_email_company_and_id()
    {
        var store = new InMemoryCustomerStore();

        Assert.Single(store.List("morgan", 1, 10).Items);
        Assert.Single(store.List("ALICE.MORGAN@EXAMPLE.TEST", 1, 10).Items);
        Assert.Single(store.List("C-000003", 1, 10).Items);
        Assert.Empty(store.List("no-such-customer", 1, 10).Items);
    }

    [Fact]
    public void Search_by_field_finds_only_matching_customers()
    {
        var store = new InMemoryCustomerStore();

        Assert.Equal([1], store.Search(CustomerSearchField.Email, "alice.morgan").Select(c => c.Id));
        Assert.Equal([2], store.Search(CustomerSearchField.Name, "brian fletcher").Select(c => c.Id));
        Assert.Equal([6], store.Search(CustomerSearchField.Phone, "(555) 0105").Select(c => c.Id));
        Assert.Equal([7], store.Search(CustomerSearchField.Id, "c-7").Select(c => c.Id));
        Assert.Empty(store.Search(CustomerSearchField.Email, "Brian Fletcher"));
        Assert.Empty(store.Search(CustomerSearchField.Name, "example.test"));
        Assert.Empty(store.Search(CustomerSearchField.Any, "   "));
        Assert.Empty(store.Search(CustomerSearchField.Phone, "abc"));
    }
}
