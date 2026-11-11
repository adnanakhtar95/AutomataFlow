using System.Net;
using System.Net.Http.Json;
using AutomataFlow.Api.Contracts;
using AutomataFlow.IntegrationTests.Support;

namespace AutomataFlow.IntegrationTests.Api;

public class SystemAndDocsEndpointTests
{
    [Fact]
    public async Task System_info_returns_service_identification_as_a_dto()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<SystemInfoDto>("/api/v1/system/info", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("AutomataFlow.Api", info.Name);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Fact]
    public async Task Swagger_document_is_served_in_development_and_describes_the_system_endpoint()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, "Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", CancellationToken.None);
        var json = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/system/info", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swagger_is_not_exposed_outside_development()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase, "Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/does-not-exist", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
