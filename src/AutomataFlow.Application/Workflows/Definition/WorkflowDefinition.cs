using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// A bound schemaVersion 1 workflow definition (docs/PHASE4_PLAN.md §1). Instances are created only by
/// <see cref="WorkflowDefinitionParser"/> and are immutable: every property is get-only outside Application and every
/// collection is an <see cref="ImmutableArray{T}"/>. Consumers obtain one through <see cref="ValidatedWorkflow"/>.
/// The model contains typed data only; there is no field that can carry a script.
/// </summary>
public sealed class WorkflowDefinition
{
    internal WorkflowDefinition()
    {
    }

    public int SchemaVersion { get; internal init; }

    /// <summary>Identity of the workflow: <c>^[a-z][a-z0-9-]{2,63}$</c>.</summary>
    public string Name { get; internal init; } = string.Empty;

    public string? Description { get; internal init; }

    /// <summary>Whole-attempt timeout; <see cref="SchemaV1Defaults.TimeoutSeconds"/> when omitted.</summary>
    public int TimeoutSeconds { get; internal init; }

    /// <summary>Supplied by the job configuration, never by a record (§16 D7).</summary>
    public ImmutableArray<WorkflowParameter> Parameters { get; internal init; } = [];

    /// <summary>
    /// Raw entries (literal origins or <c>{{params.x}}</c>). Structural checks only in step 3; canonical-origin and
    /// template validation is implementation step 4 (§6.2).
    /// </summary>
    public ImmutableArray<string> AllowedHosts { get; internal init; } = [];

    /// <summary>The record schema.</summary>
    public ImmutableArray<WorkflowInput> Inputs { get; internal init; } = [];

    /// <summary>Secret names only; values come from the secret provider at run time.</summary>
    public ImmutableArray<string> Secrets { get; internal init; } = [];

    public ImmutableArray<WorkflowSelector> MaskSelectors { get; internal init; } = [];

    /// <summary>Present if and only if <see cref="HasReconcileSection"/>.</summary>
    public ReconciliationPolicy? Reconciliation { get; internal init; }

    public ImmutableArray<WorkflowStep> Setup { get; internal init; } = [];

    /// <summary>True when the definition contains a <c>reconcile</c> property (even an empty one).</summary>
    public bool HasReconcileSection { get; internal init; }

    public ImmutableArray<WorkflowStep> Reconcile { get; internal init; } = [];

    public ImmutableArray<WorkflowStep> Steps { get; internal init; } = [];
}

/// <summary>JSON names: <c>url</c>, <c>string</c> (see <see cref="WorkflowJsonNames.ParameterTypes"/>).</summary>
public enum WorkflowParameterType
{
    Url,
    Text,
}

public sealed class WorkflowParameter
{
    internal WorkflowParameter()
    {
    }

    public string Name { get; internal init; } = string.Empty;

    public WorkflowParameterType Type { get; internal init; }
}

/// <summary>JSON names: <c>string</c>, <c>email</c>, <c>integer</c>, <c>decimal</c>, <c>boolean</c>, <c>date</c>, <c>file</c>.</summary>
public enum WorkflowInputType
{
    Text,
    Email,
    WholeNumber,
    DecimalNumber,
    Boolean,
    Date,
    File,
}

public sealed class WorkflowInput
{
    internal WorkflowInput()
    {
    }

    public string Name { get; internal init; } = string.Empty;

    public WorkflowInputType Type { get; internal init; }

    /// <summary>False when omitted.</summary>
    public bool Required { get; internal init; }

    /// <summary><see cref="SchemaV1Defaults.InputMaxLength"/> when omitted.</summary>
    public int MaxLength { get; internal init; }

    /// <summary>False when omitted.</summary>
    public bool Sensitive { get; internal init; }
}

public enum EvidenceLevel
{
    Advisory,
    Authoritative,
}

public enum MatchKeyKind
{
    OperationId,
    BusinessKey,
}

public sealed class ReconciliationPolicy
{
    internal ReconciliationPolicy()
    {
    }

    public EvidenceLevel Evidence { get; internal init; }

    public MatchKeyKind MatchKey { get; internal init; }

    /// <summary>Required for <see cref="EvidenceLevel.Authoritative"/>, absent for advisory (ADR-057).</summary>
    public ReconciliationAuthority? Authority { get; internal init; }
}

/// <summary>The target's documented commit-latency contract (§10.4, ADR-057).</summary>
public sealed class ReconciliationAuthority
{
    internal ReconciliationAuthority()
    {
    }

    public int MaxCommitLatencySeconds { get; internal init; }

    public string ContractReference { get; internal init; } = string.Empty;

    /// <summary><see cref="SchemaV1Defaults.MaxEvidenceAgeSeconds"/> when omitted.</summary>
    public int MaxEvidenceAgeSeconds { get; internal init; }
}

