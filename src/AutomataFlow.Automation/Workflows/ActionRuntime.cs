using System.Collections.Immutable;
using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Automation.Browser;

namespace AutomataFlow.Automation.Workflows;

/// <summary>
/// The Automation half of an action (docs/PHASE4_PLAN.md §2): executes a typed config against <see cref="IBrowserSession"/>
/// only (never Playwright directly). Handlers are stateless singletons; all state lives in <see cref="StepContext"/>.
/// </summary>
public interface IActionHandler
{
    ActionType Type { get; }

    Type ConfigType { get; }

    Task<StepOutcome> ExecuteAsync(StepContext context, ActionConfig config, CancellationToken cancellationToken);
}

public abstract class ActionHandler<TConfig> : IActionHandler
    where TConfig : ActionConfig
{
    public abstract ActionType Type { get; }

    public Type ConfigType => typeof(TConfig);

    public Task<StepOutcome> ExecuteAsync(StepContext context, ActionConfig config, CancellationToken cancellationToken) =>
        ExecuteAsync(context, (TConfig)config, cancellationToken);

    protected abstract Task<StepOutcome> ExecuteAsync(StepContext context, TConfig config, CancellationToken cancellationToken);
}

/// <summary>An engine-level step failure with a classified error (thrown by handlers; mapped by the executor).</summary>
public sealed class StepFailureException(ExecutionError error) : Exception(error.Message)
{
    public ExecutionError Error { get; } = error;

    public static StepFailureException Permanent(string code, string message) => new(new ExecutionError(code, ErrorCategory.Permanent, false, null, message));

    public static StepFailureException Transient(string code, string message) => new(new ExecutionError(code, ErrorCategory.Transient, true, null, message));
}

/// <summary>Thrown when the side-effect checkpoint could not be written: the handler must not dispatch.</summary>
public sealed class CheckpointFailedException(string message, Exception? inner) : Exception(message, inner);

public enum ReconcileFindingKind
{
    Found,
    NotFound,
    TooManyRows,
    AmbiguousMatch,
    Inconclusive,
}

/// <summary>What a step produced: outputs, a condition result, a capture, a reconcile finding, artifacts.</summary>
public sealed class StepOutcome
{
    public static StepOutcome Empty { get; } = new();

    public ImmutableDictionary<string, (string Value, bool Sensitive)> Outputs { get; init; } = ImmutableDictionary<string, (string, bool)>.Empty;

    public bool? ConditionResult { get; init; }

    /// <summary>Resolved captureResult fields (never sensitive) and the declared outcome with its resolved transience.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Fields { get; init; } = [];

    public OutcomeSpec? Outcome { get; init; }

    public bool OutcomeTransient { get; init; }

    public ReconcileFindingKind? Finding { get; init; }

    public ImmutableArray<ArtifactDescriptor> Artifacts { get; init; } = [];

    public static StepOutcome WithOutputs(params (string Name, string Value, bool Sensitive)[] outputs) =>
        new() { Outputs = outputs.ToImmutableDictionary(o => o.Name, o => (o.Value, o.Sensitive), StringComparer.Ordinal) };
}

/// <summary>Server upload policy (from <c>Automation:Browser:Uploads</c>).</summary>
public sealed record UploadPolicy(long MaxBytes, IReadOnlyCollection<string> AllowedExtensions);

/// <summary>Everything a handler needs for one step (§2).</summary>
public sealed class StepContext
{
    internal StepContext(
        IBrowserSession session,
        WorkflowStep step,
        OperationOptions options,
        ITemplateValues values,
        IReadOnlyDictionary<string, WorkflowInput> inputs,
        BoundWorkflow bound,
        ExecutionScope scope,
        IExecutionFileProvider? files,
        UploadPolicy uploads,
        Func<CancellationToken, Task> beforeSideEffect)
    {
        Session = session;
        Step = step;
        Options = options;
        Values = values;
        Inputs = inputs;
        Bound = bound;
        Scope = scope;
        Files = files;
        Uploads = uploads;
        _beforeSideEffect = beforeSideEffect;
    }

    private readonly Func<CancellationToken, Task> _beforeSideEffect;

