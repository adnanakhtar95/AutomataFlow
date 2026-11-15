using System.Collections.Immutable;
using System.Text;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// A workflow definition that passed strict parsing and validation, together with its canonical form and verified
/// hash (docs/PHASE4_PLAN.md §11, §16 D5). The only way to obtain one is <see cref="Create(string, string?)"/>;
/// it is deeply immutable. The hash is computed from the canonical bytes of the very tree that was validated, so the
/// hash and the validated content cannot disagree.
/// </summary>
public sealed class ValidatedWorkflow
{
    private ValidatedWorkflow(WorkflowDefinition definition, ImmutableArray<byte> canonicalUtf8, string definitionHash)
    {
        Definition = definition;
        CanonicalUtf8 = canonicalUtf8;
        DefinitionHash = definitionHash;
    }

    public WorkflowDefinition Definition { get; }

    public int SchemaVersion => Definition.SchemaVersion;

    /// <summary>The canonical UTF-8 bytes (no BOM) the hash was computed over.</summary>
    public ImmutableArray<byte> CanonicalUtf8 { get; }

    /// <summary>The canonical form as text.</summary>
    public string CanonicalJson => Encoding.UTF8.GetString(CanonicalUtf8.AsSpan());

    /// <summary><c>sha256:</c> + 64 lowercase hex digits.</summary>
    public string DefinitionHash { get; }

    /// <summary>Validates a definition supplied as text. A non-null <paramref name="expectedHash"/> must match.</summary>
    public static WorkflowValidationResult Create(string definitionJson, string? expectedHash = null)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);

        // A UTF-16 string never has more chars than its UTF-8 encoding has bytes, so this rejects oversized input
        // before any encoding or parsing work.
        if (definitionJson.Length > WorkflowLimits.MaxDefinitionBytes)
        {
            return WorkflowValidationResult.Failure(new ValidationError("$", "json-too-large",
                $"The definition is larger than {WorkflowLimits.MaxDefinitionBytes} bytes."));
        }

        byte[] utf8;
        try
        {
            utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(definitionJson);
        }
        catch (EncoderFallbackException)
        {
            return WorkflowValidationResult.Failure(new ValidationError("$", "json-invalid-string", "The definition text is not valid UTF-16 (lone surrogate)."));
        }

        return Create(utf8, expectedHash);
    }

    /// <summary>Validates a definition supplied as UTF-8 bytes. A non-null <paramref name="expectedHash"/> must match.</summary>
    public static WorkflowValidationResult Create(ReadOnlySpan<byte> definitionUtf8, string? expectedHash = null)
    {
        if (expectedHash is not null && !Json.CanonicalJson.IsWellFormedHash(expectedHash))
        {
            return WorkflowValidationResult.Failure(new ValidationError("$", WorkflowErrorCodes.HashMalformed,
                "The expected hash must be 'sha256:' followed by 64 lowercase hex digits."));
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
            return WorkflowValidationResult.Failure(new ValidationError("$", WorkflowErrorCodes.WorkflowTampered,
                "The definition does not match its recorded hash."));
        }

        return WorkflowValidationResult.Success(new ValidatedWorkflow(bound.Definition!, ImmutableArray.Create(canonical), hash));
    }

    /// <summary>
    /// Recomputes the hash from <see cref="CanonicalUtf8"/> and compares it with <see cref="DefinitionHash"/> (the
    /// execution-start check of §11). Always true for an instance created by <see cref="Create(string, string?)"/>;
    /// it exists so the executor verifies rather than trusts.
    /// </summary>
    public bool VerifyIntegrity() =>
        string.Equals(Json.CanonicalJson.ComputeHash(CanonicalUtf8.AsSpan()), DefinitionHash, StringComparison.Ordinal);
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
