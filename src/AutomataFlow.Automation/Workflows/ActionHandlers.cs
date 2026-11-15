using System.Collections.Immutable;
using System.Globalization;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;
using AutomataFlow.Automation.Browser;

namespace AutomataFlow.Automation.Workflows;

/// <summary>
/// The handler registry (§3). Construction fails (and with it host startup) unless every catalog action has exactly one
/// handler, no handler is for an unknown type, and every handler's config type equals its descriptor's.
/// </summary>
public sealed class ActionRegistry
{
    private readonly ImmutableDictionary<ActionType, IActionHandler> _handlers;

    public ActionRegistry(IEnumerable<IActionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var list = handlers.ToList();
        var problems = new List<string>();
        foreach (var group in list.GroupBy(h => h.Type).Where(g => g.Count() > 1))
        {
            problems.Add($"duplicate handlers for '{group.Key}'");
        }

        foreach (var handler in list.Where(h => !ActionCatalog.All.ContainsKey(h.Type)))
        {
            problems.Add($"handler for unknown action type '{handler.Type}'");
        }

        foreach (var descriptor in ActionCatalog.All.Values)
        {
            var handler = list.FirstOrDefault(h => h.Type == descriptor.Type);
            if (handler is null)
            {
                problems.Add($"no handler for '{descriptor.JsonName}'");
            }
            else if (handler.ConfigType != descriptor.ConfigType)
            {
                problems.Add($"handler for '{descriptor.JsonName}' expects {handler.ConfigType.Name}, descriptor declares {descriptor.ConfigType.Name}");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The action registry is inconsistent with the action catalog: " + string.Join("; ", problems) + ".");
        }

        _handlers = list.ToImmutableDictionary(h => h.Type);
    }

    public IActionHandler Get(ActionType type) => _handlers[type];

    /// <summary>The 13 handlers of the platform.</summary>
    public static IReadOnlyList<IActionHandler> CreateDefaultHandlers() =>
    [
        new NavigateHandler(), new ClickHandler(), new FillHandler(), new SelectHandler(), new WaitHandler(), new ExtractHandler(),
        new ScreenshotHandler(), new UploadHandler(), new DownloadHandler(), new ConditionHandler(), new DelayHandler(),
        new CaptureResultHandler(), new ReconcileMatchHandler(),
    ];
}

public sealed class NavigateHandler : ActionHandler<NavigateConfig>
{
    public override ActionType Type => ActionType.Navigate;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, NavigateConfig config, CancellationToken cancellationToken)
    {
        var origin = context.Bound.OriginOf(config.Url) ?? throw StepFailureException.Permanent(EngineErrorCodes.UnsafeUrlValue, "The URL origin is not bound.");
        var rendered = config.Url.Render(origin, context.Values, context.Inputs);
        if (!rendered.Succeeded)
        {
            throw StepFailureException.Permanent(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        var uri = FinalUrlValidator.Validate(rendered.Url!, config.Url, origin, context.Bound.EgressPolicy, out _)
            ?? throw StepFailureException.Permanent(EngineErrorCodes.UnsafeUrlValue, "The resolved URL failed final validation.");
        var result = await context.Session.NavigateAsync(uri, context.Options, cancellationToken);
        if (result.Status >= 400 && !config.AcceptStatus.Contains(result.Status))
        {
            throw StepFailureException.Permanent(nameof(BrowserErrorCode.HttpError), $"The page returned HTTP {result.Status}.");
        }

        return StepOutcome.WithOutputs(("status", result.Status.ToString(CultureInfo.InvariantCulture), false), ("url", result.FinalUrl, false));
    }
}

public sealed class ClickHandler : ActionHandler<ClickConfig>
{
    public override ActionType Type => ActionType.Click;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, ClickConfig config, CancellationToken cancellationToken)
    {
        var selector = context.Selector(config.Selector);
        await context.BeforeSideEffectAsync(cancellationToken);
        if (config.WaitForNavigation)
        {
            await context.Session.SubmitAsync(selector, context.Options, cancellationToken);
        }
        else
        {
            await context.Session.ClickAsync(selector, context.Options, cancellationToken);
        }

        return StepOutcome.Empty;
    }
}

public sealed class FillHandler : ActionHandler<FillConfig>
{
    public override ActionType Type => ActionType.Fill;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, FillConfig config, CancellationToken cancellationToken)
    {
        var selector = context.Selector(config.Selector);
        var value = context.Resolve(config.Value, TemplateSink.FillValue);
        await context.Session.FillAsync(selector, value.IsSensitive ? InputValue.Sensitive(value.Text) : InputValue.Plain(value.Text), context.Options, cancellationToken);
        return StepOutcome.Empty;
    }
}

