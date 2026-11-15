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
}
