using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Jobs;
using AutomataFlow.IntegrationTests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.IntegrationTests.Api;

/// <summary>Phase 8: invitations, membership administration, workspace switching, platform onboarding and suspension.</summary>
public sealed class OnboardingTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    internal static async Task<JsonNode> JsonAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    internal static async Task<string> InviteAsync(HttpClient admin, string email, string role)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/workspace/invitations", new { email, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return (await JsonAsync(response))["token"]!.GetValue<string>();
    }

    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    [Fact]
    public async Task An_invited_person_signs_up_through_the_link_once_and_works_in_the_workspace()
    {
        var (_, owner) = await api.RegisterAsync();
        var asOwner = api.Client(owner.AccessToken);
        var email = NewEmail("new-operator");
        var token = await InviteAsync(asOwner, email, "Operator");

        var preview = await JsonAsync(await api.Client().PostAsJsonAsync("/api/v1/invitations/preview", new { token }));
        Assert.Equal((email, "Operator", false), (preview["email"]!.GetValue<string>(), preview["role"]!.GetValue<string>(), preview["accountExists"]!.GetValue<bool>()));

        var accepted = await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token, password = PlatformApi.Password });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var session = await JsonAsync(accepted);
        Assert.Equal((owner.OrganizationId, "Operator"), (session["organizationId"]!.GetValue<Guid>(), session["role"]!.GetValue<string>()));

        // Works in the workspace: an Operator may create workflows.
        var asMember = api.Client(session["accessToken"]!.GetValue<string>());
        await WorkflowApiTests.CreateAsync(asMember, "by-invitee", WorkflowApiTests.Sample);
        var members = (await JsonAsync(await asOwner.GetAsync("/api/v1/workspace/members"))).AsArray();
        Assert.Contains(members, m => m!["email"]!.GetValue<string>() == email && m["role"]!.GetValue<string>() == "Operator");

        // Single use: the same token is now unknown, for preview and acceptance alike.
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().PostAsJsonAsync("/api/v1/invitations/preview", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token, password = PlatformApi.Password })).StatusCode);
        var invitations = (await JsonAsync(await asOwner.GetAsync("/api/v1/workspace/invitations"))).AsArray();
        Assert.Equal("Accepted", invitations.Single(i => i!["email"]!.GetValue<string>() == email)!["state"]!.GetValue<string>());

        // Only the hash is stored.
        await using var db = api.Database.Context(owner.OrganizationId);
        Assert.False(await db.OrganizationInvitations.AnyAsync(i => i.TokenHash == token));
    }

    [Fact]
    public async Task An_existing_account_must_prove_its_password_and_can_then_switch_between_workspaces()
    {
        var (email, existing) = await api.RegisterAsync("existing");
        var (_, owner) = await api.RegisterAsync();
        var token = await InviteAsync(api.Client(owner.AccessToken), email, "Viewer");

        Assert.True((await JsonAsync(await api.Client().PostAsJsonAsync("/api/v1/invitations/preview", new { token })))["accountExists"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token, password = "wrong-password-123" })).StatusCode);
        var accepted = await JsonAsync(await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token, password = PlatformApi.Password }));
        Assert.Equal(owner.OrganizationId, accepted["organizationId"]!.GetValue<Guid>());

        var me = await JsonAsync(await api.Client(existing.AccessToken).GetAsync("/api/v1/me"));
        Assert.Equal(2, me["memberships"]!.AsArray().Count);

        var switched = await api.Client(existing.AccessToken).PostAsJsonAsync("/api/v1/auth/switch", new { organizationId = owner.OrganizationId, refreshToken = existing.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var inOwnerWorkspace = await JsonAsync(switched);
        Assert.Equal(("Viewer", owner.OrganizationId), (inOwnerWorkspace["role"]!.GetValue<string>(), inOwnerWorkspace["organizationId"]!.GetValue<Guid>()));
        // The previous session was revoked by the switch.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = existing.RefreshToken })).StatusCode);

        // Switching into a workspace one does not belong to is refused.
        var (_, stranger) = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client(existing.AccessToken).PostAsJsonAsync("/api/v1/auth/switch", new { organizationId = stranger.OrganizationId })).StatusCode);
    }

    [Fact]
    public async Task Revoked_and_replaced_invitations_cannot_be_used()
    {
        var (_, owner) = await api.RegisterAsync();
        var asOwner = api.Client(owner.AccessToken);
        var email = NewEmail("revoked");
        var first = await InviteAsync(asOwner, email, "Viewer");
        var second = await InviteAsync(asOwner, email, "Operator"); // replaces the first
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().PostAsJsonAsync("/api/v1/invitations/preview", new { token = first })).StatusCode);

        var id = (await JsonAsync(await asOwner.GetAsync("/api/v1/workspace/invitations"))).AsArray().First(i => i!["state"]!.GetValue<string>() == "Open")!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await asOwner.DeleteAsync($"/api/v1/workspace/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token = second, password = PlatformApi.Password })).StatusCode);
    }

    [Fact]
    public async Task Role_rules_protect_owners_and_the_last_owner()
    {
        var (_, owner) = await api.RegisterAsync();
        var ownerId = Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(owner.AccessToken).Subject);
        var admin = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Admin);
        var viewer = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Viewer);
        var viewerId = Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(viewer.AccessToken).Subject);
        var asAdmin = api.Client(admin.AccessToken);
        var asOwner = api.Client(owner.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await asAdmin.PostAsJsonAsync("/api/v1/workspace/invitations", new { email = NewEmail("x"), role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAdmin.PutAsJsonAsync($"/api/v1/workspace/members/{ownerId}/role", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAdmin.DeleteAsync($"/api/v1/workspace/members/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await asOwner.PutAsJsonAsync($"/api/v1/workspace/members/{ownerId}/role", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await asOwner.DeleteAsync($"/api/v1/workspace/members/{ownerId}")).StatusCode);

        // Viewers manage nothing.
        var asViewer = api.Client(viewer.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await asViewer.GetAsync("/api/v1/workspace/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asViewer.PutAsJsonAsync($"/api/v1/workspace/members/{viewerId}/role", new { role = "Admin" })).StatusCode);

        // An Admin promotes a Viewer to Operator; an Owner can make another Owner, after which demoting is allowed.
        Assert.Equal("Operator", (await JsonAsync(await asAdmin.PutAsJsonAsync($"/api/v1/workspace/members/{viewerId}/role", new { role = "Operator" })))["role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await asOwner.PutAsJsonAsync($"/api/v1/workspace/members/{viewerId}/role", new { role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await asOwner.PutAsJsonAsync($"/api/v1/workspace/members/{ownerId}/role", new { role = "Admin" })).StatusCode);
    }

    [Fact]
    public async Task A_removed_member_loses_access_and_sessions_immediately()
    {
        var (_, owner) = await api.RegisterAsync();
        var member = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Operator);
        var memberId = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(member.AccessToken).Subject;

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(owner.AccessToken).DeleteAsync($"/api/v1/workspace/members/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(member.AccessToken).GetAsync("/api/v1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = member.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Workspace_profile_rename_and_usage_belong_to_the_caller()
    {
        var (_, owner) = await api.RegisterAsync();
        var asOwner = api.Client(owner.AccessToken);
        var version = await JobApiTests.PublishedVersionAsync(asOwner, "usage");
        await JobApiTests.UploadAsync(asOwner, version, string.Join("\n", JobApiTests.Header, JobApiTests.Row(1), JobApiTests.Row(2)) + "\n");

        var renamed = await JsonAsync(await asOwner.PutAsJsonAsync("/api/v1/workspace", new { name = "Renamed Logistics" }));
        Assert.Equal(("Renamed Logistics", 1), (renamed["name"]!.GetValue<string>(), renamed["memberCount"]!.GetValue<int>()));
        var usage = await JsonAsync(await asOwner.GetAsync("/api/v1/workspace/usage?days=7"));
        Assert.Equal(2, usage["totals"]!["records.imported"]!.GetValue<long>());

        var admin = await api.AddMemberAsync(owner.OrganizationId, OrganizationRole.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(admin.AccessToken).PutAsJsonAsync("/api/v1/workspace", new { name = "Not allowed" })).StatusCode);
    }
}

/// <summary>Cross-tenant rules of the workspace endpoints, on both hosts (with and without the query-filter backstop).</summary>
public abstract class WorkspaceCrossTenantTests(PlatformApi api)
{
    [Fact]
    public async Task Another_workspace_can_neither_see_nor_change_members_or_invitations()
    {
        var (_, a) = await api.RegisterAsync();
        var (_, b) = await api.RegisterAsync();
        var memberOfA = await api.AddMemberAsync(a.OrganizationId, OrganizationRole.Viewer);
        var memberId = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(memberOfA.AccessToken).Subject;
        await OnboardingTests.InviteAsync(api.Client(a.AccessToken), $"a-invitee-{Guid.NewGuid():N}@example.test", "Viewer");
        var invitationOfA = (await OnboardingTests.JsonAsync(await api.Client(a.AccessToken).GetAsync("/api/v1/workspace/invitations"))).AsArray()[0]!["id"]!.GetValue<Guid>();

        var asB = api.Client(b.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.PutAsJsonAsync($"/api/v1/workspace/members/{memberId}/role", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.DeleteAsync($"/api/v1/workspace/members/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asB.DeleteAsync($"/api/v1/workspace/invitations/{invitationOfA}")).StatusCode);
        var membersOfB = (await OnboardingTests.JsonAsync(await asB.GetAsync("/api/v1/workspace/members"))).AsArray();
        Assert.Single(membersOfB);
        Assert.Empty((await OnboardingTests.JsonAsync(await asB.GetAsync("/api/v1/workspace/invitations"))).AsArray());
        Assert.Equal(b.OrganizationId, (await OnboardingTests.JsonAsync(await asB.GetAsync("/api/v1/workspace")))["id"]!.GetValue<Guid>());

        // A's member is untouched.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(memberOfA.AccessToken).GetAsync("/api/v1/me")).StatusCode);
    }
}

public sealed class WorkspaceCrossTenantWithBackstop(PlatformApi api) : WorkspaceCrossTenantTests(api), IClassFixture<PlatformApi>;

public sealed class WorkspaceCrossTenantWithoutBackstop(FiltersOffApi fixture) : WorkspaceCrossTenantTests(fixture.Api), IClassFixture<FiltersOffApi>;

/// <summary>Platform-operator onboarding, suspension and bootstrap.</summary>
public sealed class PlatformOnboardingTests(PlatformApi api) : IClassFixture<PlatformApi>
{
    private async Task<TokenDto> OperatorAsync()
    {
        var (_, tokens) = await api.RegisterAsync("operator");
        await api.GrantPlatformOperatorAsync(Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken).Subject));
        return tokens;
    }

    [Fact]
    public async Task Customers_cannot_use_platform_endpoints()
    {
        var (_, owner) = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(owner.AccessToken).GetAsync("/api/v1/platform/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(owner.AccessToken).PostAsJsonAsync("/api/v1/platform/workspaces", new { name = "X", slug = "x-ws", ownerEmail = "x@example.test" })).StatusCode);
    }

    [Fact]
    public async Task An_operator_onboards_a_business_whose_owner_signs_up_through_the_invitation()
    {
        var operatorTokens = await OperatorAsync();
        var asOperator = api.Client(operatorTokens.AccessToken);
        var slug = $"acme-{Guid.NewGuid():N}"[..20];
        var ownerEmail = $"founder-{Guid.NewGuid():N}@example.test";

        var created = await asOperator.PostAsJsonAsync("/api/v1/platform/workspaces", new { name = "Acme Freight", slug, ownerEmail });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await OnboardingTests.JsonAsync(created);
        var workspaceId = body["workspace"]!["id"]!.GetValue<Guid>();
        Assert.Equal(0, body["workspace"]!["memberCount"]!.GetValue<int>());
        var token = body["ownerInvitation"]!["token"]!.GetValue<string>();

        var accepted = await OnboardingTests.JsonAsync(await api.Client().PostAsJsonAsync("/api/v1/invitations/accept", new { token, password = PlatformApi.Password }));
        Assert.Equal((workspaceId, "Owner"), (accepted["organizationId"]!.GetValue<Guid>(), accepted["role"]!.GetValue<string>()));
        var listed = (await OnboardingTests.JsonAsync(await asOperator.GetAsync("/api/v1/platform/workspaces"))).AsArray().Single(w => w!["id"]!.GetValue<Guid>() == workspaceId)!;
        Assert.Equal(1, listed["memberCount"]!.GetValue<int>());

        // Duplicate slugs are refused; the platform listing contains counts only, no customer data fields.
        Assert.Equal(HttpStatusCode.Conflict, (await asOperator.PostAsJsonAsync("/api/v1/platform/workspaces", new { name = "Again", slug, ownerEmail = "again@example.test" })).StatusCode);
        Assert.Null(listed["members"]);
    }

    [Fact]
    public async Task A_suspended_workspace_cannot_sign_in_call_the_Api_or_have_records_claimed_until_reactivated()
    {
        var operatorTokens = await OperatorAsync();
        var asOperator = api.Client(operatorTokens.AccessToken);
        var (email, owner) = await api.RegisterAsync("suspended");
        var asOwner = api.Client(owner.AccessToken);
        var version = await JobApiTests.PublishedVersionAsync(asOwner, "suspension");
        Assert.Equal(HttpStatusCode.Created, (await JobApiTests.UploadAsync(asOwner, version, JobApiTests.Header + "\n" + JobApiTests.Row(1) + "\n")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await asOperator.PostAsync($"/api/v1/platform/workspaces/{owner.OrganizationId}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asOwner.GetAsync("/api/v1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = owner.RefreshToken })).StatusCode);

        var dispatcher = new RecordDispatcher(Npgsql.NpgsqlDataSource.Create(api.Database.ConnectionString));
        var claimed = await dispatcher.ClaimAsync(Guid.NewGuid(), 50, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.DoesNotContain(claimed, c => c.OrganizationId == owner.OrganizationId);

        Assert.Equal(HttpStatusCode.OK, (await asOperator.PostAsync($"/api/v1/platform/workspaces/{owner.OrganizationId}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await asOwner.GetAsync("/api/v1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password })).StatusCode);

        // An operator cannot suspend the workspace they are signed in to.
        Assert.Equal(HttpStatusCode.Conflict, (await asOperator.PostAsync($"/api/v1/platform/workspaces/{operatorTokens.OrganizationId}/suspend", null)).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_creates_an_operator_with_an_operations_workspace_and_never_twice()
    {
        var email = $"root-{Guid.NewGuid():N}@example.test";
        using var scope = api.Factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var slug = $"ops-{Guid.NewGuid():N}"[..16];

        var created = await accounts.BootstrapOperatorAsync(email, PlatformApi.Password, "Operations", slug, roles, CancellationToken.None);
        Assert.Equal(AccountStatus.Ok, created.Status);
        Assert.True(await accounts.IsPlatformOperatorAsync(created.Session!.UserId));

        var login = await OnboardingTests.JsonAsync(await api.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = PlatformApi.Password }));
        Assert.Equal(HttpStatusCode.OK, (await api.Client(login["accessToken"]!.GetValue<string>()).GetAsync("/api/v1/platform/workspaces")).StatusCode);

        using var again = api.Factory.Services.CreateScope();
        Assert.Equal(AccountStatus.Conflict, (await again.ServiceProvider.GetRequiredService<AccountService>().BootstrapOperatorAsync(email, PlatformApi.Password, "Operations 2", slug + "x",
            again.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(), CancellationToken.None)).Status);
    }
}
