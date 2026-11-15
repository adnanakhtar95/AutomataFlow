using System.Collections.Immutable;
using AutomataFlow.Application.Network;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;

namespace AutomataFlow.Application.Workflows.Binding;

/// <summary>Server-side inputs to binding. Nothing here comes from a workflow, job, record or request (§16 R3).</summary>
public sealed class BindingOptions
{
    /// <summary>True only when the engine runs in Development (server configuration).</summary>
    public bool IsDevelopment { get; init; }

    /// <summary>The trusted server setting <c>Automation:Browser:DevelopmentPrivateNetworkOrigins</c> (ignored outside Development).</summary>
    public IReadOnlyList<string> DevelopmentPrivateNetworkOrigins { get; init; } = [];

    /// <summary>Server <c>Automation:Browser:Timeouts:SessionSeconds</c>; a workflow's <c>timeoutSeconds</c> may not exceed it.</summary>
    public int MaxSessionSeconds { get; init; } = 300;

    /// <summary>
    /// Server-permitted upload extensions (§5.4). An upload step's <c>allowedExtensions</c> must be a subset. The default
    /// is the set the engine can verify by content sniffing; executables and scripts are never permitted (authoring rule).
    /// </summary>
    public IReadOnlyCollection<string> UploadAllowedExtensions { get; init; } = [".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".csv", ".txt"];

    /// <summary>Server <c>Uploads.MaxBytes</c>; an upload step's <c>maxBytes</c> may not exceed it.</summary>
    public long MaxUploadBytes { get; init; } = 10L * 1024 * 1024;
}

/// <summary>The result of binding a workflow to job parameters (§6.2 step 2).</summary>
public sealed class BoundWorkflow
{
    internal BoundWorkflow(ImmutableDictionary<string, string> textParameters, ImmutableDictionary<string, OriginParts> urlParameters, EgressPolicy egressPolicy)
    {
        TextParameters = textParameters;
        UrlParameters = urlParameters;
        EgressPolicy = egressPolicy;
    }

    /// <summary><c>string</c> parameters as supplied (validated).</summary>
    public ImmutableDictionary<string, string> TextParameters { get; }

    /// <summary><c>url</c> parameters, canonicalized; the canonical value is what is substituted.</summary>
    public ImmutableDictionary<string, OriginParts> UrlParameters { get; }

    /// <summary>Literal allowed hosts plus the bound url parameters listed in <c>allowedHosts</c>.</summary>
    public EgressPolicy EgressPolicy { get; }

    /// <summary>The bound origin of a URL template (its literal origin, or the bound url parameter).</summary>
    public OriginParts? OriginOf(UrlTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.LiteralOrigin ?? (UrlParameters.TryGetValue(template.OriginParameter!, out var origin) ? origin : null);
    }
}

/// <summary>
/// Binds a validated workflow to the job configuration before any browser exists (docs/PHASE4_PLAN.md §6.2, §16 D7,
/// R3; ADR-043, ADR-047):
/// <list type="bullet">
/// <item>every declared parameter must be supplied and nothing else may be;</item>
/// <item><c>url</c> parameters are canonicalized; <c>https</c> only and never a private IP literal outside Development;</item>
/// <item><c>string</c> parameters are 1–200 characters with no control characters;</item>
/// <item><c>timeoutSeconds</c> must not exceed the server session limit;</item>
/// <item>private-network access is granted to a rule only in Development and only when its origin exactly matches the
/// trusted server configuration; no workflow, parameter, record or request can grant it.</item>
/// </list>
/// Errors never contain parameter values.
/// </summary>
public static class WorkflowBinder
{
    public const int MaxTextParameterChars = 200;

    public static (BoundWorkflow? Bound, ImmutableArray<ValidationError> Errors) Bind(WorkflowDefinition definition, IReadOnlyDictionary<string, string> parameters, BindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);
        var errors = ImmutableArray.CreateBuilder<ValidationError>();

        if (definition.TimeoutSeconds > options.MaxSessionSeconds)
        {
            errors.Add(new ValidationError("$.timeoutSeconds", EngineErrorCodes.WorkflowTimeoutExceedsServerLimit,
                $"timeoutSeconds ({definition.TimeoutSeconds}) exceeds the server session limit ({options.MaxSessionSeconds})."));
        }

        CheckUploads(definition, options, errors);

        var declared = definition.Parameters.ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
        foreach (var name in parameters.Keys)
        {
            if (!declared.ContainsKey(name))
            {
                errors.Add(new ValidationError(DiagnosticPath.Property("params", name), "parameter-unknown", "A supplied parameter is not declared by the workflow."));
            }
        }

