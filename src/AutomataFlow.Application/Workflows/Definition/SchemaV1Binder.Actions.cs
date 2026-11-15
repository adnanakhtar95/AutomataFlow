using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition.Json;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Strict per-action config parsers (docs/PHASE4_PLAN.md §2, §8.1; ADR-063). Every config object has a closed property
/// set (unknown keys are errors; loop/regex keys are forbidden constructs), every value is typed and bounded, and
/// templates are compiled. Reference and sink rules are applied afterwards by <see cref="WorkflowSemanticValidator"/>.
/// </summary>
internal sealed partial class SchemaV1Binder
{
    /// <summary>Extensions never allowed for uploads, even if a server lists them (§5.4).</summary>
    private static readonly string[] ExecutableExtensions =
    [
        ".exe", ".dll", ".msi", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".mjs", ".jse",
        ".wsf", ".wsh", ".hta", ".jar", ".sh", ".bash", ".py", ".pl", ".rb", ".php", ".app", ".apk", ".deb", ".rpm",
        ".lnk", ".reg", ".cpl", ".msc", ".html", ".htm", ".svg", ".xhtml",
    ];

    private static readonly string[] ConditionKeys =
    [
        "all", "any", "not", "elementExists", "elementVisible", "elementCountAtLeast", "textEquals", "textContains",
        "attributeEquals", "urlMatches", "valueEquals", "valueContains", "valueStartsWith", "isEmpty",
    ];

    internal NavigateConfig? ParseNavigate(JObject config, string path)
    {
        CheckProperties(config, path, "url", "acceptStatus");
        var text = ReadString(config, path, "url", required: true, 1, WorkflowLimits.MaxUrlTemplateChars, null, null);
        UrlTemplate? url = null;
        if (text is not null)
        {
            url = UrlTemplate.Parse(text, out var code, out var message);
            if (url is null)
            {
                _errors.Add(path + ".url", code!, message!);
            }
        }

        var statuses = ImmutableArray<int>.Empty;
        if (config.TryGet("acceptStatus", out var node))
        {
            if (node is not JArray array)
            {
                _errors.Add(path + ".acceptStatus", WorkflowErrorCodes.InvalidType, "'acceptStatus' must be an array of integers.");
            }
            else if (array.Items.Length > WorkflowLimits.MaxAcceptStatuses)
            {
                _errors.Add(path + ".acceptStatus", WorkflowErrorCodes.TooManyItems, $"'acceptStatus' allows at most {WorkflowLimits.MaxAcceptStatuses} entries.");
            }
            else
            {
                var builder = ImmutableArray.CreateBuilder<int>();
                for (var i = 0; i < array.Items.Length; i++)
                {
                    var itemPath = DiagnosticPath.Index(path + ".acceptStatus", i);
                    if (array.Items[i] is not JInteger { Value: >= WorkflowLimits.MinAcceptStatus and <= WorkflowLimits.MaxAcceptStatus } status)
                    {
                        _errors.Add(itemPath, WorkflowErrorCodes.OutOfRange, $"Each status must be an integer {WorkflowLimits.MinAcceptStatus}..{WorkflowLimits.MaxAcceptStatus}.");
                    }
                    else if (builder.Contains((int)status.Value))
                    {
                        _errors.Add(itemPath, WorkflowErrorCodes.Duplicate, "Statuses must be unique.");
                    }
                    else
                    {
                        builder.Add((int)status.Value);
                    }
                }

                statuses = builder.ToImmutable();
            }
        }

        return url is null ? null : new NavigateConfig(url, statuses);
    }

    internal ClickConfig? ParseClick(JObject config, string path)
    {
        CheckProperties(config, path, "selector", "waitForNavigation");
        var selector = ReadRequiredSelector(config, path, "selector");
        var wait = ReadBool(config, path, "waitForNavigation") ?? SchemaV1Defaults.ClickWaitForNavigation;
        return selector is null ? null : new ClickConfig(selector, wait);
    }

    internal FillConfig? ParseFill(JObject config, string path)
    {
        CheckProperties(config, path, "selector", "value");
        var selector = ReadRequiredSelector(config, path, "selector");
        var value = ReadTemplate(config, path, "value", required: true, minChars: 0);
        return selector is null || value is null ? null : new FillConfig(selector, value);
    }

