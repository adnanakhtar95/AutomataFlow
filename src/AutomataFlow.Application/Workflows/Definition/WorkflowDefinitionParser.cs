using System.Collections.Immutable;
using System.Globalization;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition.Json;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Binds a strictly parsed JSON tree to a <see cref="WorkflowDefinition"/>, dispatching on <c>schemaVersion</c>
/// (docs/PHASE4_PLAN.md §11). Unknown schema versions are refused; there is one binder per supported version.
/// </summary>
public static class WorkflowDefinitionParser
{
    public static DefinitionBindResult Bind(JNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is not JObject obj)
        {
            return DefinitionBindResult.Failure(new ValidationError("$", WorkflowErrorCodes.InvalidType, "The definition must be a JSON object."));
        }

        if (!obj.TryGet("schemaVersion", out var version))
        {
            return DefinitionBindResult.Failure(new ValidationError("$.schemaVersion", WorkflowErrorCodes.Required, "'schemaVersion' is required."));
        }

        if (version is not JInteger number)
        {
            return DefinitionBindResult.Failure(new ValidationError("$.schemaVersion", WorkflowErrorCodes.InvalidType, "'schemaVersion' must be an integer."));
        }

        return number.Value switch
        {
            WorkflowLimits.SupportedSchemaVersion => new SchemaV1Binder().Bind(obj),
            _ => DefinitionBindResult.Failure(new ValidationError(
                "$.schemaVersion",
                WorkflowErrorCodes.UnsupportedSchemaVersion,
                $"schemaVersion {number.Value.ToString(CultureInfo.InvariantCulture)} is not supported; supported: {WorkflowLimits.SupportedSchemaVersion}.")),
        };
    }
}

/// <summary>Either a bound definition (no errors) or every binding error found.</summary>
public sealed class DefinitionBindResult
{
    private DefinitionBindResult(WorkflowDefinition? definition, ImmutableArray<ValidationError> errors)
    {
        Definition = definition;
        Errors = errors;
    }

    public WorkflowDefinition? Definition { get; }

    public ImmutableArray<ValidationError> Errors { get; }

    public bool IsValid => Errors.IsEmpty && Definition is not null;

    internal static DefinitionBindResult Success(WorkflowDefinition definition) => new(definition, []);

    internal static DefinitionBindResult Failure(ValidationError error) => new(null, [error]);

    internal static DefinitionBindResult Failure(IEnumerable<ValidationError> errors) => new(null, [.. errors]);
}

/// <summary>Stable error codes of definition parsing and validation.</summary>
public static class WorkflowErrorCodes
{
    public const string Required = "required";
    public const string UnknownProperty = "unknown-property";
    public const string ForbiddenConstruct = "forbidden-construct";
    public const string InvalidType = "invalid-type";
    public const string InvalidValue = "invalid-value";
    public const string OutOfRange = "out-of-range";
    public const string TooShort = "too-short";
    public const string TooLong = "too-long";
    public const string TooFewItems = "too-few-items";
    public const string TooManyItems = "too-many-items";
    public const string TooManySteps = "too-many-steps";
    public const string Duplicate = "duplicate";
    public const string InvalidFormat = "invalid-format";
    public const string InvalidSelector = "invalid-selector";
    public const string UnsupportedSchemaVersion = "unsupported-schema-version";
    public const string MissingDependency = "missing-dependency";
    public const string NotAllowed = "not-allowed";
    public const string ExceedsWorkflowTimeout = "exceeds-workflow-timeout";
    public const string HashMalformed = "hash-malformed";
    public const string WorkflowTampered = "WorkflowTampered";
}

/// <summary>The schemaVersion 1 binder. Collects every error (with its JSON path) instead of stopping at the first.</summary>
internal sealed class SchemaV1Binder
{
    private static readonly string[] TopLevelProperties =
    [
        "schemaVersion", "name", "description", "timeoutSeconds", "parameters", "allowedHosts", "inputs", "secrets",
        "maskSelectors", "reconciliation", "setup", "reconcile", "steps",
    ];

    private static readonly string[] StepProperties = ["id", "type", "config", "timeoutMs", "onError", "sideEffect", "idempotency", "description"];

    private static readonly string[] SelectorKeys = ["testId", "css", "role", "text", "label", "placeholder", "xpath"];

    private readonly ValidationResult _errors = new();
    private readonly Dictionary<string, string> _stepIds = new(StringComparer.Ordinal);

