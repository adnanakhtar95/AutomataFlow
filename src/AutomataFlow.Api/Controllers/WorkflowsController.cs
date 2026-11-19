using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AutomataFlow.Api.Auth;
using AutomataFlow.Infrastructure.Security;
using AutomataFlow.Infrastructure.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <param name="Definition">The workflow definition as a JSON object; stored exactly as received (identity is its canonical hash).</param>
public sealed record CreateWorkflowRequest([Required, MaxLength(64)] string Name, [MaxLength(500)] string? Description, JsonElement Definition);

public sealed record DefinitionRequest(JsonElement Definition);

public sealed record PublishRequest(bool AcknowledgeAttestations);

[ApiController]
[Route("api/v1/workflows")]
[Authorize(Policy = Policies.Viewer)]
public sealed class WorkflowsController(WorkflowService workflows) : ControllerBase
{
    /// <param name="search">Optional: case-insensitive match on name or description.</param>
    [HttpGet]
    public async Task<IReadOnlyList<WorkflowSummary>> List([FromQuery, MaxLength(WorkflowService.MaxSearchLength)] string? search = null, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        await workflows.ListAsync(search, skip, take, cancellationToken);

    /// <summary>Validates a definition with the same rules as saving a draft, without storing anything.</summary>
    [HttpPost("validate")]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType<DefinitionValidation>(StatusCodes.Status200OK)]
    public DefinitionValidation Validate(DefinitionRequest request) => WorkflowService.ValidateDefinition(Raw(request.Definition));

    [HttpGet("{workflowId:guid}")]
    [ProducesResponseType<WorkflowDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid workflowId, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.GetAsync(workflowId, cancellationToken), Ok);

    [HttpPost]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType<VersionDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateWorkflowRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.CreateAsync(request.Name, request.Description, Raw(request.Definition), cancellationToken),
            v => Created($"/api/v1/workflows/{v.WorkflowId}/versions/{v.Version.Id}", v));

    [HttpGet("{workflowId:guid}/versions/{versionId:guid}")]
    [ProducesResponseType<VersionDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVersion(Guid workflowId, Guid versionId, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.GetVersionAsync(workflowId, versionId, cancellationToken), Ok);

    [HttpPost("{workflowId:guid}/versions")]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType<VersionDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddVersion(Guid workflowId, DefinitionRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.AddVersionAsync(workflowId, Raw(request.Definition), cancellationToken),
            v => Created($"/api/v1/workflows/{workflowId}/versions/{v.Version.Id}", v));

    [HttpPut("{workflowId:guid}/versions/{versionId:guid}")]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType<VersionDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateDraft(Guid workflowId, Guid versionId, DefinitionRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.UpdateDraftAsync(workflowId, versionId, Raw(request.Definition), cancellationToken), Ok);

    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/publish")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<VersionSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Publish(Guid workflowId, Guid versionId, PublishRequest request, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.PublishAsync(workflowId, versionId, request.AcknowledgeAttestations, cancellationToken), Ok);

    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/archive")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<VersionSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Archive(Guid workflowId, Guid versionId, CancellationToken cancellationToken) =>
        this.ToHttp(await workflows.ArchiveAsync(workflowId, versionId, cancellationToken), Ok);

    /// <summary>The definition exactly as received; a missing definition becomes an empty one (a validation error).</summary>
    private static string Raw(JsonElement definition) => definition.ValueKind == JsonValueKind.Undefined ? string.Empty : definition.GetRawText();
}

public sealed record SetSecretRequest([Required, MaxLength(SecretService.MaxValueLength)] string Value);

/// <summary>Organization secrets: names can be listed, values can only be written (never read back).</summary>
[ApiController]
[Route("api/v1/secrets")]
[Authorize(Policy = Policies.Admin)]
public sealed class SecretsController(SecretService secrets, AutomataFlow.Application.Abstractions.ICurrentUser user) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<SecretSummary>> List(CancellationToken cancellationToken) => await secrets.ListAsync(user.OrganizationId, cancellationToken);

    [HttpPut("{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Set(string name, SetSecretRequest request, CancellationToken cancellationToken) =>
        await secrets.SetAsync(user.OrganizationId, name, request.Value, cancellationToken) ? NoContent() : Problem(statusCode: 400, title: "Invalid secret name or value.");

    [HttpDelete("{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken) =>
        await secrets.DeleteAsync(user.OrganizationId, name, cancellationToken) ? NoContent() : NotFound();
}