    internal SelectConfig? ParseSelect(JObject config, string path)
    {
        CheckProperties(config, path, "selector", "value", "label", "index");
        var selector = ReadRequiredSelector(config, path, "selector");
        var present = SelectChoices.Where(k => config.TryGet(k, out _)).ToList();
        if (present.Count != 1)
        {
            _errors.Add(path, "select-choice", "A select needs exactly one of 'value', 'label' or 'index'.");
            return null;
        }

        if (present[0] == "index")
        {
            var index = ReadInt(config, path, "index", required: true, 0, WorkflowLimits.MaxSelectIndex);
            return selector is null || index is null ? null : new SelectConfig(selector, SelectBy.Index, null, index);
        }

        var text = ReadTemplate(config, path, present[0], required: true, minChars: 1);
        return selector is null || text is null ? null : new SelectConfig(selector, present[0] == "value" ? SelectBy.Value : SelectBy.Label, text, null);
    }

    internal WaitConfig? ParseWait(JObject config, string path)
    {
        CheckProperties(config, path, "for", "selector", "url");
        var hasFor = config.TryGet("for", out _);
        var hasUrl = config.TryGet("url", out _);
        if (hasFor == hasUrl)
        {
            _errors.Add(path, "wait-kind", "A wait needs exactly one of 'for' or 'url'.");
            return null;
        }

        if (hasUrl)
        {
            if (config.TryGet("selector", out _))
            {
                _errors.Add(path + ".selector", WorkflowErrorCodes.NotAllowed, "'selector' is not allowed with 'url'.");
            }

            var glob = ReadGlob(config, path, "url");
            return glob is null ? null : new WaitConfig(WaitFor.Url, null, glob);
        }

        var kind = ReadEnum(config, path, "for", required: true, WaitKinds);
        if (kind is null)
        {
            return null;
        }

        if (kind == WaitFor.Load)
        {
            if (config.TryGet("selector", out _))
            {
                _errors.Add(path + ".selector", WorkflowErrorCodes.NotAllowed, "'selector' is not allowed with 'for: load'.");
                return null;
            }

            return new WaitConfig(WaitFor.Load, null, null);
        }

        var selector = ReadRequiredSelector(config, path, "selector");
        return selector is null ? null : new WaitConfig(kind.Value, selector, null);
    }

    internal ExtractConfig? ParseExtract(JObject config, string path)
    {
        CheckProperties(config, path, "selector", "source", "attribute", "sensitive", "maxLength");
        var selector = ReadRequiredSelector(config, path, "selector");
        var source = ReadEnum(config, path, "source", required: true, ExtractSources);
        string? attribute = null;
        if (source == ExtractSource.Attribute)
        {
            attribute = ReadAttributeName(config, path, "attribute", required: true);
        }
        else if (config.TryGet("attribute", out _))
        {
            _errors.Add(path + ".attribute", WorkflowErrorCodes.NotAllowed, "'attribute' is only allowed with 'source: attribute'.");
        }

        var sensitive = ReadBool(config, path, "sensitive") ?? SchemaV1Defaults.ExtractSensitive;
        var maxLength = ReadInt(config, path, "maxLength", required: false, WorkflowLimits.MinExtractMaxLength, WorkflowLimits.MaxExtractMaxLength);
        if (selector is null || source is null || (source == ExtractSource.Attribute && attribute is null) || (config.TryGet("maxLength", out _) && maxLength is null))
        {
            return null;
        }

        return new ExtractConfig(selector, source.Value, attribute, sensitive, maxLength ?? SchemaV1Defaults.ExtractMaxLength);
    }

    internal ScreenshotConfig? ParseScreenshot(JObject config, string path)
    {
        CheckProperties(config, path, "label", "fullPage");
        var label = ReadTemplate(config, path, "label", required: true, minChars: 1, maxChars: WorkflowLimits.MaxScreenshotLabelChars);
        var fullPage = ReadBool(config, path, "fullPage") ?? SchemaV1Defaults.ScreenshotFullPage;
        return label is null ? null : new ScreenshotConfig(label, fullPage);
    }