    public DefinitionBindResult Bind(JObject root)
    {
        const string path = "$";
        CheckProperties(root, path, TopLevelProperties);

        var name = ReadString(root, path, "name", required: true, WorkflowLimits.MinNameChars, WorkflowLimits.MaxNameChars, Charset.IsWorkflowName,
            "must start with a lowercase letter and contain only lowercase letters, digits and '-'");
        var description = ReadText(root, path, "description", WorkflowLimits.MaxDescriptionChars);
        var timeoutSeconds = ReadInt(root, path, "timeoutSeconds", required: false, WorkflowLimits.MinTimeoutSeconds, WorkflowLimits.MaxTimeoutSeconds);
        var timeoutValid = !root.TryGet("timeoutSeconds", out _) || timeoutSeconds is not null;
        var effectiveTimeoutSeconds = timeoutSeconds ?? WorkflowLimits.DefaultTimeoutSeconds;

        var parameters = ReadParameters(root, path);
        var allowedHosts = ReadAllowedHosts(root, path);
        var inputs = ReadInputs(root, path);
        var secrets = ReadSecrets(root, path);
        var maskSelectors = ReadList(root, path, "maskSelectors", required: false, 0, WorkflowLimits.MaxMaskSelectors, ReadSelector);
        var reconciliation = ReadReconciliation(root, path);

        var hasReconcile = root.TryGet("reconcile", out _);
        if (hasReconcile && !root.TryGet("reconciliation", out _))
        {
            _errors.Add("$.reconciliation", WorkflowErrorCodes.MissingDependency, "'reconciliation' is required when 'reconcile' is present.");
        }

        if (!hasReconcile && root.TryGet("reconciliation", out _))
        {
            _errors.Add("$.reconcile", WorkflowErrorCodes.MissingDependency, "'reconcile' is required when 'reconciliation' is present.");
        }

        int? stepTimeoutCeilingMs = timeoutValid ? effectiveTimeoutSeconds * 1_000 : null;
        var (setup, reconcile, steps) = ReadSections(root, stepTimeoutCeilingMs);

        if (!_errors.IsValid)
        {
            return DefinitionBindResult.Failure(_errors.Errors);
        }

        return DefinitionBindResult.Success(new WorkflowDefinition
        {
            SchemaVersion = WorkflowLimits.SupportedSchemaVersion,
            Name = name!,
            Description = description,
            TimeoutSeconds = effectiveTimeoutSeconds,
            Parameters = parameters,
            AllowedHosts = allowedHosts,
            Inputs = inputs,
            Secrets = secrets,
            MaskSelectors = maskSelectors,
            Reconciliation = reconciliation,
            Setup = setup,
            HasReconcileSection = hasReconcile,
            Reconcile = reconcile,
            Steps = steps,
        });
    }

    // ----- Declarations -----

    private ImmutableArray<WorkflowParameter> ReadParameters(JObject root, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return ReadList(root, path, "parameters", required: false, 0, WorkflowLimits.MaxParameters, (node, itemPath) =>
        {
            if (!AsObject(node, itemPath, out var obj))
            {
                return null;
            }

            CheckProperties(obj, itemPath, "name", "type");
            var name = ReadDeclaredName(obj, itemPath, names);
            var type = ReadEnum(obj, itemPath, "type", required: true, WorkflowJsonNames.ParameterTypes);
            return name is null || type is null ? null : new WorkflowParameter { Name = name, Type = type.Value };
        });
    }

    private ImmutableArray<string> ReadAllowedHosts(JObject root, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ReadList(root, path, "allowedHosts", required: true, 1, WorkflowLimits.MaxAllowedHosts, (node, itemPath) =>
        {
            if (node is not JString text)
            {
                _errors.Add(itemPath, WorkflowErrorCodes.InvalidType, "Each allowed host must be a string.");
                return null;
            }

            if (!CheckLength(text.Value, itemPath, 1, WorkflowLimits.MaxAllowedHostChars))
            {
                return null;
            }

            if (Charset.ContainsWhitespaceOrControl(text.Value))
            {
                _errors.Add(itemPath, WorkflowErrorCodes.InvalidFormat, "An allowed host cannot contain whitespace or control characters.");
                return null;
            }

            if (!seen.Add(text.Value))
            {
                _errors.Add(itemPath, WorkflowErrorCodes.Duplicate, "Allowed hosts must be unique.");
                return null;
            }

            return text.Value;
        });
    }

