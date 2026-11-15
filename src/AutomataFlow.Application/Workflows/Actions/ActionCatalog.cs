using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Actions;

/// <summary>The sections a step may appear in (§16 D4).</summary>
[Flags]
public enum WorkflowSections
{
    None = 0,
    Setup = 1,
    Reconcile = 2,
    Steps = 4,
    All = Setup | Reconcile | Steps,
}

/// <summary>
/// The Application half of an action (docs/PHASE4_PLAN.md §2): type, strict config parser, default timeout, declared
/// outputs and capability flags. Used for publish-time validation, by the editor (Phase 6) and by the executor; the
/// Automation handler (step 7) executes the typed config. The parser reads the same immutable JSON tree that is hashed.
/// </summary>
public sealed class ActionDescriptor
{
    internal ActionDescriptor(
        ActionType type,
        Type configType,
        int defaultTimeoutMs,
        ImmutableArray<string> outputs,
        bool sideEffectAllowed,
        bool continueAllowed,
        WorkflowSections allowedSections,
        Func<SchemaV1Binder, JObject, string, int, ActionConfig?> parse)
    {
        Type = type;
        JsonName = WorkflowJsonNames.ActionTypes.Single(p => p.Value == type).Key;
        ConfigType = configType;
        DefaultTimeout = TimeSpan.FromMilliseconds(defaultTimeoutMs);
        Outputs = outputs;
        SideEffectAllowed = sideEffectAllowed;
        ContinueAllowed = continueAllowed;
        AllowedSections = allowedSections;
        Parse = parse;
    }

    public ActionType Type { get; }

    public string JsonName { get; }

    public Type ConfigType { get; }

    /// <summary>Used when a step has no <c>timeoutMs</c> (frozen schemaVersion 1 values, <see cref="SchemaV1Defaults"/>).</summary>
    public TimeSpan DefaultTimeout { get; }

    /// <summary>Outputs readable as <c>{{steps.&lt;id&gt;.&lt;output&gt;}}</c>; every step also exposes <c>failed</c>.</summary>
    public ImmutableArray<string> Outputs { get; }

    /// <summary>May declare <c>sideEffect: true</c> (only in <c>steps</c>).</summary>
    public bool SideEffectAllowed { get; }

    /// <summary>May use <c>onError: continue</c> (ADR-053).</summary>
    public bool ContinueAllowed { get; }

    public WorkflowSections AllowedSections { get; }

    internal Func<SchemaV1Binder, JObject, string, int, ActionConfig?> Parse { get; }
}

/// <summary>The single list of the 13 action descriptors; exactly one per <see cref="ActionType"/>.</summary>
public static class ActionCatalog
{
    /// <summary>Output every step exposes (§9).</summary>
    public const string FailedOutput = "failed";

    public static ImmutableDictionary<ActionType, ActionDescriptor> All { get; } = Build();

    public static ActionDescriptor Get(ActionType type) => All[type];

    private static ImmutableDictionary<ActionType, ActionDescriptor> Build()
    {
        ActionDescriptor[] descriptors =
        [
            new(ActionType.Navigate, typeof(NavigateConfig), SchemaV1Defaults.NavigateTimeoutMs, ["status", "url"], false, false, WorkflowSections.All, (b, c, p, d) => b.ParseNavigate(c, p)),
            new(ActionType.Click, typeof(ClickConfig), SchemaV1Defaults.ClickTimeoutMs, [], true, false, WorkflowSections.All, (b, c, p, d) => b.ParseClick(c, p)),
            new(ActionType.Fill, typeof(FillConfig), SchemaV1Defaults.FillTimeoutMs, [], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseFill(c, p)),
            new(ActionType.Select, typeof(SelectConfig), SchemaV1Defaults.SelectTimeoutMs, [], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseSelect(c, p)),
            new(ActionType.Wait, typeof(WaitConfig), SchemaV1Defaults.WaitTimeoutMs, [], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseWait(c, p)),
            new(ActionType.Extract, typeof(ExtractConfig), SchemaV1Defaults.ExtractTimeoutMs, ["value"], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseExtract(c, p)),
            new(ActionType.Screenshot, typeof(ScreenshotConfig), SchemaV1Defaults.ScreenshotTimeoutMs, ["artifactId"], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseScreenshot(c, p)),
            new(ActionType.Upload, typeof(UploadConfig), SchemaV1Defaults.UploadTimeoutMs, ["fileName"], true, false, WorkflowSections.Steps, (b, c, p, d) => b.ParseUpload(c, p)),
            new(ActionType.Download, typeof(DownloadConfig), SchemaV1Defaults.DownloadTimeoutMs, ["artifactId", "fileName", "sha256"], false, false, WorkflowSections.Setup | WorkflowSections.Steps, (b, c, p, d) => b.ParseDownload(c, p)),
            new(ActionType.Condition, typeof(ConditionConfig), SchemaV1Defaults.ConditionTimeoutMs, ["result"], false, true, WorkflowSections.All, (b, c, p, d) => b.ParseCondition(c, p, d)),
            new(ActionType.Delay, typeof(DelayConfig), SchemaV1Defaults.DelayTimeoutMs, [], false, false, WorkflowSections.All, (b, c, p, d) => b.ParseDelay(c, p)),
            new(ActionType.CaptureResult, typeof(CaptureResultConfig), SchemaV1Defaults.CaptureResultTimeoutMs, [], false, false, WorkflowSections.Setup | WorkflowSections.Steps, (b, c, p, d) => b.ParseCaptureResult(c, p)),
            new(ActionType.ReconcileMatch, typeof(ReconcileMatchConfig), SchemaV1Defaults.ReconcileMatchTimeoutMs, [], false, false, WorkflowSections.Reconcile, (b, c, p, d) => b.ParseReconcileMatch(c, p)),
        ];

        return descriptors.ToImmutableDictionary(d => d.Type);
    }
}