public sealed class SelectHandler : ActionHandler<SelectConfig>
{
    public override ActionType Type => ActionType.Select;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, SelectConfig config, CancellationToken cancellationToken)
    {
        var selector = context.Selector(config.Selector);
        SelectChoice choice;
        if (config.By == SelectBy.Index)
        {
            choice = SelectChoice.ByIndex(config.Index!.Value);
        }
        else
        {
            var value = context.Resolve(config.Text!, TemplateSink.SelectValue);
            if (value.IsSensitive)
            {
                context.Session.RegisterSensitive(value.Text, selector);
            }

            choice = config.By == SelectBy.Value ? SelectChoice.ByValue(value.Text) : SelectChoice.ByLabel(value.Text);
        }

        await context.Session.SelectAsync(selector, choice, context.Options, cancellationToken);
        return StepOutcome.Empty;
    }
}

public sealed class WaitHandler : ActionHandler<WaitConfig>
{
    public override ActionType Type => ActionType.Wait;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, WaitConfig config, CancellationToken cancellationToken)
    {
        var condition = config.Kind switch
        {
            WaitFor.Load => WaitCondition.Loaded(),
            WaitFor.Url => WaitCondition.Url(config.Url!.Pattern),
            WaitFor.Visible => WaitCondition.ElementVisible(context.Selector(config.Selector!)),
            WaitFor.Hidden => WaitCondition.ElementHidden(context.Selector(config.Selector!)),
            WaitFor.Attached => WaitCondition.ElementAttached(context.Selector(config.Selector!)),
            _ => WaitCondition.ElementDetached(context.Selector(config.Selector!)),
        };

        await context.Session.WaitForAsync(condition, context.Options, cancellationToken);
        return StepOutcome.Empty;
    }
}

public sealed class ExtractHandler : ActionHandler<ExtractConfig>
{
    public override ActionType Type => ActionType.Extract;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, ExtractConfig config, CancellationToken cancellationToken)
    {
        var selector = context.Selector(config.Selector);
        var value = config.Source == ExtractSource.Text
            ? await context.Session.GetTextAsync(selector, context.Options, cancellationToken)
            : await context.Session.GetAttributeAsync(selector, config.Attribute!, context.Options, cancellationToken)
              ?? throw StepFailureException.Transient(nameof(BrowserErrorCode.ElementNotFound), "The attribute is not present.");

        if (config.Sensitive)
        {
            // Registered BEFORE any further use: redacted from every later message and masked in later screenshots.
            context.Session.RegisterSensitive(value, selector);
        }

        if (value.Length > config.MaxLength)
        {
            throw StepFailureException.Permanent(ExecutionErrorCodes.ExtractTooLong, $"The extracted value is longer than {config.MaxLength} characters.");
        }

        return StepOutcome.WithOutputs(("value", value, config.Sensitive));
    }
}

public sealed class ScreenshotHandler : ActionHandler<ScreenshotConfig>
{
    public override ActionType Type => ActionType.Screenshot;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, ScreenshotConfig config, CancellationToken cancellationToken)
    {
        var label = context.Resolve(config.Label, TemplateSink.ScreenshotLabel);
        var artifact = await context.Session.ScreenshotAsync(new ScreenshotRequest(config.FullPage, label.Text), context.Options, cancellationToken);
        return new StepOutcome
        {
            Outputs = ImmutableDictionary<string, (string, bool)>.Empty.Add("artifactId", (artifact?.Id.ToString("D") ?? string.Empty, false)),
            Artifacts = artifact is null ? [] : [artifact],
        };
    }
}

public sealed class UploadHandler : ActionHandler<UploadConfig>
{
    public override ActionType Type => ActionType.Upload;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, UploadConfig config, CancellationToken cancellationToken)
    {
        var selector = context.Selector(config.Selector);
        var reference = context.Resolve(config.File, TemplateSink.UploadFile);
        if (context.Files is null)
        {
            throw StepFailureException.Permanent(ExecutionErrorCodes.UploadRejected, "No file provider is configured.");
        }

        UploadFile file;
        var source = await context.Files.OpenAsync(context.Scope, reference.Text, cancellationToken);
        await using (source.Content)
        {
            // Every check runs BEFORE the checkpoint, so a rejected upload never counts as a started side effect.
            file = await UploadInspector.InspectAsync(source, config, context.Uploads, cancellationToken);
        }

        await context.BeforeSideEffectAsync(cancellationToken);
        await context.Session.UploadAsync(selector, file, context.Options, cancellationToken);
        return StepOutcome.WithOutputs(("fileName", file.FileName, false));
    }
}

public sealed class DownloadHandler : ActionHandler<DownloadConfig>
{
    public override ActionType Type => ActionType.Download;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, DownloadConfig config, CancellationToken cancellationToken)
    {
        var result = await context.Session.DownloadAsync(context.Selector(config.Trigger), context.Options, cancellationToken);
        return new StepOutcome
        {
            Outputs = new Dictionary<string, (string, bool)>
            {
                ["artifactId"] = (result.Artifact.Id.ToString("D"), false),
                ["fileName"] = (FileNameSanitizer.Sanitize(result.SuggestedFileName, "download"), false),
                ["sha256"] = (result.Artifact.Sha256, false),
            }.ToImmutableDictionary(StringComparer.Ordinal),
            Artifacts = [result.Artifact],
        };
    }
}