    private ImmutableArray<WorkflowInput> ReadInputs(JObject root, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return ReadList(root, path, "inputs", required: false, 0, WorkflowLimits.MaxInputs, (node, itemPath) =>
        {
            if (!AsObject(node, itemPath, out var obj))
            {
                return null;
            }

            CheckProperties(obj, itemPath, "name", "type", "required", "maxLength", "sensitive");
            var name = ReadDeclaredName(obj, itemPath, names);
            var type = ReadEnum(obj, itemPath, "type", required: true, WorkflowJsonNames.InputTypes);
            var isRequired = ReadBool(obj, itemPath, "required");
            var maxLength = ReadInt(obj, itemPath, "maxLength", required: false, WorkflowLimits.MinInputMaxLength, WorkflowLimits.MaxInputMaxLength);
            var sensitive = ReadBool(obj, itemPath, "sensitive");
            if (name is null || type is null)
            {
                return null;
            }

            return new WorkflowInput
            {
                Name = name,
                Type = type.Value,
                Required = isRequired ?? false,
                MaxLength = maxLength ?? WorkflowLimits.DefaultInputMaxLength,
                Sensitive = sensitive ?? false,
            };
        });
    }

    private ImmutableArray<string> ReadSecrets(JObject root, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return ReadList(root, path, "secrets", required: false, 0, WorkflowLimits.MaxSecrets, (node, itemPath) =>
        {
            if (node is not JString text)
            {
                _errors.Add(itemPath, WorkflowErrorCodes.InvalidType, "Each secret must be a name (string).");
                return null;
            }

            return CheckDeclaredName(text.Value, itemPath, names) ? text.Value : null;
        });
    }

    private string? ReadDeclaredName(JObject obj, string path, HashSet<string> names)
    {
        if (!obj.TryGet("name", out var node))
        {
            _errors.Add(path + ".name", WorkflowErrorCodes.Required, "'name' is required.");
            return null;
        }

        if (node is not JString text)
        {
            _errors.Add(path + ".name", WorkflowErrorCodes.InvalidType, "'name' must be a string.");
            return null;
        }

        return CheckDeclaredName(text.Value, path + ".name", names) ? text.Value : null;
    }

    private bool CheckDeclaredName(string value, string path, HashSet<string> names)
    {
        if (!CheckLength(value, path, 1, WorkflowLimits.MaxDeclaredNameChars))
        {
            return false;
        }

        if (!Charset.IsDeclaredName(value))
        {
            _errors.Add(path, WorkflowErrorCodes.InvalidFormat, "A declared name must start with an ASCII letter and contain only ASCII letters and digits.");
            return false;
        }

        if (!names.Add(value))
        {
            _errors.Add(path, WorkflowErrorCodes.Duplicate, $"The name '{value}' is declared more than once.");
            return false;
        }

        return true;
    }

    private ReconciliationPolicy? ReadReconciliation(JObject root, string path)
    {
        if (!root.TryGet("reconciliation", out var node))
        {
            return null;
        }

        var policyPath = path + ".reconciliation";
        if (!AsObject(node, policyPath, out var obj))
        {
            return null;
        }

        CheckProperties(obj, policyPath, "evidence", "matchKey", "authority");
        var evidence = ReadEnum(obj, policyPath, "evidence", required: true, WorkflowJsonNames.EvidenceLevels);
        var matchKey = ReadEnum(obj, policyPath, "matchKey", required: true, WorkflowJsonNames.MatchKeys);

        ReconciliationAuthority? authority = null;
        var hasAuthority = obj.TryGet("authority", out var authorityNode);
        if (evidence == EvidenceLevel.Authoritative && !hasAuthority)
        {
            _errors.Add(policyPath + ".authority", WorkflowErrorCodes.Required, "'authority' is required for authoritative evidence.");
        }
        else if (evidence == EvidenceLevel.Advisory && hasAuthority)
        {
            _errors.Add(policyPath + ".authority", WorkflowErrorCodes.NotAllowed, "'authority' is not allowed for advisory evidence.");
        }
        else if (hasAuthority)
        {
            authority = ReadAuthority(authorityNode, policyPath + ".authority");
        }

        if (evidence is null || matchKey is null)
        {
            return null;
        }

        return new ReconciliationPolicy { Evidence = evidence.Value, MatchKey = matchKey.Value, Authority = authority };
    }