    internal UploadConfig? ParseUpload(JObject config, string path)
    {
        CheckProperties(config, path, "selector", "file", "allowedExtensions", "maxBytes");
        var selector = ReadRequiredSelector(config, path, "selector");
        var file = ReadTemplate(config, path, "file", required: true, minChars: 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var extensions = ReadList(config, path, "allowedExtensions", required: true, 1, WorkflowLimits.MaxUploadExtensions, (node, itemPath) =>
        {
            if (node is not JString { Value: var value } || !IsExtension(value))
            {
                _errors.Add(itemPath, WorkflowErrorCodes.InvalidFormat, "Extensions are a dot followed by 1–10 lowercase letters or digits, e.g. '.pdf'.");
                return null;
            }

            if (Array.IndexOf(ExecutableExtensions, value) >= 0)
            {
                _errors.Add(itemPath, "upload-extension-forbidden", "Executable and script file types can never be uploaded.");
                return null;
            }

            return seen.Add(value) ? value : ReportDuplicate(itemPath);
        });

        var maxBytes = ReadInt(config, path, "maxBytes", required: false, 1, int.MaxValue);
        if (selector is null || file is null || extensions.IsEmpty || (config.TryGet("maxBytes", out _) && maxBytes is null))
        {
            return null;
        }

        return new UploadConfig(selector, file, extensions, maxBytes);
    }

    internal DownloadConfig? ParseDownload(JObject config, string path)
    {
        CheckProperties(config, path, "trigger");
        var trigger = ReadRequiredSelector(config, path, "trigger");
        return trigger is null ? null : new DownloadConfig(trigger);
    }

    internal ConditionConfig? ParseCondition(JObject config, string path, int depth)
    {
        CheckProperties(config, path, "if", "then", "else");
        ConditionExpr? expression = null;
        if (!config.TryGet("if", out var ifNode))
        {
            _errors.Add(path + ".if", WorkflowErrorCodes.Required, "'if' is required.");
        }
        else
        {
            expression = ReadConditionRoot(ifNode, path + ".if");
        }

        var then = ReadBranch(config, path, "then", required: true, depth);
        var otherwise = ReadBranch(config, path, "else", required: false, depth);
        return expression is null || then is null || otherwise is null ? null : new ConditionConfig(expression, then.Value, otherwise.Value);
    }

    internal DelayConfig? ParseDelay(JObject config, string path)
    {
        CheckProperties(config, path, "milliseconds");
        var milliseconds = ReadInt(config, path, "milliseconds", required: true, WorkflowLimits.MinDelayMilliseconds, WorkflowLimits.MaxDelayMilliseconds);
        return milliseconds is null ? null : new DelayConfig(milliseconds.Value);
    }

    internal CaptureResultConfig? ParseCaptureResult(JObject config, string path)
    {
        CheckProperties(config, path, "fields", "outcome");
        var fields = ReadFieldMap(config, path, (node, fieldPath) => node is JString text ? CompileTemplate(text.Value, fieldPath, 0, WorkflowLimits.MaxResultFieldChars) : InvalidFieldValue(fieldPath, "a template string"));
        OutcomeSpec? outcome = null;
        if (config.TryGet("outcome", out var outcomeNode))
        {
            outcome = ReadOutcome(outcomeNode, path + ".outcome");
            if (outcome is null)
            {
                return null;
            }
        }

        if (fields is null)
        {
            return null;
        }

        if (fields.Value.IsEmpty && outcome is null)
        {
            _errors.Add(path, "capture-empty", "A captureResult needs 'fields', an 'outcome', or both.");
            return null;
        }

        return new CaptureResultConfig(fields.Value, outcome);
    }

    internal ReconcileMatchConfig? ParseReconcileMatch(JObject config, string path)
    {
        CheckProperties(config, path, "rows", "key", "equals", "ignoreCase", "emptyIndicator", "maxRows", "fields");
        var rows = ReadRequiredSelector(config, path, "rows");
        var key = ReadRowKey(config, path);
        var equalsTemplate = ReadTemplate(config, path, "equals", required: true, minChars: 1);
        var ignoreCase = ReadBool(config, path, "ignoreCase") ?? SchemaV1Defaults.ReconcileIgnoreCase;
        var emptyIndicator = ReadRequiredSelector(config, path, "emptyIndicator");
        var maxRows = ReadInt(config, path, "maxRows", required: false, WorkflowLimits.MinReconcileMaxRows, WorkflowLimits.MaxReconcileMaxRows);
        var fields = ReadFieldMap(config, path, (node, fieldPath) =>
        {
            if (!AsObject(node, fieldPath, out var field))
            {
                return null;
            }

            CheckProperties(field, fieldPath, "attribute");
            return ReadAttributeName(field, fieldPath, "attribute", required: true);
        });

        if (rows is null || key is null || equalsTemplate is null || emptyIndicator is null || fields is null || (config.TryGet("maxRows", out _) && maxRows is null))
        {
            return null;
        }

        return new ReconcileMatchConfig(rows, key, equalsTemplate, ignoreCase, emptyIndicator, maxRows ?? SchemaV1Defaults.ReconcileMaxRows, fields.Value);
    }

    // ----- Conditions -----

    private ConditionExpr? ReadConditionRoot(JNode node, string path)
    {
        var predicates = 0;
        var expression = ReadCondition(node, path, 1, ref predicates);
        if (predicates > WorkflowLimits.MaxConditionPredicates)
        {
            _errors.Add(path, "condition-too-many-predicates", $"An expression may have at most {WorkflowLimits.MaxConditionPredicates} predicates.");
            return null;
        }

        return expression;
    }

    private ConditionExpr? ReadCondition(JNode node, string path, int depth, ref int predicates)
    {
        if (depth > WorkflowLimits.MaxConditionDepth)
        {
            _errors.Add(path, "condition-too-deep", $"Expressions may be nested at most {WorkflowLimits.MaxConditionDepth} levels.");
            return null;
        }

        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, ConditionKeys);
        if (obj.Properties.Length != 1)
        {
            _errors.Add(path, "condition-shape", "An expression is an object with exactly one predicate or combinator.");
            return null;
        }

        var (name, value) = (obj.Properties[0].Key, obj.Properties[0].Value);
        var valuePath = path + "." + name;
        switch (name)
        {
            case "all" or "any":
                if (value is not JArray { Items: var items })
                {
                    _errors.Add(valuePath, WorkflowErrorCodes.InvalidType, $"'{name}' must be an array of expressions.");
                    return null;
                }

                if (items.Length < 1 || items.Length > WorkflowLimits.MaxConditionChildren)
                {
                    _errors.Add(valuePath, "condition-children", $"'{name}' needs 1..{WorkflowLimits.MaxConditionChildren} children.");
                    return null;
                }

                var children = ImmutableArray.CreateBuilder<ConditionExpr>(items.Length);
                for (var i = 0; i < items.Length; i++)
                {
                    var child = ReadCondition(items[i], DiagnosticPath.Index(valuePath, i), depth + 1, ref predicates);
                    if (child is null)
                    {
                        return null;
                    }

                    children.Add(child);
                }

                return new CombinedCondition(name == "all" ? Combinator.All : Combinator.Any, children.MoveToImmutable());
            case "not":
                if (value is not JObject)
                {
                    _errors.Add(valuePath, "condition-children", "'not' takes exactly one expression (an object).");
                    return null;
                }

                var negated = ReadCondition(value, valuePath, depth + 1, ref predicates);
                return negated is null ? null : new NotCondition(negated);
        }

        predicates++;
        switch (name)
        {
            case "elementExists" or "elementVisible":
                var selector = ReadSelector(value, valuePath);
                return selector is null ? null : new ElementCondition(name == "elementExists" ? ElementCheck.Exists : ElementCheck.Visible, selector, 0);
            case "elementCountAtLeast":
            {
                if (!AsObject(value, valuePath, out var args))
                {
                    return null;
                }

                CheckProperties(args, valuePath, "selector", "count");
                var countSelector = ReadRequiredSelector(args, valuePath, "selector");
                var count = ReadInt(args, valuePath, "count", required: true, 1, WorkflowLimits.MaxElementCount);
                return countSelector is null || count is null ? null : new ElementCondition(ElementCheck.CountAtLeast, countSelector, count.Value);
            }

            case "textEquals" or "textContains":
            {
                if (!AsObject(value, valuePath, out var args))
                {
                    return null;
                }

                CheckProperties(args, valuePath, "selector", "value", "ignoreCase");
                var textSelector = ReadRequiredSelector(args, valuePath, "selector");
                var operand = ReadTemplate(args, valuePath, "value", required: true, minChars: 0);
                var ignoreCase = ReadBool(args, valuePath, "ignoreCase") ?? SchemaV1Defaults.ConditionIgnoreCase;
                return textSelector is null || operand is null ? null : new TextCondition(name == "textEquals" ? TextCheck.Equal : TextCheck.Contains, textSelector, operand, ignoreCase);
            }

            case "attributeEquals":
            {
                if (!AsObject(value, valuePath, out var args))
                {
                    return null;
                }

                CheckProperties(args, valuePath, "selector", "attribute", "value", "ignoreCase");
                var attributeSelector = ReadRequiredSelector(args, valuePath, "selector");
                var attribute = ReadAttributeName(args, valuePath, "attribute", required: true);
                var operand = ReadTemplate(args, valuePath, "value", required: true, minChars: 0);
                var ignoreCase = ReadBool(args, valuePath, "ignoreCase") ?? SchemaV1Defaults.ConditionIgnoreCase;
                return attributeSelector is null || attribute is null || operand is null ? null : new AttributeCondition(attributeSelector, attribute, operand, ignoreCase);
            }

            case "urlMatches":
                if (value is not JString pattern)
                {
                    _errors.Add(valuePath, WorkflowErrorCodes.InvalidType, "'urlMatches' must be a glob pattern string.");
                    return null;
                }

                var glob = UrlGlob.Parse(pattern.Value, out var globError);
                if (glob is null)
                {
                    _errors.Add(valuePath, "invalid-glob", $"The URL pattern is not supported ({globError}).");
                    return null;
                }

                return new UrlCondition(glob);
            case "valueEquals" or "valueContains" or "valueStartsWith":
            {
                if (!AsObject(value, valuePath, out var args))
                {
                    return null;
                }

                CheckProperties(args, valuePath, "left", "right", "ignoreCase");
                var left = ReadTemplate(args, valuePath, "left", required: true, minChars: 0);
                var right = ReadTemplate(args, valuePath, "right", required: true, minChars: 0);
                var ignoreCase = ReadBool(args, valuePath, "ignoreCase") ?? SchemaV1Defaults.ConditionIgnoreCase;
                var check = name switch { "valueEquals" => ValueCheck.Equal, "valueContains" => ValueCheck.Contains, _ => ValueCheck.StartsWith };
                return left is null || right is null ? null : new ValueCondition(check, left, right, ignoreCase);
            }

            case "isEmpty":
                if (value is not JString emptyOperand)
                {
                    _errors.Add(valuePath, WorkflowErrorCodes.InvalidType, "'isEmpty' takes a template string.");
                    return null;
                }

                var template = CompileTemplate(emptyOperand.Value, valuePath, 0, WorkflowLimits.MaxGenericStringChars);
                return template is null ? null : new IsEmptyCondition(template);
            default:
                return null; // unknown names were reported by CheckProperties
        }
    }

