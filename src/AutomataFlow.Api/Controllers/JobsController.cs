using System.Text.Json;
using AutomataFlow.Api.Auth;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AutomataFlow.Api.Controllers;

/// <summary>Multipart job creation: the record file plus the pinned version, parameters and options.</summary>
public sealed class CreateJobForm
{
    public Guid WorkflowVersionId { get; set; }

    /// <summary>JSON object of parameter name → value (from the job configuration only, never from records).</summary>
    public string? Parameters { get; set; }

    /// <summary>JSON object: maxConcurrency, maxAttempts, retryBackoffSeconds (all optional).</summary>
    public string? Options { get; set; }

    public IFormFile? File { get; set; }

    /// <summary>Opt-in: ignore columns that are not declared workflow inputs (e.g. a test-expectation column).</summary>
    public bool IgnoreUnknownColumns { get; set; }
}

public sealed record JobOptionsDto(int? MaxConcurrency, int? MaxAttempts, int? RetryBackoffSeconds);

[ApiController]
[Route("api/v1/jobs")]
[Authorize(Policy = Policies.Viewer)]
public sealed class JobsController(JobService jobs, IOptions<ImportOptions> imports) : ControllerBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, (ImportFormat Format, string[] ContentTypes)> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = (ImportFormat.Csv, ["text/csv", "application/csv", "text/plain", "application/vnd.ms-excel"]),
        [".json"] = (ImportFormat.Json, ["application/json", "text/json"]),
    };

    /// <summary>Creates a job from an uploaded CSV/JSON record file. Send an <c>Idempotency-Key</c> header to make retries safe.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Operator)]
    [RequestSizeLimit(110L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 110L * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] CreateJobForm form, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (form.File is not { } file || file.Length == 0)
        {
            return Problem(statusCode: 400, title: "A non-empty record file is required.");
        }

        if (file.Length > imports.Value.MaxBytes)
        {
            return Problem(statusCode: 413, title: $"The file is larger than {imports.Value.MaxBytes} bytes.");
        }

        var extension = Path.GetExtension(file.FileName ?? string.Empty);
        if (!Formats.TryGetValue(extension, out var format)
            || !format.ContentTypes.Contains((file.ContentType ?? string.Empty).Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Problem(statusCode: 415, title: "Only .csv (text/csv) and .json (application/json) record files are accepted.");
        }

        Dictionary<string, string>? parameters;
        JobOptionsDto? options;
        try
        {
            parameters = string.IsNullOrWhiteSpace(form.Parameters) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(form.Parameters, Web);
            options = string.IsNullOrWhiteSpace(form.Options) ? null : JsonSerializer.Deserialize<JobOptionsDto>(form.Options, Web);
        }
        catch (JsonException)
        {
            return Problem(statusCode: 400, title: "parameters and options must be JSON objects.");
        }

        var defaults = JobOptions.Default;
        var jobOptions = new JobOptions(options?.MaxConcurrency ?? defaults.MaxConcurrency, options?.MaxAttempts ?? defaults.MaxAttempts, options?.RetryBackoffSeconds ?? defaults.RetryBackoffSeconds);
        await using var content = file.OpenReadStream();
        var result = await jobs.CreateAsync(new CreateJobCommand(form.WorkflowVersionId, parameters ?? [], jobOptions, idempotencyKey, content, file.FileName, format.Format, form.IgnoreUnknownColumns), cancellationToken);
        return this.ToHttp(result, created => created.Replayed ? Ok(created) : Created($"/api/v1/jobs/{created.JobId}", created));
    }

    [HttpGet]
    public async Task<IReadOnlyList<JobView>> List([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        await jobs.ListAsync(skip, take, cancellationToken);

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken) => this.ToHttp(await jobs.GetAsync(jobId, cancellationToken), Ok);

    [HttpGet("{jobId:guid}/records")]
    public async Task<IActionResult> Records(Guid jobId, [FromQuery] JobRecordStatus? status, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken cancellationToken = default) =>
        this.ToHttp(await jobs.RecordsAsync(jobId, status, skip, take, cancellationToken), Ok);
}
