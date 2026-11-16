using System.ComponentModel.DataAnnotations;
using AutomataFlow.Api.Auth;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Infrastructure.Persistence.Journal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <param name="Method">HostPoweredOffOrDestroyed, HostRebootedVerified or DrainVerified (closed list, validated by the journal).</param>
/// <param name="EvidenceReference">Ticket, console record or command output (10..1,000 characters).</param>
/// <param name="AttestedPersonally">The operator confirms they checked the evidence personally.</param>
public sealed record ConfirmFenceRequest(FenceMethod Method, [Required, MaxLength(1_000)] string EvidenceReference, bool AttestedPersonally, string? CurrentBootId, DrainReport? Drain);

/// <summary>
/// Platform operations (§7.5.6): Worker incarnations and audited human fence confirmation. Platform operators only;
/// tenants can never confirm fences. Every call is audited by the journal, accepted or rejected.
/// </summary>
[ApiController]
[Route("api/v1/platform")]
[Authorize(Policy = Policies.PlatformOperator)]
public sealed class PlatformController(FenceAdministration fences, ICurrentUser user) : ControllerBase
{
    [HttpGet("incarnations")]
    public Task<IReadOnlyList<IncarnationView>> Incarnations(CancellationToken cancellationToken) => fences.ListAsync(cancellationToken);

    [HttpPost("incarnations/{incarnationId:guid}/fence")]
    public async Task<IActionResult> Confirm(Guid incarnationId, ConfirmFenceRequest request, CancellationToken cancellationToken)
    {
        var result = await fences.ConfirmHumanAsync(
            new HumanFenceRequest(incarnationId, request.Method, request.EvidenceReference, request.AttestedPersonally, request.CurrentBootId, request.Drain),
            user.UserId.ToString(), cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return result.Accepted
            ? Ok(new { result.Accepted, result.FencedAt, FencedAttempts = result.FencedAttempts.Length })
            : Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The fence evidence was rejected.", detail: result.RejectionReason);
    }
}