    // ----- Helpers -----

    private static readonly ImmutableArray<KeyValuePair<string, WaitFor>> WaitKinds =
    [
        new("visible", WaitFor.Visible), new("hidden", WaitFor.Hidden), new("attached", WaitFor.Attached), new("detached", WaitFor.Detached), new("load", WaitFor.Load),
    ];

    private static readonly string[] SelectChoices = ["value", "label", "index"];

    private static readonly ImmutableArray<KeyValuePair<string, ExtractSource>> ExtractSources = [new("text", ExtractSource.Text), new("attribute", ExtractSource.Attribute)];

    private static readonly ImmutableArray<KeyValuePair<string, OutcomeStatus>> OutcomeStatuses = [new("succeeded", OutcomeStatus.Succeeded), new("failed", OutcomeStatus.Failed)];

    private ImmutableArray<WorkflowStep>? ReadBranch(JObject config, string path, string name, bool required, int depth)
    {
        var branchPath = path + "." + name;
        if (!config.TryGet(name, out var node))
        {
            if (required)
            {
                _errors.Add(branchPath, WorkflowErrorCodes.Required, $"'{name}' is required.");
                return null;
            }

            return [];
        }

        if (node is not JArray array)
        {
            _errors.Add(branchPath, WorkflowErrorCodes.InvalidType, $"'{name}' must be an array of steps.");
            return null;
        }

        if (required && array.Items.IsEmpty)
        {
            _errors.Add(branchPath, WorkflowErrorCodes.TooFewItems, $"'{name}' needs at least one step.");
            return null;
        }

        if (depth + 1 > WorkflowLimits.MaxStepNestingDepth)
        {
            _errors.Add(branchPath, "step-nesting-too-deep", $"Condition branches may be nested at most {WorkflowLimits.MaxStepNestingDepth} levels.");
            return null;
        }

        var steps = ImmutableArray.CreateBuilder<WorkflowStep>(array.Items.Length);
        var complete = true;
        for (var i = 0; i < array.Items.Length; i++)
        {
            var step = ReadStep(array.Items[i], DiagnosticPath.Index(branchPath, i), depth + 1);
            if (step is null)
            {
                complete = false;
            }
            else
            {
                steps.Add(step);
            }
        }

        return complete ? steps.ToImmutable() : null;
    }

