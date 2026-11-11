using System.Net;
using AutomataFlow.IntegrationTests.Support;

namespace AutomataFlow.IntegrationTests.Api;

public class HealthEndpointTests
{
    [Fact]
    public async Task Liveness_is_healthy_even_when_the_database_is_unreachable()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_the_database_is_unreachable_and_does_not_leak_details()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_is_healthy_when_the_database_is_reachable()
    {
        await using var database = await TestDatabase.CreateAsync(CancellationToken.None);
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
