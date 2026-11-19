using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.IntegrationTests.Support;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 6 step 2: the browser session transport (ADR-077) and the dashboard's read endpoints.</summary>
public sealed class BrowserSessionTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    private const string Header = "X-AutomataFlow-Session";
    private const string Cookie = "__Secure-af_refresh";

    private HttpClient Browser()
    {
        var client = api.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(Header, "browser");
        return client;
    }

    private static string SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Single(v => v.StartsWith(Cookie + "=", StringComparison.Ordinal)) : string.Empty;

    private static string CookieValue(string setCookie) => setCookie[(Cookie.Length + 1)..setCookie.IndexOf(';', StringComparison.Ordinal)];

    private static HttpRequestMessage WithCookie(string path, string value, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{Cookie}={value}");
        if (!header)
        {
            request.Headers.Remove(Header);
        }

        return request;
    }

    [Fact]
    public async Task Browser_sign_in_puts_the_refresh_token_only_in_a_strict_HttpOnly_cookie_scoped_to_auth()
    {
        var (email, _) = await api.RegisterAsync();
        var response = await Browser().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Null(body["refreshToken"]);
        Assert.False(string.IsNullOrEmpty(body["accessToken"]!.GetValue<string>()));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var setCookie = SetCookie(response).ToLowerInvariant();
        Assert.Contains("httponly", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cookie_rotates_on_refresh_reuse_is_refused_and_clears_it_and_it_needs_the_session_header()
    {
        var (email, _) = await api.RegisterAsync();
        var first = CookieValue(SetCookie(await Browser().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password })));

        // Without the custom header (what a cross-site form post would look like) the cookie is ignored.
        using (var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh"))
        {
            noHeader.Headers.Add("Cookie", $"{Cookie}={first}");
            var plain = api.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
            Assert.Equal(HttpStatusCode.BadRequest, (await plain.SendAsync(noHeader)).StatusCode);
        }

        var rotated = await Browser().SendAsync(WithCookie("/api/v1/auth/refresh", first));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = CookieValue(SetCookie(rotated));
        Assert.NotEqual(first, second);
        Assert.Null(JsonNode.Parse(await rotated.Content.ReadAsStringAsync())!["refreshToken"]);

        // Replaying the rotated cookie revokes the family and tells the browser to drop the cookie.
        var replay = await Browser().SendAsync(WithCookie("/api/v1/auth/refresh", first));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("expires=thu, 01 jan 1970", SetCookie(replay).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser().SendAsync(WithCookie("/api/v1/auth/refresh", second))).StatusCode);
    }

    [Fact]
    public async Task Browser_logout_revokes_the_session_and_clears_the_cookie()
    {
        var (email, _) = await api.RegisterAsync();
        var value = CookieValue(SetCookie(await Browser().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password })));

        var logout = await Browser().SendAsync(WithCookie("/api/v1/auth/logout", value));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=thu, 01 jan 1970", SetCookie(logout).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser().SendAsync(WithCookie("/api/v1/auth/refresh", value))).StatusCode);
    }

    [Fact]
    public async Task Api_clients_keep_the_body_token_and_get_no_cookie()
    {
        var (email, _) = await api.RegisterAsync();
        var response = await api.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password });
        Assert.False(string.IsNullOrEmpty(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["refreshToken"]!.GetValue<string>()));
        Assert.Equal(string.Empty, SetCookie(response));
    }

    [Fact]
    public async Task Me_returns_the_profile_the_active_workspace_and_only_the_callers_memberships()
    {
        var (email, owner) = await api.RegisterAsync();
        var (_, other) = await api.RegisterAsync();
        var me = JsonNode.Parse(await api.Client(owner.AccessToken).GetStringAsync("/api/v1/me"))!;

        Assert.Equal(email, me["email"]!.GetValue<string>());
        Assert.Equal(owner.OrganizationId, me["organizationId"]!.GetValue<Guid>());
        Assert.StartsWith("Org ", me["organizationName"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(me["isPlatformOperator"]!.GetValue<bool>());
        var memberships = me["memberships"]!.AsArray();
        Assert.Equal(owner.OrganizationId, Assert.Single(memberships)!["organizationId"]!.GetValue<Guid>());
        Assert.DoesNotContain(other.OrganizationId.ToString(), me.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Overview, search, catalog and validation; cross-tenant checks run on both hosts (with and without the query-filter backstop).</summary>
public abstract class DashboardReadTests(PlatformApi api)
{
    protected PlatformApi Api => api;

    [Fact]
    public async Task Overview_counts_only_the_callers_workspace()
    {
        var (_, a) = await api.RegisterAsync();
        var (_, b) = await api.RegisterAsync();
        var asA = api.Client(a.AccessToken);
        var version = await JobApiTests.PublishedVersionAsync(asA, "overview");
        await WorkflowApiTests.CreateAsync(asA, "draft-only", WorkflowApiTests.Sample);
        var csv = string.Join("\n", JobApiTests.Header, JobApiTests.Row(1), JobApiTests.Row(2, "bad"), JobApiTests.Row(3)) + "\n";
        Assert.Equal(HttpStatusCode.Created, (await JobApiTests.UploadAsync(asA, version, csv)).StatusCode);

        var overviewA = JsonNode.Parse(await asA.GetStringAsync("/api/v1/overview"))!;
        Assert.Equal(1, overviewA["totalJobs"]!.GetValue<int>());
        Assert.Equal(1, overviewA["jobsByStatus"]!["Pending"]!.GetValue<int>());
        Assert.Equal(3, overviewA["totalRecords"]!.GetValue<int>());
        Assert.Equal((2, 1), (overviewA["recordsByStatus"]!["Pending"]!.GetValue<int>(), overviewA["recordsByStatus"]!["Invalid"]!.GetValue<int>()));
        Assert.Equal((2, 1), (overviewA["totalWorkflows"]!.GetValue<int>(), overviewA["publishedWorkflows"]!.GetValue<int>()));
        var recent = Assert.Single(overviewA["recentJobs"]!.AsArray())!;
        Assert.Equal(("overview", 1, 1), (recent["workflowName"]!.GetValue<string>(), recent["workflowVersionNumber"]!.GetValue<int>(), recent["invalidRecords"]!.GetValue<int>()));

        var overviewB = JsonNode.Parse(await api.Client(b.AccessToken).GetStringAsync("/api/v1/overview"))!;
        Assert.Equal((0, 0, 0), (overviewB["totalJobs"]!.GetValue<int>(), overviewB["totalRecords"]!.GetValue<int>(), overviewB["totalWorkflows"]!.GetValue<int>()));
        Assert.Empty(overviewB["recentJobs"]!.AsArray());
        Assert.Empty(overviewB["recentWorkflows"]!.AsArray());
    }

    [Fact]
    public async Task Workflow_search_is_literal_case_insensitive_shows_the_published_version_and_never_crosses_workspaces()
    {
        var (_, a) = await api.RegisterAsync();
        var (_, b) = await api.RegisterAsync();
        var asA = api.Client(a.AccessToken);
        var published = await JobApiTests.PublishedVersionAsync(asA, "Invoice Sync");
        await WorkflowApiTests.CreateAsync(asA, "customer_import", WorkflowApiTests.Sample);
        await WorkflowApiTests.CreateAsync(api.Client(b.AccessToken), "invoice-other-tenant", WorkflowApiTests.Sample);

        var found = JsonNode.Parse(await asA.GetStringAsync("/api/v1/workflows?search=INVOICE"))!.AsArray();
        var hit = Assert.Single(found)!;
        Assert.Equal(("Invoice Sync", published, 1), (hit["name"]!.GetValue<string>(), hit["publishedVersionId"]!.GetValue<Guid>(), hit["publishedVersionNumber"]!.GetValue<int>()));

        // LIKE wildcards are literal: "_" matches only an underscore, "%" matches nothing here.
        Assert.Equal("customer_import", Assert.Single(JsonNode.Parse(await asA.GetStringAsync("/api/v1/workflows?search=r_i"))!.AsArray())!["name"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(await asA.GetStringAsync("/api/v1/workflows?search=%25"))!.AsArray());
        Assert.Null(JsonNode.Parse(await asA.GetStringAsync("/api/v1/workflows?search=customer"))!.AsArray()[0]!["publishedVersionId"]);
    }

    [Fact]
    public async Task Job_list_filters_by_status_within_the_workspace()
    {
        var (_, a) = await api.RegisterAsync();
        var (_, b) = await api.RegisterAsync();
        var asA = api.Client(a.AccessToken);
        var version = await JobApiTests.PublishedVersionAsync(asA, "filter");
        var created = JsonNode.Parse(await (await JobApiTests.UploadAsync(asA, version, JobApiTests.Header + "\n" + JobApiTests.Row(1) + "\n")).Content.ReadAsStringAsync())!;
        var jobId = created["jobId"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.Accepted, (await asA.PostAsync($"/api/v1/jobs/{jobId}/cancel", null)).StatusCode);

        Assert.Single(JsonNode.Parse(await asA.GetStringAsync("/api/v1/jobs?status=Cancelled"))!.AsArray());
        Assert.Empty(JsonNode.Parse(await asA.GetStringAsync("/api/v1/jobs?status=Pending"))!.AsArray());
        Assert.Empty(JsonNode.Parse(await api.Client(b.AccessToken).GetStringAsync("/api/v1/jobs?status=Cancelled"))!.AsArray());
    }
}

public sealed class DashboardReadsWithBackstop(PlatformApi api) : DashboardReadTests(api), IClassFixture<PlatformApi>;

public sealed class DashboardReadsWithoutBackstop(FiltersOffApi fixture) : DashboardReadTests(fixture.Api), IClassFixture<FiltersOffApi>;

public sealed class WorkflowAuthoringApiTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    [Fact]
    public async Task The_catalog_lists_exactly_the_implemented_actions_and_closed_value_sets()
    {
        var (_, owner) = await api.RegisterAsync();
        var catalog = JsonNode.Parse(await api.Client(owner.AccessToken).GetStringAsync("/api/v1/workflow-catalog"))!;
        var actions = catalog["actions"]!.AsArray();
        Assert.Equal(13, actions.Count);
        Assert.Equal(
            ["navigate", "click", "fill", "select", "wait", "extract", "screenshot", "upload", "download", "condition", "delay", "captureResult", "reconcileMatch"],
            actions.Select(a => a!["type"]!.GetValue<string>()));
        var reconcile = actions.Single(a => a!["type"]!.GetValue<string>() == "reconcileMatch")!;
        Assert.Equal(["reconcile"], reconcile["sections"]!.AsArray().Select(s => s!.GetValue<string>()));
        var click = actions.Single(a => a!["type"]!.GetValue<string>() == "click")!;
        Assert.True(click["sideEffectAllowed"]!.GetValue<bool>());
        Assert.Contains("failed", click["outputs"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Contains("file", catalog["inputTypes"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task Validation_matches_draft_storage_without_storing_anything()
    {
        var (_, owner) = await api.RegisterAsync();
        var client = api.Client(owner.AccessToken);
        var sample = JsonNode.Parse(WorkflowApiTests.Sample)!;

        var ok = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/v1/workflows/validate", new { definition = sample })).Content.ReadAsStringAsync())!;
        Assert.True(ok["valid"]!.GetValue<bool>());
        Assert.Contains("TargetEnforcedIdempotency", ok["attestations"]!.ToJsonString(), StringComparison.Ordinal);
        var (_, _, storedHash) = await WorkflowApiTests.CreateAsync(client, "stored", WorkflowApiTests.Sample);
        Assert.Equal(storedHash, ok["definitionHash"]!.GetValue<string>());

        sample["steps"]![0]!["type"] = "runScript";
        var bad = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/v1/workflows/validate", new { definition = sample })).Content.ReadAsStringAsync())!;
        Assert.False(bad["valid"]!.GetValue<bool>());
        Assert.NotEmpty(bad["errors"]!.AsArray());

        var workflows = JsonNode.Parse(await client.GetStringAsync("/api/v1/workflows"))!.AsArray();
        Assert.Single(workflows); // only the explicitly created one
    }

    [Fact]
    public async Task Viewers_may_read_the_catalog_but_not_validate()
    {
        var (_, owner) = await api.RegisterAsync();
        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        var client = api.Client(viewer.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/workflow-catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/workflows/validate", new { definition = new { } })).StatusCode);
    }
}