    private OutcomeSpec? ReadOutcome(JNode node, string path)
    {
        if (!AsObject(node, path, out var obj))
        {
            return null;
        }

        CheckProperties(obj, path, "status", "code", "transient", "transientWhen");
        var status = ReadEnum(obj, path, "status", required: true, OutcomeStatuses);
        if (status is null)
        {
            return null;
        }

        if (status == OutcomeStatus.Succeeded)
        {
            foreach (var failureOnly in (ReadOnlySpan<string>)["code", "transient", "transientWhen"])
            {
                if (obj.TryGet(failureOnly, out _))
                {
                    _errors.Add(path + "." + failureOnly, WorkflowErrorCodes.NotAllowed, $"'{failureOnly}' is only allowed on a failed outcome.");
                    return null;
                }
            }

            return new OutcomeSpec(OutcomeStatus.Succeeded, null, false, null);
        }

        var code = ReadString(obj, path, "code", required: true, 1, WorkflowLimits.MaxOutcomeCodeChars, IsOutcomeCode, "must start with an uppercase letter and contain only ASCII letters and digits");
        var transient = ReadBool(obj, path, "transient");
        ConditionExpr? transientWhen = null;
        if (obj.TryGet("transientWhen", out var whenNode))
        {
            if (transient is not null)
            {
                _errors.Add(path + ".transientWhen", WorkflowErrorCodes.NotAllowed, "Use either 'transient' or 'transientWhen', not both.");
                return null;
            }

            transientWhen = ReadConditionRoot(whenNode, path + ".transientWhen");
            if (transientWhen is null)
            {
                return null;
            }
        }

        return code is null ? null : new OutcomeSpec(OutcomeStatus.Failed, code, transient ?? SchemaV1Defaults.OutcomeTransient, transientWhen);
    }