        var text = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var urls = ImmutableDictionary.CreateBuilder<string, OriginParts>(StringComparer.Ordinal);
        foreach (var (name, type) in declared)
        {
            var path = "params." + name;
            if (!parameters.TryGetValue(name, out var value) || value is null)
            {
                errors.Add(new ValidationError(path, "parameter-missing", "A declared parameter was not supplied."));
                continue;
            }

            if (type == WorkflowParameterType.Text)
            {
                if (value.Length is 0 or > MaxTextParameterChars || value.Any(char.IsControl) || !Definition.Json.StrictJsonParser.IsValidUtf16(value))
                {
                    errors.Add(new ValidationError(path, "parameter-invalid", $"A string parameter must be 1–{MaxTextParameterChars} characters without control characters."));
                    continue;
                }

                text[name] = value;
                continue;
            }

            var origin = CanonicalOrigin.TryCanonicalize(value, allowWildcard: false, out var originError);
            if (origin is null)
            {
                errors.Add(new ValidationError(path, "parameter-invalid", $"A url parameter must be an origin scheme://host[:port] ({originError})."));
                continue;
            }

            if (!options.IsDevelopment && origin.Scheme != "https")
            {
                errors.Add(new ValidationError(path, "parameter-invalid", "A url parameter must use https outside Development."));
                continue;
            }

            if (!options.IsDevelopment && origin.Literal is not null && !IpAddressPolicy.IsAllowed(origin.Literal))
            {
                errors.Add(new ValidationError(path, "parameter-invalid", "A url parameter cannot be a private or reserved IP address outside Development."));
                continue;
            }

            urls[name] = origin;
        }

        if (errors.Count > 0)
        {
            return (null, errors.ToImmutable());
        }

        var trusted = TrustedOrigins(options);
        var rules = new List<AllowedHostRule>();
        foreach (var entry in definition.AllowedHosts)
        {
            var origin = AllowedHosts.TryGetParameterName(entry, out var parameter) ? urls[parameter].ToString() : entry;
            rules.Add(AllowedHostRule.Parse(origin, allowPrivateNetwork: trusted.Contains(origin)));
        }

        return (new BoundWorkflow(text.ToImmutable(), urls.ToImmutable(), new EgressPolicy(rules)), []);
    }

    private static void CheckUploads(WorkflowDefinition definition, BindingOptions options, ImmutableArray<ValidationError>.Builder errors)
    {
        foreach (var step in definition.AllSteps())
        {
            if (step.Action is not Actions.UploadConfig upload)
            {
                continue;
            }

            foreach (var extension in upload.AllowedExtensions.Where(e => !options.UploadAllowedExtensions.Contains(e, StringComparer.Ordinal)))
            {
                errors.Add(new ValidationError(step.Path + ".config.allowedExtensions", "upload-extension-not-permitted", $"The server does not permit uploading '{extension}' files."));
            }

            if (upload.MaxBytes > options.MaxUploadBytes)
            {
                errors.Add(new ValidationError(step.Path + ".config.maxBytes", "upload-max-bytes-exceeds-server", $"maxBytes exceeds the server limit ({options.MaxUploadBytes})."));
            }
        }
    }

    /// <summary>Canonical trusted origins; empty outside Development, so the flag can never be set there.</summary>
    private static HashSet<string> TrustedOrigins(BindingOptions options)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        if (!options.IsDevelopment)
        {
            return trusted;
        }

        foreach (var origin in options.DevelopmentPrivateNetworkOrigins)
        {
            if (CanonicalOrigin.TryCanonicalize(origin, allowWildcard: false, out _) is { } parts)
            {
                trusted.Add(parts.ToString());
            }
        }

        return trusted;
    }
}

/// <summary>Authoring-time rules for <c>allowedHosts</c> entries (§6.2 step 1).</summary>
public static class AllowedHosts
{
    /// <summary>True when the entry is exactly <c>{{params.&lt;name&gt;}}</c>.</summary>
    public static bool TryGetParameterName(string entry, out string name)
    {
        ArgumentNullException.ThrowIfNull(entry);
        name = string.Empty;
        var template = Template.Parse(entry, out _, out _);
        if (template is not null && template.IsSinglePlaceholder(out var placeholder) && placeholder.Namespace == TemplateNamespace.Params)
        {
            name = placeholder.Name;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates one entry: a canonical literal <c>https</c> origin (wildcards for subdomains only, never a private or
    /// reserved IP literal), or exactly <c>{{params.&lt;url parameter&gt;}}</c> of a declared url parameter.
    /// </summary>
    public static ValidationError? Check(string entry, string path, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(definition);
        if (entry.Contains("{{", StringComparison.Ordinal))
        {
            if (!TryGetParameterName(entry, out var name))
            {
                return new ValidationError(path, "allowed-host-invalid", "A placeholder entry must be exactly {{params.<url parameter>}}.");
            }

            var parameter = definition.Parameters.FirstOrDefault(p => p.Name == name);
            if (parameter is null)
            {
                return new ValidationError(path, TemplateErrorCodes.UndeclaredParameter, $"Parameter '{name}' is not declared.");
            }

            return parameter.Type == WorkflowParameterType.Url
                ? null
                : new ValidationError(path, TemplateErrorCodes.ParameterMisuse, $"Parameter '{name}' must be of type 'url' to be an allowed host.");
        }

        var origin = CanonicalOrigin.TryParseCanonical(entry, allowWildcard: true, out var error);
        if (origin is null)
        {
            return new ValidationError(path, "allowed-host-not-canonical", $"An allowed host must be a canonical origin such as https://crm.example.com ({error}).");
        }

        if (origin.Scheme != "https")
        {
            return new ValidationError(path, "allowed-host-not-https", "Literal allowed hosts must use https; use a url parameter for Development-only http origins.");
        }

        if (origin.Literal is not null && !IpAddressPolicy.IsAllowed(origin.Literal))
        {
            return new ValidationError(path, "allowed-host-private", "An allowed host cannot be a private or reserved IP address.");
        }

        return null;
    }
}
