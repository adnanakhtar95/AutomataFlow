using AutomataFlow.Domain.Organizations;

namespace AutomataFlow.UnitTests.Domain;

public class OrganizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_normalizes_name_and_slug_and_assigns_id()
    {
        var org = Organization.Create("  Acme Corp  ", "  Acme-Corp ", Now);

        Assert.NotEqual(Guid.Empty, org.Id);
        Assert.Equal("Acme Corp", org.Name);
        Assert.Equal("acme-corp", org.Slug);
        Assert.Equal(Now, org.CreatedAt);
    }

    [Fact]
    public void Create_generates_distinct_ids()
    {
        var first = Organization.Create("One", "one", Now);
        var second = Organization.Create("Two", "two", Now);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Organization.Create(name, "acme", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("ac me")]
    [InlineData("acme_corp")]
    [InlineData("acme.corp")]
    [InlineData("../acme")]
    public void Create_rejects_invalid_slug(string slug)
    {
        Assert.ThrowsAny<ArgumentException>(() => Organization.Create("Acme", slug, Now));
    }

    [Fact]
    public void Create_rejects_overlong_values()
    {
        Assert.Throws<ArgumentException>(() => Organization.Create(new string('a', Organization.NameMaxLength + 1), "acme", Now));
        Assert.Throws<ArgumentException>(() => Organization.Create("Acme", new string('a', Organization.SlugMaxLength + 1), Now));
    }
}
