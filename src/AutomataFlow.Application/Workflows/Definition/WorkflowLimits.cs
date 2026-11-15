namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Single source of truth for workflow limits (docs/PHASE4_PLAN.md §1.6, ADR-055). The parser, the validator, the
/// runtime checks and (by parity test) the JSON Schema all use these constants. Server-configurable maxima live in the
/// respective options classes; a workflow can never exceed them.
/// </summary>
public static class WorkflowLimits
{
    public const int SupportedSchemaVersion = 1;

    // ----- Parser (checked before or during strict JSON parsing) -----

    /// <summary>Maximum definition size in UTF-8 bytes, checked before any parsing.</summary>
    public const int MaxDefinitionBytes = 262_144;

    /// <summary>
    /// Maximum JSON nesting depth (objects and arrays). Deep enough for 3 nested condition branches with depth-5
    /// expressions (about 26 levels), bounded so that recursion and processing stay cheap.
    /// </summary>
    public const int MaxJsonDepth = 32;

    /// <summary>Hard cap for any JSON string value (the longest field, a URL template, is 2,048).</summary>
    public const int MaxJsonStringChars = 2_048;

    /// <summary>Hard cap for any JSON property name.</summary>
    public const int MaxJsonPropertyNameChars = 64;

    /// <summary>Maximum number of validation errors reported for one definition (one extra entry marks truncation).</summary>
    public const int MaxValidationErrors = 100;

    // ----- Definition structure -----

    public const int MinNameChars = 3;
    public const int MaxNameChars = 64;
    public const int MaxDescriptionChars = 500;
    public const int MaxGenericStringChars = 2_000;
    public const int MaxSelectorChars = 500;

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3_600;

    /// <summary>Total steps including setup, reconcile and (from step 5) condition branches.</summary>
    public const int MaxSteps = 200;
    public const int MaxStepNestingDepth = 3;
    public const int MaxStepIdChars = 40;

    public const int MinStepTimeoutMs = 100;
    public const int MinSideEffectStepTimeoutMs = 5_000;
    public const int MaxStepTimeoutMs = 120_000;

    public const int MaxInputs = 100;
    public const int MaxParameters = 20;
    public const int MaxSecrets = 20;
    public const int MaxDeclaredNameChars = 64;

    public const int MaxAllowedHosts = 20;
    public const int MaxAllowedHostChars = 300;
    public const int MaxMaskSelectors = 50;

    public const int MinInputMaxLength = 1;
    public const int MaxInputMaxLength = 10_000;

    public const int MinCommitLatencySeconds = 1;
    public const int MaxCommitLatencySeconds = 3_600;
    public const int MinContractReferenceChars = 10;
    public const int MaxContractReferenceChars = 500;
    public const int MinEvidenceAgeSeconds = 30;
    public const int MaxEvidenceAgeSeconds = 3_600;

    // ----- Action-level limits (enforced by the action catalog from step 5; defined here as the single source) -----

    public const int MaxConditionDepth = 5;
    public const int MaxConditionPredicates = 20;
    public const int MaxConditionChildren = 10;
    public const int MaxUrlTemplateChars = 2_048;
    public const int MaxFinalUrlChars = 2_048;
    public const int MaxUrlPatternChars = 500;
    public const int MinDelayMilliseconds = 1;
    public const int MaxDelayMilliseconds = 30_000;
    public const int MinExtractMaxLength = 1;
    public const int MaxExtractMaxLength = 10_000;
    public const int MaxResultFields = 50;
    public const int MaxResultFieldChars = 2_000;
    public const int MaxResultTotalBytes = 32 * 1_024;
    public const int MaxUploadExtensions = 20;
    public const int MinReconcileMaxRows = 1;
    public const int MaxReconcileMaxRows = 50;
    public const int MaxAcceptStatuses = 10;
    public const int MinAcceptStatus = 400;
    public const int MaxAcceptStatus = 599;
    public const int MaxAttributeNameChars = 100;
    public const int MaxScreenshotLabelChars = 100;
    public const int MaxSelectIndex = 1_000;
    public const int MaxElementCount = 1_000;
    public const int MaxResultFieldNameChars = 64;
    public const int MaxOutcomeCodeChars = 64;
}
