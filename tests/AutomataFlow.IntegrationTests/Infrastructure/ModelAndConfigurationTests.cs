using AutomataFlow.Domain.Common;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.IntegrationTests.Infrastructure;

public class ModelAndConfigurationTests
{
    private static AutomataFlowDbContext CreateContextWithoutConnecting()
    {
        // Building the EF model never opens a connection.
        var options = new DbContextOptionsBuilder<AutomataFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        return new AutomataFlowDbContext(options);
    }

    [Fact]
    public void Every_entity_with_an_OrganizationId_is_marked_IOrganizationOwned()
    {
        using var context = CreateContextWithoutConnecting();

        var ownedLooking = context.Model.GetEntityTypes()
            .Where(e => e.FindProperty("OrganizationId") is not null)
            .Select(e => e.ClrType)
            .ToList();

        Assert.NotEmpty(ownedLooking);
        Assert.All(ownedLooking, type => Assert.True(
            typeof(IOrganizationOwned).IsAssignableFrom(type),
            $"{type.Name} has an OrganizationId but does not implement IOrganizationOwned."));
    }

    [Fact]
    public void Every_IOrganizationOwned_entity_in_the_model_has_a_mapped_OrganizationId()
    {
        using var context = CreateContextWithoutConnecting();

        var owned = context.Model.GetEntityTypes()
            .Where(e => typeof(IOrganizationOwned).IsAssignableFrom(e.ClrType))
            .ToList();

        Assert.Contains(owned, e => e.ClrType == typeof(OrganizationMember));
        Assert.All(owned, e => Assert.NotNull(e.FindProperty(nameof(IOrganizationOwned.OrganizationId))));
    }

    [Fact]
    public void AddInfrastructure_fails_fast_with_a_clear_message_when_no_connection_string_is_configured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(configuration));

        Assert.Contains("ConnectionStrings__Default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddInfrastructure_registers_the_DbContext_when_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost;Database=x" })
            .Build();
        var services = new ServiceCollection().AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<AutomataFlowDbContext>());
    }
}