public sealed class ConditionHandler : ActionHandler<ConditionConfig>
{
    public override ActionType Type => ActionType.Condition;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, ConditionConfig config, CancellationToken cancellationToken)
    {
        var result = await ConditionEvaluator.CheckAsync(config.If, context.Probe, context.Values, context.Inputs, cancellationToken);
        if (!result.Succeeded)
        {
            throw StepFailureException.Permanent(result.ErrorCode!, "The condition could not be evaluated.");
        }

        // Only the boolean is ever logged or exposed; the compared values are not.
        return new StepOutcome
        {
            ConditionResult = result.Value,
            Outputs = ImmutableDictionary<string, (string, bool)>.Empty.Add("result", (result.Value ? "true" : "false", false)),
        };
    }
}

public sealed class DelayHandler : ActionHandler<DelayConfig>
{
    public override ActionType Type => ActionType.Delay;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, DelayConfig config, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(config.Milliseconds), cancellationToken);
        return StepOutcome.Empty;
    }
}

public sealed class CaptureResultHandler : ActionHandler<CaptureResultConfig>
{
    public override ActionType Type => ActionType.CaptureResult;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, CaptureResultConfig config, CancellationToken cancellationToken)
    {
        var fields = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        foreach (var (name, template) in config.Fields)
        {
            var value = context.Resolve(template, TemplateSink.ResultField);
            if (value.Text.Length > WorkflowLimits.MaxResultFieldChars)
            {
                throw StepFailureException.Permanent(ExecutionErrorCodes.ResultTooLarge, $"Result field '{name}' is longer than {WorkflowLimits.MaxResultFieldChars} characters.");
            }

            fields.Add(new(name, value.Text));
        }

        var transient = config.Outcome?.Transient ?? false;
        if (config.Outcome?.TransientWhen is { } when)
        {
            var evaluation = await ConditionEvaluator.CheckAsync(when, context.Probe, context.Values, context.Inputs, cancellationToken);
            transient = evaluation.Succeeded
                ? evaluation.Value
                : throw StepFailureException.Permanent(evaluation.ErrorCode!, "transientWhen could not be evaluated.");
        }

        return new StepOutcome { Fields = fields.ToImmutable(), Outcome = config.Outcome, OutcomeTransient = transient };
    }
}

/// <summary>
/// Exact-match reconciliation (§10.3): bounded reads only, values never logged. Zero rows count as NotFound only with the
/// empty indicator visible; otherwise the lookup is inconclusive (transient).
/// </summary>
public sealed class ReconcileMatchHandler : ActionHandler<ReconcileMatchConfig>
{
    public override ActionType Type => ActionType.ReconcileMatch;

    protected override async Task<StepOutcome> ExecuteAsync(StepContext context, ReconcileMatchConfig config, CancellationToken cancellationToken)
    {
        var rows = context.Selector(config.Rows);
        var count = await context.Session.CountAsync(rows, context.Options, cancellationToken);
        if (count > config.MaxRows)
        {
            return new StepOutcome { Finding = ReconcileFindingKind.TooManyRows };
        }

        if (count == 0)
        {
            var empty = await context.Session.IsVisibleAsync(context.Selector(config.EmptyIndicator), context.Options, cancellationToken);
            return new StepOutcome { Finding = empty ? ReconcileFindingKind.NotFound : ReconcileFindingKind.Inconclusive };
        }

        var expected = context.Resolve(config.EqualsTemplate, TemplateSink.ReconcileEquals).Text.Trim();
        if (expected.Length == 0)
        {
            throw StepFailureException.Permanent(EngineErrorCodes.VariableUnresolved, "The reconcile key resolved to an empty value.");
        }

        var specs = new List<RowValueSpec>
        {
            config.Key.Attribute is { } attribute ? RowValueSpec.OfAttribute(attribute) : RowValueSpec.OfChildText(context.Selector(config.Key.Text!)),
        };
        specs.AddRange(config.Fields.Select(f => RowValueSpec.OfAttribute(f.Value)));

        var snapshot = await context.Session.QueryRowsAsync(rows, specs, config.MaxRows, context.Options, cancellationToken);
        if (snapshot.Truncated || snapshot.Count > config.MaxRows)
        {
            return new StepOutcome { Finding = ReconcileFindingKind.TooManyRows };
        }

        var comparison = config.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = snapshot.Values.Where(row => row[0] is { } key && string.Equals(key.Trim(), expected, comparison)).ToList();
        return matches.Count switch
        {
            0 => new StepOutcome { Finding = ReconcileFindingKind.NotFound },
            1 => new StepOutcome
            {
                Finding = ReconcileFindingKind.Found,
                Fields = [.. config.Fields.Select((f, i) => new KeyValuePair<string, string>(f.Key, (matches[0][i + 1] ?? string.Empty).Trim()))],
            },
            _ => new StepOutcome { Finding = ReconcileFindingKind.AmbiguousMatch },
        };
    }
}