    public IBrowserSession Session { get; }

    public WorkflowStep Step { get; }

    /// <summary>The step's total deadline (ADR-053).</summary>
    public OperationOptions Options { get; }

    public ITemplateValues Values { get; }

    public IReadOnlyDictionary<string, WorkflowInput> Inputs { get; }

    public BoundWorkflow Bound { get; }

    public ExecutionScope Scope { get; }

    public IExecutionFileProvider? Files { get; }

    public UploadPolicy Uploads { get; }

    /// <summary>
    /// Called by a side-effect handler after its pre-checks and immediately before it dispatches: for a side-effect step
    /// this durably writes the journal checkpoint (and throws <see cref="CheckpointFailedException"/> if it cannot); for
    /// any other step it does nothing.
    /// </summary>
    public Task BeforeSideEffectAsync(CancellationToken cancellationToken) => _beforeSideEffect(cancellationToken);

    /// <summary>Resolves a template for a sink; failures (and run-time sink violations) become step failures.</summary>
    public ResolvedValue Resolve(Template template, TemplateSink sink)
    {
        var resolution = TemplateResolver.Resolve(template, sink, Values, Inputs);
        return resolution.Succeeded
            ? resolution.Value!
            : throw StepFailureException.Permanent(resolution.ErrorCode!, resolution.ErrorMessage!);
    }

    /// <summary>Resolves a selector's placeholders (selector sink) and builds the Phase 3 selector (re-validated).</summary>
    public ElementSelector Selector(WorkflowSelector selector) => Selectors.ToElement(Selectors.Resolve(selector, Values, Inputs));

    public IPageProbe Probe => new SessionPageProbe(Session, Options);
}

/// <summary>Selector conversion between the workflow model and the Phase 3 browser selector.</summary>
public static class Selectors
{
    public static ResolvedSelector Resolve(WorkflowSelector selector, ITemplateValues values, IReadOnlyDictionary<string, WorkflowInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(selector);
        string Text(string raw)
        {
            var template = Template.Parse(raw, out var code, out var message) ?? throw StepFailureException.Permanent(code!, message!);
            var resolution = TemplateResolver.Resolve(template, TemplateSink.SelectorValue, values, inputs);
            if (!resolution.Succeeded)
            {
                throw StepFailureException.Permanent(resolution.ErrorCode!, resolution.ErrorMessage!);
            }

            return resolution.Value!.Text.Length == 0
                ? throw StepFailureException.Permanent(EngineErrorCodes.VariableUnresolved, "A selector value resolved to an empty string.")
                : resolution.Value.Text;
        }

        var templated = selector.Kind is WorkflowSelectorKind.TestId or WorkflowSelectorKind.Text or WorkflowSelectorKind.Label or WorkflowSelectorKind.Placeholder;
        return new ResolvedSelector(selector.Kind, templated ? Text(selector.Value) : selector.Value, selector.AccessibleName is null ? null : Text(selector.AccessibleName));
    }

    public static ElementSelector ToElement(ResolvedSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var kind = selector.Kind switch
        {
            WorkflowSelectorKind.TestId => SelectorKind.TestId,
            WorkflowSelectorKind.Css => SelectorKind.Css,
            WorkflowSelectorKind.Role => SelectorKind.Role,
            WorkflowSelectorKind.Text => SelectorKind.Text,
            WorkflowSelectorKind.Label => SelectorKind.Label,
            WorkflowSelectorKind.Placeholder => SelectorKind.Placeholder,
            _ => SelectorKind.XPath,
        };

        try
        {
            return ElementSelector.Create(kind, selector.Value, selector.AccessibleName);
        }
        catch (ArgumentException)
        {
            throw StepFailureException.Permanent(nameof(BrowserErrorCode.InvalidSelector), "A resolved selector is not valid.");
        }
    }
}

/// <summary>The condition probe over the browser session (§8.1): missing elements give 0, false or null.</summary>
internal sealed class SessionPageProbe(IBrowserSession session, OperationOptions options) : IPageProbe
{
    public string CurrentUrl => session.CurrentUrl;