/// <summary>The 13 action types of schemaVersion 1; JSON names in <see cref="WorkflowJsonNames"/>.</summary>
public enum ActionType
{
    Navigate,
    Click,
    Fill,
    Select,
    Wait,
    Extract,
    Screenshot,
    Upload,
    Download,
    Condition,
    Delay,
    CaptureResult,
    ReconcileMatch,
}

public enum StepErrorMode
{
    Fail,
    Continue,
}

public enum IdempotencyGuarantee
{
    TargetEnforced,
}

/// <summary>The author's attestation that the target deduplicates by the operation id (§10.1).</summary>
public sealed class IdempotencyAttestation
{
    public const string OperationIdKey = "{{operation.id}}";

    internal IdempotencyAttestation()
    {
    }

    /// <summary>Always <see cref="OperationIdKey"/>.</summary>
    public string Key { get; internal init; } = OperationIdKey;

    public IdempotencyGuarantee Guarantee { get; internal init; }
}

public sealed class WorkflowStep
{
    internal WorkflowStep()
    {
    }

    /// <summary><c>^[a-z][a-z0-9-]{0,39}$</c>, unique across the whole workflow.</summary>
    public string Id { get; internal init; } = string.Empty;

    public ActionType Type { get; internal init; }

    /// <summary>
    /// The action-specific configuration as an immutable, strictly parsed JSON object. Typed per action by the action
    /// catalog in implementation step 5; in step 3 it is only required to be an object free of loop and regex keys.
    /// </summary>
    public JObject Config { get; internal init; } = null!;

    public int? TimeoutMs { get; internal init; }

    /// <summary><see cref="StepErrorMode.Fail"/> when omitted.</summary>
    public StepErrorMode OnError { get; internal init; }

    /// <summary>Null when omitted (the per-section declaration rules are implementation step 5).</summary>
    public bool? SideEffect { get; internal init; }

    public IdempotencyAttestation? Idempotency { get; internal init; }

    public string? Description { get; internal init; }
}

public enum WorkflowSelectorKind
{
    TestId,
    Css,
    Role,
    Text,
    Label,
    Placeholder,
    XPath,
}

/// <summary>Maps 1:1 to the Phase 3 browser selector (§1.3); validated with the same rules at authoring time.</summary>
public sealed class WorkflowSelector
{
    internal WorkflowSelector()
    {
    }

    public WorkflowSelectorKind Kind { get; internal init; }

    public string Value { get; internal init; } = string.Empty;

    /// <summary>Only for <see cref="WorkflowSelectorKind.Role"/>.</summary>
    public string? AccessibleName { get; internal init; }
}

/// <summary>The JSON spelling of every closed value set of schemaVersion 1 (used by the binder and parity tests).</summary>
public static class WorkflowJsonNames
{
    public static readonly ImmutableArray<KeyValuePair<string, ActionType>> ActionTypes =
    [
        new("navigate", ActionType.Navigate),
        new("click", ActionType.Click),
        new("fill", ActionType.Fill),
        new("select", ActionType.Select),
        new("wait", ActionType.Wait),
        new("extract", ActionType.Extract),
        new("screenshot", ActionType.Screenshot),
        new("upload", ActionType.Upload),
        new("download", ActionType.Download),
        new("condition", ActionType.Condition),
        new("delay", ActionType.Delay),
        new("captureResult", ActionType.CaptureResult),
        new("reconcileMatch", ActionType.ReconcileMatch),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, WorkflowParameterType>> ParameterTypes =
    [
        new("url", WorkflowParameterType.Url),
        new("string", WorkflowParameterType.Text),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, WorkflowInputType>> InputTypes =
    [
        new("string", WorkflowInputType.Text),
        new("email", WorkflowInputType.Email),
        new("integer", WorkflowInputType.WholeNumber),
        new("decimal", WorkflowInputType.DecimalNumber),
        new("boolean", WorkflowInputType.Boolean),
        new("date", WorkflowInputType.Date),
        new("file", WorkflowInputType.File),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, EvidenceLevel>> EvidenceLevels =
    [
        new("advisory", EvidenceLevel.Advisory),
        new("authoritative", EvidenceLevel.Authoritative),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, MatchKeyKind>> MatchKeys =
    [
        new("operationId", MatchKeyKind.OperationId),
        new("businessKey", MatchKeyKind.BusinessKey),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, StepErrorMode>> ErrorModes =
    [
        new("fail", StepErrorMode.Fail),
        new("continue", StepErrorMode.Continue),
    ];

    public static readonly ImmutableArray<KeyValuePair<string, IdempotencyGuarantee>> Guarantees =
    [
        new("targetEnforced", IdempotencyGuarantee.TargetEnforced),
    ];

    /// <summary>Keys that would introduce loops or regular expressions; rejected anywhere in v1 (§16 D6).</summary>
    public static readonly ImmutableArray<string> ForbiddenKeys = ["loop", "repeat", "forEach", "while", "regex", "matches"];
}