    private RowKey? ReadRowKey(JObject config, string path)
    {
        var keyPath = path + ".key";
        if (!config.TryGet("key", out var node))
        {
            _errors.Add(keyPath, WorkflowErrorCodes.Required, "'key' is required.");
            return null;
        }

        if (!AsObject(node, keyPath, out var key))
        {
            return null;
        }

        CheckProperties(key, keyPath, "attribute", "text");
        var hasAttribute = key.TryGet("attribute", out _);
        if (hasAttribute == key.TryGet("text", out _))
        {
            _errors.Add(keyPath, "row-key-shape", "'key' needs exactly one of 'attribute' or 'text'.");
            return null;
        }

        if (hasAttribute)
        {
            var attribute = ReadAttributeName(key, keyPath, "attribute", required: true);
            return attribute is null ? null : new RowKey(attribute, null);
        }

        var child = ReadRequiredSelector(key, keyPath, "text");
        if (child is { Kind: WorkflowSelectorKind.XPath })
        {
            // XPath can leave the row (parenthesised expressions, unions, parent axes): rejected for row children.
            _errors.Add(keyPath + ".text", WorkflowErrorCodes.InvalidSelector, "XPath cannot be used for a row child selector; it can escape the row.");
            return null;
        }

        return child is null ? null : new RowKey(null, child);
    }