    public Task<int> CountAsync(ResolvedSelector selector, CancellationToken cancellationToken) => session.CountAsync(Selectors.ToElement(selector), options, cancellationToken);

    public Task<bool> IsVisibleAsync(ResolvedSelector selector, CancellationToken cancellationToken) => session.IsVisibleAsync(Selectors.ToElement(selector), options, cancellationToken);

    public async Task<string?> GetTextAsync(ResolvedSelector selector, CancellationToken cancellationToken)
    {
        var element = Selectors.ToElement(selector);
        return await session.CountAsync(element, options, cancellationToken) == 0 ? null : await session.GetTextAsync(element, options, cancellationToken);
    }

    public async Task<string?> GetAttributeAsync(ResolvedSelector selector, string attribute, CancellationToken cancellationToken)
    {
        var element = Selectors.ToElement(selector);
        return await session.CountAsync(element, options, cancellationToken) == 0 ? null : await session.GetAttributeAsync(element, attribute, options, cancellationToken);
    }
}

/// <summary>Maps Phase 3 browser errors to the §9 classification.</summary>
public static class ErrorMapping
{
    public static ExecutionError FromBrowser(BrowserOperationException exception, string? stepId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var category = exception.Code switch
        {
            BrowserErrorCode.Timeout or BrowserErrorCode.SessionTimeout or BrowserErrorCode.ElementNotFound or BrowserErrorCode.NetworkError
                or BrowserErrorCode.EgressUnavailable or BrowserErrorCode.BrowserCrashed or BrowserErrorCode.SessionClosed => ErrorCategory.Transient,
            BrowserErrorCode.NavigationBlocked or BrowserErrorCode.PolicyViolation => ErrorCategory.Policy,
            _ => ErrorCategory.Permanent,
        };

        // The browser's message is already redacted (ADR-032); only its code is relied on.
        return new ExecutionError(exception.Code.ToString(), category, category == ErrorCategory.Transient, stepId, $"Browser operation failed ({exception.Code}).");
    }
}

/// <summary>
/// Upload checks before anything reaches the browser (§5.4), all before the side-effect checkpoint: size (declared and
/// actual), sanitized extension in the step and server lists, content type consistent with the extension, signature
/// sniffing. Any failure is <c>UploadRejected</c>.
/// </summary>
public static class UploadInspector
{
    private static readonly ImmutableDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>The extensions the engine can verify; a server list must be a subset.</summary>
    public static ImmutableHashSet<string> SniffableExtensions { get; } = [.. ContentTypes.Keys];

    public static async Task<UploadFile> InspectAsync(ExecutionFile file, UploadConfig config, UploadPolicy server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(server);
        var limit = Math.Min(config.MaxBytes ?? server.MaxBytes, server.MaxBytes);
        if (file.SizeBytes > limit)
        {
            throw Reject("The file is larger than the allowed size.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await file.Content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw Reject("The file content exceeds the allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        var name = FileNameSanitizer.Sanitize(file.FileName, "upload");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!config.AllowedExtensions.Contains(extension, StringComparer.Ordinal) || !server.AllowedExtensions.Contains(extension, StringComparer.Ordinal)
            || !ContentTypes.TryGetValue(extension, out var expectedType))
        {
            throw Reject("The file type is not allowed.");
        }

        var declared = file.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.Equals(declared, expectedType, StringComparison.Ordinal))
        {
            throw Reject("The content type does not match the file extension.");
        }

        var bytes = buffer.ToArray();
        if (!SignatureMatches(extension, bytes))
        {
            throw Reject("The file content does not match its type.");
        }

        return new UploadFile(name, expectedType, bytes);
    }

    private static bool SignatureMatches(string extension, byte[] bytes) => extension switch
    {
        ".pdf" => bytes.AsSpan().StartsWith("%PDF-"u8),
        ".png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".docx" or ".xlsx" => bytes.AsSpan().StartsWith("PK\u0003\u0004"u8),
        ".csv" or ".txt" => Array.IndexOf(bytes, (byte)0) < 0 && IsUtf8(bytes),
        _ => false,
    };

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static StepFailureException Reject(string message) => StepFailureException.Permanent(ExecutionErrorCodes.UploadRejected, message);
}
