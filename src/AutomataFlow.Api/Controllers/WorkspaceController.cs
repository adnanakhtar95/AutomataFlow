using System.ComponentModel.DataAnnotations;
using AutomataFlow.Api.Auth;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AutomataFlow.Api.Controllers;

public sealed record RenameWorkspaceRequest([Required, MaxLength(200)] string Name);

public sealed record ChangeRoleRequest(OrganizationRole Role);

public sealed record InviteRequest([Required, EmailAddress, MaxLength(254)] string Email, OrganizationRole Role);

public sealed record AcceptInvitationRequest([Required, MaxLength(100)] string Token, [Required, MaxLength(128)] string Password);

/// <summary>The caller's own workspace (Phase 8): profile, members, invitations and usage. Maps HTTP to <see cref="WorkspaceService"/>.</summary>
[ApiController]
[Route("api/v1/workspace")]
[Authorize(Policy = Policies.Viewer)]
public sealed class WorkspaceController(WorkspaceService workspace) : ControllerBase
{
    [HttpGet]
    public Task<WorkspaceView> Get(CancellationToken cancellationToken) => workspace.GetAsync(cancellationToken);

    [HttpPut]
    [Authorize(Policy = Policies.Owner)]
    [ProducesResponseType<WorkspaceView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Rename(RenameWorkspaceRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workspace.RenameAsync(request.Name, cancellationToken), Ok);

    [HttpGet("members")]
    public Task<IReadOnlyList<MemberView>> Members(CancellationToken cancellationToken) => workspace.MembersAsync(cancellationToken);

    [HttpPut("members/{userId:guid}/role")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<MemberView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeRole(Guid userId, ChangeRoleRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workspace.ChangeRoleAsync(userId, request.Role, cancellationToken), Ok);

    /// <summary>Removes a member; Admins may also remove themselves (leave), except the last Owner.</summary>
    [HttpDelete("members/{userId:guid}")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken cancellationToken) =>
        this.ToHttp(await workspace.RemoveAsync(userId, cancellationToken), _ => NoContent());

    [HttpGet("invitations")]
    [Authorize(Policy = Policies.Admin)]
    public Task<IReadOnlyList<InvitationView>> Invitations(CancellationToken cancellationToken) => workspace.InvitationsAsync(cancellationToken);

    /// <summary>Creates an invitation. The token is returned ONCE; deliver the link yourself (no e-mail is sent).</summary>
    [HttpPost("invitations")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<InvitationCreated>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite(InviteRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToHttp(await workspace.InviteAsync(request.Email, request.Role, cancellationToken), created => StatusCode(StatusCodes.Status201Created, created));
    }

    [HttpDelete("invitations/{invitationId:guid}")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid invitationId, CancellationToken cancellationToken) =>
        this.ToHttp(await workspace.RevokeInvitationAsync(invitationId, cancellationToken), _ => NoContent());

    [HttpGet("usage")]
    [Authorize(Policy = Policies.Admin)]
    public Task<UsageView> Usage([FromQuery, Range(1, WorkspaceService.MaxUsageDays)] int days = 30, CancellationToken cancellationToken = default) =>
        workspace.UsageAsync(days, cancellationToken);
}

/// <summary>Invitation preview and acceptance BEFORE sign-in (Phase 8). Rate-limited like sign-in; tokens never appear in logs.</summary>
[ApiController]
[Route("api/v1/invitations")]
[EnableRateLimiting(RateLimits.Authentication)]
public sealed class InvitationsController(InvitationService invitations, TokenIssuer tokens) : ControllerBase
{
    /// <summary>The token travels in the body, never in a URL (URLs end up in logs and referrers).</summary>
    [HttpPost("preview")]
    [ProducesResponseType<InvitationPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(InvitationTokenRequest request, CancellationToken cancellationToken) =>
        await invitations.PreviewAsync(request.Token, cancellationToken) is { } preview
            ? Ok(preview)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "The invitation is invalid, expired, revoked or already used.");

    /// <summary>Accepts an invitation: an existing account proves its password; otherwise the account is created. Signs in.</summary>
    [HttpPost("accept")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        var result = await invitations.AcceptAsync(request.Token, request.Password, cancellationToken);
        if (result is { Outcome: InvitationOutcome.Accepted, Session: { } session })
        {
            return Ok(SessionResponses.Create(Request, Response, tokens, session));
        }

        return result.Outcome switch
        {
            InvitationOutcome.WrongPassword => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication failed."),
            InvitationOutcome.InvalidPassword => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The password does not meet the requirements.", detail: string.Join(",", result.Errors ?? [])),
            InvitationOutcome.LockedOut => Problem(statusCode: StatusCodes.Status423Locked, title: "The account is temporarily locked."),
            InvitationOutcome.Suspended => Problem(statusCode: StatusCodes.Status403Forbidden, title: "The workspace is suspended."),
            InvitationOutcome.AlreadyMember => Problem(statusCode: StatusCodes.Status409Conflict, title: "You are already a member of this workspace."),
            _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "The invitation is invalid, expired, revoked or already used."),
        };
    }
}

public sealed record InvitationTokenRequest([Required, MaxLength(100)] string Token);

public sealed record CreateWorkspaceRequest([Required, MaxLength(200)] string Name, [Required, MaxLength(63)] string Slug, [Required, EmailAddress, MaxLength(254)] string OwnerEmail);

/// <summary>Platform-operator workspace administration (Phase 8). Never a tenant role; never tenant data.</summary>
[ApiController]
[Route("api/v1/platform/workspaces")]
[Authorize(Policy = Policies.PlatformOperator)]
public sealed class PlatformWorkspacesController(PlatformWorkspaceService platform) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PlatformWorkspaceView>> List(CancellationToken cancellationToken) => platform.ListAsync(cancellationToken);

    [HttpPost]
    [ProducesResponseType<PlatformWorkspaceCreated>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToHttp(await platform.CreateAsync(request.Name, request.Slug, request.OwnerEmail, cancellationToken), created => StatusCode(StatusCodes.Status201Created, created));
    }

    [HttpPost("{organizationId:guid}/suspend")]
    [ProducesResponseType<PlatformWorkspaceView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Suspend(Guid organizationId, CancellationToken cancellationToken) =>
        this.ToHttp(await platform.SuspendAsync(organizationId, cancellationToken), Ok);

    [HttpPost("{organizationId:guid}/reactivate")]
    [ProducesResponseType<PlatformWorkspaceView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reactivate(Guid organizationId, CancellationToken cancellationToken) =>
        this.ToHttp(await platform.ReactivateAsync(organizationId, cancellationToken), Ok);
}