    /// <summary>Reads an author-named map (<c>fields</c>): names are data (any name matching the field-name rule).</summary>
    private ImmutableArray<KeyValuePair<string, T>>? ReadFieldMap<T>(JObject config, string path, Func<JNode, string, T?> readValue)
        where T : class
    {
        var mapPath = path + ".fields";
        if (!config.TryGet("fields", out var node))
        {
            return [];
        }

        if (!AsObject(node, mapPath, out var map))
        {
            return null;
        }

        if (map.Properties.Length > WorkflowLimits.MaxResultFields)
        {
            _errors.Add(mapPath, WorkflowErrorCodes.TooManyItems, $"At most {WorkflowLimits.MaxResultFields} fields.");
            return null;
        }

        var fields = ImmutableArray.CreateBuilder<KeyValuePair<string, T>>(map.Properties.Length);
        var complete = true;
        foreach (var (name, valueNode) in map.Properties)
        {
            var fieldPath = DiagnosticPath.Property(mapPath, name);
            if (!IsResultFieldName(name))
            {
                _errors.Add(fieldPath, WorkflowErrorCodes.InvalidFormat, "Field names start with an ASCII letter and contain only ASCII letters, digits and '_' (max 64).");
                complete = false;
                continue;
            }

            var value = readValue(valueNode, fieldPath);
            if (value is null)
            {
                complete = false;
                continue;
            }

            fields.Add(new(name, value));
        }

        return complete ? fields.ToImmutable() : null;
    }

    private WorkflowSelector? ReadRequiredSelector(JObject config, string path, string name)
    {
        if (!config.TryGet(name, out var node))
        {
            _errors.Add(path + "." + name, WorkflowErrorCodes.Required, $"'{name}' is required.");
            return null;
        }

        return ReadSelector(node, path + "." + name);
    }

    private Template? ReadTemplate(JObject config, string path, string name, bool required, int minChars, int maxChars = WorkflowLimits.MaxGenericStringChars)
    {
        var text = ReadString(config, path, name, required, minChars, maxChars, null, null);
        return text is null ? null : CompileTemplate(text, path + "." + name, minChars, maxChars);
    }

    private Template? CompileTemplate(string text, string path, int minChars, int maxChars)
    {
        if (!CheckLength(text, path, minChars, maxChars))
        {
            return null;
        }

        var template = Template.Parse(text, out var code, out var message);
        if (template is null)
        {
            _errors.Add(path, code!, message!);
        }

        return template;
    }

    private UrlGlob? ReadGlob(JObject config, string path, string name)
    {
        var text = ReadString(config, path, name, required: true, 1, WorkflowLimits.MaxUrlPatternChars, null, null);
        if (text is null)
        {
            return null;
        }

        var glob = UrlGlob.Parse(text, out var error);
        if (glob is null)
        {
            _errors.Add(path + "." + name, "invalid-glob", $"The URL pattern is not supported ({error}).");
        }

        return glob;
    }

    private string? ReadAttributeName(JObject config, string path, string name, bool required) =>
        ReadString(config, path, name, required, 1, WorkflowLimits.MaxAttributeNameChars, IsAttributeName,
            "must be a plain attribute name (letters, digits, '-', '_', ':' or '.')");

    private string? ReportDuplicate(string path)
    {
        _errors.Add(path, WorkflowErrorCodes.Duplicate, "Values must be unique.");
        return null;
    }

    private Template? InvalidFieldValue(string path, string expected)
    {
        _errors.Add(path, WorkflowErrorCodes.InvalidType, $"Each field value must be {expected}.");
        return null;
    }

    private static bool IsAttributeName(string value) =>
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    private static bool IsResultFieldName(string value) =>
        value.Length is > 0 and <= WorkflowLimits.MaxResultFieldNameChars && char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsOutcomeCode(string value) => char.IsAsciiLetterUpper(value[0]) && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsExtension(string value) =>
        value.Length is >= 2 and <= 11 && value[0] == '.' && value.Skip(1).All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));
}
