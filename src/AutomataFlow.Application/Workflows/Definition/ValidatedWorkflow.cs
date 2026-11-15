using System.Collections.Immutable;
using System.Text;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// A workflow definition that passed strict parsing and validation, together with its canonical form and hash
/// (docs/PHASE4_PLAN.md §11, §16 D5, ADR-061). It is deeply immutable and can be obtained only two ways:
/// <list type="bullet">
/// <item><see cref="CreateForPublication(string)"/>: authoring and publishing. The hash is <em>computed</em> and is what the
/// caller stores with the new version.</item>
/// <item><see cref="Load(string, string)"/>: every use of a stored definition. The recorded hash is <em>mandatory</em>;
/// the definition is re-parsed and re-validated, the hash recomputed and a mismatch refused (<c>WorkflowTampered</c>).</item>
/// </list>
/// The hash is computed from the canonical bytes of the very tree that was validated, so the hash and the validated
/// content cannot disagree.
/// </summary>
public sealed class ValidatedWorkflow
{
    private ValidatedWorkflow(WorkflowDefinition definition, ImmutableArray<byte> canonicalUtf8, string definitionHash, ImmutableArray<ValidationError> warnings)
    {
        Definition = definition;
        CanonicalUtf8 = canonicalUtf8;
        DefinitionHash = definitionHash;
        Warnings = warnings;
    }

    public WorkflowDefinition Definition { get; }

    public int SchemaVersion => Definition.SchemaVersion;

    /// <summary>The canonical UTF-8 bytes (no BOM) the hash was computed over.</summary>
    public ImmutableArray<byte> CanonicalUtf8 { get; }

    /// <summary>The canonical form as text.</summary>
    public string CanonicalJson => Encoding.UTF8.GetString(CanonicalUtf8.AsSpan());

    /// <summary><c>sha256:</c> + 64 lowercase hex digits.</summary>
    public string DefinitionHash { get; }

    /// <summary>Authoring warnings (never errors; e.g. sensitive data without <c>maskSelectors</c>, §5.5).</summary>
    public ImmutableArray<ValidationError> Warnings { get; }

    /// <summary>Validates a new definition for publication and computes its hash.</summary>
    public static WorkflowValidationResult CreateForPublication(string definitionJson)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        return EncodeThen(definitionJson, bytes => Validate(bytes, expectedHash: null));
    }

    /// <inheritdoc cref="CreateForPublication(string)"/>
    public static WorkflowValidationResult CreateForPublication(ReadOnlySpan<byte> definitionUtf8) => Validate(definitionUtf8, expectedHash: null);

    /// <summary>
    /// Loads a stored definition. <paramref name="recordedHash"/> is the hash recorded at publication and is required;
    /// a malformed hash is refused before parsing and a mismatch is refused as <c>WorkflowTampered</c>.
    /// </summary>
    public static WorkflowValidationResult Load(string definitionJson, string recordedHash)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        ArgumentNullException.ThrowIfNull(recordedHash);
        return EncodeThen(definitionJson, bytes => Validate(bytes, recordedHash));
    }

    /// <inheritdoc cref="Load(string, string)"/>
    public static WorkflowValidationResult Load(ReadOnlySpan<byte> definitionUtf8, string recordedHash)
    {
        ArgumentNullException.ThrowIfNull(recordedHash);
        return Validate(definitionUtf8, recordedHash);
    }

    /// <summary>
    /// The execution-start check (§11): recomputes the hash from <see cref="CanonicalUtf8"/> and compares it with a hash
    /// recorded <b>outside</b> this object, i.e. the hash stored for the workflow version the job pinned. Returns false on
    /// any difference, including a malformed <paramref name="recordedHash"/>.
    /// </summary>
    public bool VerifyAgainst(string recordedHash) =>
        Json.CanonicalJson.IsWellFormedHash(recordedHash)
        && string.Equals(Json.CanonicalJson.ComputeHash(CanonicalUtf8.AsSpan()), recordedHash, StringComparison.Ordinal);

    /// <summary>
    /// Self-consistency only: recomputes the hash from <see cref="CanonicalUtf8"/> and compares it with this object's
    /// own <see cref="DefinitionHash"/>. It detects in-process corruption, not a substituted definition; the execution
    /// start must use <see cref="VerifyAgainst(string)"/> with the externally recorded hash.
    /// </summary>
    public bool VerifyIntegrity() => VerifyAgainst(DefinitionHash);

    private static WorkflowValidationResult EncodeThen(string definitionJson, Func<byte[], WorkflowValidationResult> validate)
    {
        // A UTF-16 string never has more chars than its UTF-8 encoding has bytes, so this rejects oversized input
        // before any encoding or parsing work.
        if (definitionJson.Length > WorkflowLimits.MaxDefinitionBytes)
        {
            return WorkflowValidationResult.Failure(new ValidationError(DiagnosticPath.Root, "json-too-large",
                $"The definition is larger than {WorkflowLimits.MaxDefinitionBytes} bytes."));
        }

        byte[] utf8;
        try
        {
            utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(definitionJson);
        }
        catch (EncoderFallbackException)
        {
            return WorkflowValidationResult.Failure(new ValidationError(DiagnosticPath.Root, "json-invalid-string", "The definition text is not valid UTF-16 (lone surrogate)."));
        }

        return validate(utf8);
    }

    private static WorkflowValidationResult Validate(ReadOnlySpan<byte> definitionUtf8, string? expectedHash)
    {
        if (expectedHash is not null && !Json.CanonicalJson.IsWellFormedHash(expectedHash))
        {
            return WorkflowValidationResult.Failure(new ValidationError(DiagnosticPath.Root, WorkflowErrorCodes.HashMalformed,
                "The recorded hash must be 'sha256:' followed by 64 lowercase hex digits."));
        }

        var parsed = StrictJsonParser.Parse(definitionUtf8);
        if (!parsed.IsValid)
        {
            return WorkflowValidationResult.Failure(parsed.Error!);
        }

        var bound = WorkflowDefinitionParser.Bind(parsed.Root!);
        if (!bound.IsValid)
        {
            return WorkflowValidationResult.Failure(bound.Errors);
        }

        var canonical = Json.CanonicalJson.Serialize(parsed.Root!);
        var hash = Json.CanonicalJson.ComputeHash(canonical);
        if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.Ordinal))
        {
            return WorkflowValidationResult.Failure(new ValidationError(DiagnosticPath.Root, WorkflowErrorCodes.WorkflowTampered,
                "The definition does not match its recorded hash."));
        }

        return WorkflowValidationResult.Success(new ValidatedWorkflow(bound.Definition!, ImmutableArray.Create(canonical), hash, bound.Warnings));
    }
}

/// <summary>Either a <see cref="ValidatedWorkflow"/> (no errors) or the errors that prevented one.</summary>
public sealed class WorkflowValidationResult
{
    private WorkflowValidationResult(ValidatedWorkflow? workflow, ImmutableArray<ValidationError> errors)
    {
        Workflow = workflow;
        Errors = errors;
    }

    public ValidatedWorkflow? Workflow { get; }

    public ImmutableArray<ValidationError> Errors { get; }

    public bool IsValid => Workflow is not null;

    internal static WorkflowValidationResult Success(ValidatedWorkflow workflow) => new(workflow, []);

    internal static WorkflowValidationResult Failure(ValidationError error) => new(null, [error]);

    internal static WorkflowValidationResult Failure(IEnumerable<ValidationError> errors) => new(null, [.. errors]);
}
