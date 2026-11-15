namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// The values schemaVersion 1 assigns to omitted optional properties. They are <b>frozen</b> (ADR-061): the definition
/// hash covers the definition as written, so an omitted property's meaning comes from these constants, and changing
/// one would silently change what an already-published hash means. A different default requires a new schemaVersion.
/// Literal values are pinned by tests. They are deliberately separate from <see cref="WorkflowLimits"/>, whose bounds
/// may be tuned independently.
/// </summary>
public static class SchemaV1Defaults
{
    public const int TimeoutSeconds = 120;

    public const bool InputRequired = false;
    public const int InputMaxLength = 1_000;
    public const bool InputSensitive = false;

    public const int MaxEvidenceAgeSeconds = 300;

    public const StepErrorMode OnError = StepErrorMode.Fail;

    // Action-level defaults (applied by the action catalog).
    public const bool ClickWaitForNavigation = false;
    public const bool ExtractSensitive = false;
    public const int ExtractMaxLength = 1_000;
    public const bool ScreenshotFullPage = false;
    public const bool ReconcileIgnoreCase = false;
    public const int ReconcileMaxRows = 20;
    public const bool ConditionIgnoreCase = false;

    /// <summary>A failed outcome without <c>transient</c>/<c>transientWhen</c> is permanent.</summary>
    public const bool OutcomeTransient = false;

    // Default step timeouts (total deadline per step when timeoutMs is omitted).
    public const int NavigateTimeoutMs = 30_000;
    public const int ClickTimeoutMs = 15_000;
    public const int FillTimeoutMs = 10_000;
    public const int SelectTimeoutMs = 10_000;
    public const int WaitTimeoutMs = 30_000;
    public const int ExtractTimeoutMs = 10_000;
    public const int ScreenshotTimeoutMs = 15_000;
    public const int UploadTimeoutMs = 30_000;
    public const int DownloadTimeoutMs = 60_000;
    public const int ConditionTimeoutMs = 10_000;
    public const int DelayTimeoutMs = 31_000;
    public const int CaptureResultTimeoutMs = 5_000;
    public const int ReconcileMatchTimeoutMs = 30_000;
}
