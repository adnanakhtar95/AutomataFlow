using AutomataFlow.Api.Auth;
using AutomataFlow.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <summary>Workspace statistics for the dashboard's overview (Phase 6).</summary>
[ApiController]
[Route("api/v1/overview")]
[Authorize(Policy = Policies.Viewer)]
public sealed class OverviewController(OverviewService overview) : ControllerBase
{
    [HttpGet]
    public Task<OverviewView> Get(CancellationToken cancellationToken) => overview.GetAsync(cancellationToken);
}
