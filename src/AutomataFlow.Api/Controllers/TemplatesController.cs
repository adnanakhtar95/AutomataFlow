using System.ComponentModel.DataAnnotations;
using AutomataFlow.Api.Auth;
using AutomataFlow.Infrastructure.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

public sealed record InstantiateTemplateRequest([Required, MaxLength(64)] string Name);

/// <summary>Workflow templates shipped with the product (Phase 9): browse, read, and copy into the workspace as a draft.</summary>
[ApiController]
[Route("api/v1/workflow-templates")]
[Authorize(Policy = Policies.Viewer)]
public sealed class TemplatesController(WorkflowTemplateService templates) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<WorkflowTemplateSummary> List() => WorkflowTemplates.List();

    [HttpGet("{templateId}")]
    [ProducesResponseType<WorkflowTemplateDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Get(string templateId) =>
        WorkflowTemplates.Find(templateId) is { } template ? Ok(template) : Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    /// <summary>Creates a new workflow (a draft) in the caller's workspace from the template.</summary>
    [HttpPost("{templateId}/instantiate")]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType<VersionDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Instantiate(string templateId, InstantiateTemplateRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await templates.InstantiateAsync(templateId, request.Name, cancellationToken),
            v => Created($"/api/v1/workflows/{v.WorkflowId}/versions/{v.Version.Id}", v));
}
