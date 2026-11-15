using AutomataFlow.Api.Auth;
using AutomataFlow.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <summary>Job control and execution details (Phase 5 step 6). Every id is resolved inside the caller's organization.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = Policies.Viewer)]
public sealed class ExecutionController(ExecutionQueryService executions) : ControllerBase
{
    [HttpPost("jobs/{jobId:guid}/cancel")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken) =>
        this.ToHttp(await executions.CancelAsync(jobId, cancellationToken), _ => Accepted());

    /// <summary>Results as CSV (row, operation id, status, error code, result fields). Never contains record inputs.</summary>
    [HttpGet("jobs/{jobId:guid}/results.csv")]
    public async Task Results(Guid jobId, CancellationToken cancellationToken)
    {
        var buffer = new StringWriter();
        if (!await executions.WriteResultsCsvAsync(jobId, buffer, cancellationToken))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"job-{jobId:N}-results.csv\"";
        await Response.WriteAsync(buffer.ToString(), cancellationToken);
    }

    [HttpGet("jobs/{jobId:guid}/records/{recordId:guid}/attempts")]
    public async Task<IActionResult> Attempts(Guid jobId, Guid recordId, CancellationToken cancellationToken) =>
        this.ToHttp(await executions.AttemptsAsync(jobId, recordId, cancellationToken), Ok);

    [HttpGet("attempts/{attemptId:guid}/logs")]
    public async Task<IActionResult> Logs(Guid attemptId, CancellationToken cancellationToken) =>
        this.ToHttp(await executions.LogsAsync(attemptId, cancellationToken), Ok);

    /// <summary>Artifact content: only through this authorized endpoint, never as static files (ADR-032).</summary>
    [HttpGet("artifacts/{artifactId:guid}")]
    public async Task<IActionResult> Artifact(Guid artifactId, CancellationToken cancellationToken)
    {
        if (await executions.OpenArtifactAsync(artifactId, cancellationToken) is not { } found)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        return File(found.Content, "application/octet-stream", found.Artifact.FileName);
    }
}