    private ReconciliationAuthority? ReadAuthority(JNode node, string path)
    {
        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, "maxCommitLatencySeconds", "contractReference", "maxEvidenceAgeSeconds");
        var latency = ReadInt(obj, path, "maxCommitLatencySeconds", required: true, WorkflowLimits.MinCommitLatencySeconds, WorkflowLimits.MaxCommitLatencySeconds);
        var reference = ReadText(obj, path, "contractReference", WorkflowLimits.MaxContractReferenceChars, required: true, WorkflowLimits.MinContractReferenceChars);
        if (reference is not null && reference.Contains("{{", StringComparison.Ordinal))
        {
            _errors.Add(path + ".contractReference", WorkflowErrorCodes.InvalidFormat, "'contractReference' cannot contain placeholders.");
            reference = null;
        }

        var evidenceAge = ReadInt(obj, path, "maxEvidenceAgeSeconds", required: false, WorkflowLimits.MinEvidenceAgeSeconds, WorkflowLimits.MaxEvidenceAgeSeconds);
        if (latency is null || reference is null || (obj.TryGet("maxEvidenceAgeSeconds", out _) && evidenceAge is null))
        {
            return null;
        }

        return new ReconciliationAuthority
        {
            MaxCommitLatencySeconds = latency.Value,
            ContractReference = reference,
            MaxEvidenceAgeSeconds = evidenceAge ?? WorkflowLimits.DefaultEvidenceAgeSeconds,
        };
    }

    // ----- Steps -----

    private (ImmutableArray<WorkflowStep> Setup, ImmutableArray<WorkflowStep> Reconcile, ImmutableArray<WorkflowStep> Steps) ReadSections(JObject root, int? stepTimeoutCeilingMs)
    {
        var total = 0;
        foreach (var section in (ReadOnlySpan<string>)["setup", "reconcile", "steps"])
        {
            if (root.TryGet(section, out var node) && node is JArray array)
            {
                total += array.Items.Length;
            }
        }

        if (total > WorkflowLimits.MaxSteps)
        {
            // Resource limit: report once and do not bind any step.
            _errors.Add("$", WorkflowErrorCodes.TooManySteps, $"The workflow has {total} steps; the maximum is {WorkflowLimits.MaxSteps}.");
            return ([], [], []);
        }

        ImmutableArray<WorkflowStep> ReadSection(string name, bool required) =>
            ReadList(root, "$", name, required, 0, WorkflowLimits.MaxSteps, (node, itemPath) => ReadStep(node, itemPath, stepTimeoutCeilingMs));

        return (ReadSection("setup", required: false), ReadSection("reconcile", required: false), ReadSection("steps", required: true));
    }

    private WorkflowStep? ReadStep(JNode node, string path, int? stepTimeoutCeilingMs)
    {
        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, StepProperties);
        var id = ReadString(obj, path, "id", required: true, 1, WorkflowLimits.MaxStepIdChars, Charset.IsStepId,
            "must start with a lowercase letter and contain only lowercase letters, digits and '-'");
        if (id is not null && !_stepIds.TryAdd(id, path))
        {
            _errors.Add(path + ".id", WorkflowErrorCodes.Duplicate, $"Step id '{id}' is already used at {_stepIds[id]}; step ids are unique across the whole workflow.");
        }

        var type = ReadEnum(obj, path, "type", required: true, WorkflowJsonNames.ActionTypes);

        JObject? config = null;
        if (!obj.TryGet("config", out var configNode))
        {
            _errors.Add(path + ".config", WorkflowErrorCodes.Required, "'config' is required.");
        }
        else if (AsObject(configNode, path + ".config", out var configObject))
        {
            config = configObject;
            RejectForbiddenKeys(configObject, path + ".config");
        }

        var timeoutMs = ReadInt(obj, path, "timeoutMs", required: false, WorkflowLimits.MinStepTimeoutMs, WorkflowLimits.MaxStepTimeoutMs);
        if (timeoutMs is not null && stepTimeoutCeilingMs is not null && timeoutMs > stepTimeoutCeilingMs)
        {
            _errors.Add(path + ".timeoutMs", WorkflowErrorCodes.ExceedsWorkflowTimeout,
                $"'timeoutMs' ({timeoutMs}) cannot exceed the workflow timeout ({stepTimeoutCeilingMs} ms).");
        }

        var onError = ReadEnum(obj, path, "onError", required: false, WorkflowJsonNames.ErrorModes);
        var sideEffect = ReadBool(obj, path, "sideEffect");
        var idempotency = obj.TryGet("idempotency", out var idempotencyNode) ? ReadIdempotency(idempotencyNode, path + ".idempotency") : null;
        var description = ReadText(obj, path, "description", WorkflowLimits.MaxDescriptionChars);

        if (id is null || type is null || config is null)
        {
            return null;
        }

        return new WorkflowStep
        {
            Id = id,
            Type = type.Value,
            Config = config,
            TimeoutMs = timeoutMs,
            OnError = onError ?? StepErrorMode.Fail,
            SideEffect = sideEffect,
            Idempotency = idempotency,
            Description = description,
        };
    }

    private IdempotencyAttestation? ReadIdempotency(JNode node, string path)
    {
        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, "key", "guarantee");
        var key = ReadString(obj, path, "key", required: true, 1, WorkflowLimits.MaxGenericStringChars, null, null);
        if (key is not null && !string.Equals(key, IdempotencyAttestation.OperationIdKey, StringComparison.Ordinal))
        {
            _errors.Add(path + ".key", WorkflowErrorCodes.InvalidValue, $"'key' must be exactly '{IdempotencyAttestation.OperationIdKey}'.");
            key = null;
        }

        var guarantee = ReadEnum(obj, path, "guarantee", required: true, WorkflowJsonNames.Guarantees);
        return key is null || guarantee is null ? null : new IdempotencyAttestation { Key = key, Guarantee = guarantee.Value };
    }

    private void RejectForbiddenKeys(JNode node, string path)
    {
        switch (node)
        {
            case JObject obj:
                foreach (var property in obj.Properties)
                {
                    var propertyPath = path + "." + property.Key;
                    if (WorkflowJsonNames.ForbiddenKeys.Contains(property.Key, StringComparer.Ordinal))
                    {
                        _errors.Add(propertyPath, WorkflowErrorCodes.ForbiddenConstruct, $"'{property.Key}' is not supported: schemaVersion 1 has no loops and no regular expressions.");
                    }

                    RejectForbiddenKeys(property.Value, propertyPath);
                }

                break;
            case JArray array:
                for (var i = 0; i < array.Items.Length; i++)
                {
                    RejectForbiddenKeys(array.Items[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                }

                break;
        }
    }

    // ----- Selectors (same rules as the Phase 3 browser selector, applied at authoring time) -----

    private WorkflowSelector? ReadSelector(JNode node, string path)
    {
        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, [.. SelectorKeys, "name"]);
        string? kindKey = null;
        var kinds = 0;
        foreach (var key in SelectorKeys)
        {
            if (obj.TryGet(key, out _))
            {
                kindKey = key;
                kinds++;
            }
        }

        if (kinds != 1)
        {
            _errors.Add(path, WorkflowErrorCodes.InvalidSelector, "A selector must have exactly one of: testId, css, role, text, label, placeholder, xpath.");
            return null;
        }

        var hasName = obj.TryGet("name", out _);
        if (hasName && kindKey != "role")
        {
            _errors.Add(path + ".name", WorkflowErrorCodes.NotAllowed, "'name' is only allowed with 'role'.");
            return null;
        }

        var kind = kindKey switch
        {
            "testId" => WorkflowSelectorKind.TestId,
            "css" => WorkflowSelectorKind.Css,
            "role" => WorkflowSelectorKind.Role,
            "text" => WorkflowSelectorKind.Text,
            "label" => WorkflowSelectorKind.Label,
            "placeholder" => WorkflowSelectorKind.Placeholder,
            _ => WorkflowSelectorKind.XPath,
        };

        var value = ReadString(obj, path, kindKey!, required: true, 1, WorkflowLimits.MaxSelectorChars, null, null);
        var accessibleName = hasName ? ReadString(obj, path, "name", required: true, 1, WorkflowLimits.MaxSelectorChars, null, null) : null;
        if (value is null || (hasName && accessibleName is null))
        {
            return null;
        }

        if (SelectorProblem(kind, value) is { } valueProblem)
        {
            _errors.Add(path + "." + kindKey, WorkflowErrorCodes.InvalidSelector, valueProblem);
            return null;
        }

        if (accessibleName is not null && SelectorTextProblem(accessibleName) is { } nameProblem)
        {
            _errors.Add(path + ".name", WorkflowErrorCodes.InvalidSelector, nameProblem);
            return null;
        }

        return new WorkflowSelector { Kind = kind, Value = value, AccessibleName = accessibleName };
    }

    private static string? SelectorProblem(WorkflowSelectorKind kind, string value)
    {
        var text = SelectorTextProblem(value);
        if (text is not null)
        {
            return text;
        }

        switch (kind)
        {
            case WorkflowSelectorKind.Css:
                if (value.Contains(">>", StringComparison.Ordinal) || Charset.HasSelectorEnginePrefix(value))
                {
                    return "CSS selectors cannot contain selector-engine prefixes or '>>' chains.";
                }

                return value.Contains("{{", StringComparison.Ordinal) ? "Placeholders are not allowed in CSS selectors." : null;
            case WorkflowSelectorKind.XPath:
                if (value.Contains(">>", StringComparison.Ordinal) || !(value.StartsWith('/') || value.StartsWith('(')))
                {
                    return "XPath selectors must start with '/' or '(' and cannot contain '>>' chains.";
                }

                return value.Contains("{{", StringComparison.Ordinal) ? "Placeholders are not allowed in XPath selectors." : null;
            case WorkflowSelectorKind.Role:
                return Charset.IsAsciiLetters(value) ? null : "Role selectors must be an ARIA role name (ASCII letters only).";
            default:
                return null;
        }
    }

    private static string? SelectorTextProblem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Selector values cannot be blank.";
        }

        return Charset.ContainsControl(value, allowLayoutWhitespace: false) ? "Selector values cannot contain control characters (including NUL and newline)." : null;
    }

    // ----- Primitive readers (each reports a precise error and returns null on failure) -----

    private void CheckProperties(JObject obj, string path, params string[] allowed)
    {
        foreach (var property in obj.Properties)
        {
            if (Array.IndexOf(allowed, property.Key) >= 0)
            {
                continue;
            }

            if (WorkflowJsonNames.ForbiddenKeys.Contains(property.Key, StringComparer.Ordinal))
            {
                _errors.Add(path + "." + property.Key, WorkflowErrorCodes.ForbiddenConstruct, $"'{property.Key}' is not supported: schemaVersion 1 has no loops and no regular expressions.");
            }
            else
            {
                _errors.Add(path + "." + property.Key, WorkflowErrorCodes.UnknownProperty, $"Unknown property '{property.Key}'.");
            }
        }
    }

    private bool AsObject(JNode node, string path, out JObject obj)
    {
        if (node is JObject value)
        {
            obj = value;
            return true;
        }

        _errors.Add(path, WorkflowErrorCodes.InvalidType, "Expected a JSON object.");
        obj = null!;
        return false;
    }

    private ImmutableArray<T> ReadList<T>(JObject obj, string path, string name, bool required, int minItems, int maxItems, Func<JNode, string, T?> readItem)
        where T : class
    {
        var listPath = path + "." + name;
        if (!obj.TryGet(name, out var node))
        {
            if (required)
            {
                _errors.Add(listPath, WorkflowErrorCodes.Required, $"'{name}' is required.");
            }

            return [];
        }

        if (node is not JArray array)
        {
            _errors.Add(listPath, WorkflowErrorCodes.InvalidType, $"'{name}' must be an array.");
            return [];
        }

        if (array.Items.Length < minItems)
        {
            _errors.Add(listPath, WorkflowErrorCodes.TooFewItems, $"'{name}' needs at least {minItems} item(s).");
            return [];
        }

        if (array.Items.Length > maxItems)
        {
            // Resource limit: report and do not process the items.
            _errors.Add(listPath, WorkflowErrorCodes.TooManyItems, $"'{name}' has {array.Items.Length} items; the maximum is {maxItems}.");
            return [];
        }

        var items = ImmutableArray.CreateBuilder<T>(array.Items.Length);
        for (var i = 0; i < array.Items.Length; i++)
        {
            var item = readItem(array.Items[i], listPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items.Count == items.Capacity ? items.MoveToImmutable() : items.ToImmutable();
    }

    private string? ReadString(JObject obj, string path, string name, bool required, int minChars, int maxChars, Func<string, bool>? format, string? formatMessage)
    {
        var propertyPath = path + "." + name;
        if (!obj.TryGet(name, out var node))
        {
            if (required)
            {
                _errors.Add(propertyPath, WorkflowErrorCodes.Required, $"'{name}' is required.");
            }

            return null;
        }

        if (node is not JString text)
        {
            _errors.Add(propertyPath, WorkflowErrorCodes.InvalidType, $"'{name}' must be a string.");
            return null;
        }

        if (!CheckLength(text.Value, propertyPath, minChars, maxChars))
        {
            return null;
        }

        if (format is not null && !format(text.Value))
        {
            _errors.Add(propertyPath, WorkflowErrorCodes.InvalidFormat, $"'{name}' {formatMessage}.");
            return null;
        }

        return text.Value;
    }

    /// <summary>Free text (descriptions, contract references): tab, CR and LF are allowed; other control characters are not.</summary>
    private string? ReadText(JObject obj, string path, string name, int maxChars, bool required = false, int minChars = 0)
    {
        var value = ReadString(obj, path, name, required, minChars, maxChars, null, null);
        if (value is not null && Charset.ContainsControl(value, allowLayoutWhitespace: true))
        {
            _errors.Add(path + "." + name, WorkflowErrorCodes.InvalidFormat, $"'{name}' cannot contain control characters.");
            return null;
        }

        return value;
    }

    private int? ReadInt(JObject obj, string path, string name, bool required, int min, int max)
    {
        var propertyPath = path + "." + name;
        if (!obj.TryGet(name, out var node))
        {
            if (required)
            {
                _errors.Add(propertyPath, WorkflowErrorCodes.Required, $"'{name}' is required.");
            }

            return null;
        }

        if (node is not JInteger number)
        {
            _errors.Add(propertyPath, WorkflowErrorCodes.InvalidType, $"'{name}' must be an integer.");
            return null;
        }

        if (number.Value < min || number.Value > max)
        {
            _errors.Add(propertyPath, WorkflowErrorCodes.OutOfRange, $"'{name}' must be between {min} and {max}.");
            return null;
        }

        return (int)number.Value;
    }

    private bool? ReadBool(JObject obj, string path, string name)
    {
        if (!obj.TryGet(name, out var node))
        {
            return null;
        }

        if (node is not JBoolean value)
        {
            _errors.Add(path + "." + name, WorkflowErrorCodes.InvalidType, $"'{name}' must be true or false.");
            return null;
        }

        return value.Value;
    }

    private T? ReadEnum<T>(JObject obj, string path, string name, bool required, ImmutableArray<KeyValuePair<string, T>> names)
        where T : struct, Enum
    {
        var value = ReadString(obj, path, name, required, 1, WorkflowLimits.MaxGenericStringChars, null, null);
        if (value is null)
        {
            return null;
        }

        foreach (var pair in names)
        {
            if (string.Equals(pair.Key, value, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        _errors.Add(path + "." + name, WorkflowErrorCodes.InvalidValue, $"'{name}' must be one of: {string.Join(", ", names.Select(p => p.Key))}.");
        return null;
    }

    private bool CheckLength(string value, string path, int minChars, int maxChars)
    {
        if (value.Length < minChars)
        {
            _errors.Add(path, WorkflowErrorCodes.TooShort, $"Must be at least {minChars} character(s).");
            return false;
        }

        if (value.Length > maxChars)
        {
            _errors.Add(path, WorkflowErrorCodes.TooLong, $"Must be at most {maxChars} characters.");
            return false;
        }

        return true;
    }
}

/// <summary>Hand-written character-class checks (no regular expressions in the workflow engine, §16 D6).</summary>
internal static class Charset
{
    /// <summary><c>^[a-z][a-z0-9-]{2,63}$</c> (length is checked separately).</summary>
    public static bool IsWorkflowName(string value) => IsLowerKebab(value);

    /// <summary><c>^[a-z][a-z0-9-]{0,39}$</c> (length is checked separately).</summary>
    public static bool IsStepId(string value) => IsLowerKebab(value);

    /// <summary><c>^[A-Za-z][A-Za-z0-9]{0,63}$</c> (length is checked separately).</summary>
    public static bool IsDeclaredName(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAsciiLetters(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    public static bool ContainsControl(string value, bool allowLayoutWhitespace)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) && !(allowLayoutWhitespace && c is '\t' or '\n' or '\r'))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Same rule as the Phase 3 browser selector: a leading <c>name=</c> would switch Playwright selector engines.</summary>
    public static bool HasSelectorEnginePrefix(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return false;
        }

        foreach (var c in value.AsSpan(0, equals))
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerKebab(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetterLower(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
