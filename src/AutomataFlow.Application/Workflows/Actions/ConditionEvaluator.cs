using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;

namespace AutomataFlow.Application.Workflows.Actions;

/// <summary>A selector whose placeholders have been resolved (non-sensitive by the sink rules).</summary>
public sealed record ResolvedSelector(WorkflowSelectorKind Kind, string Value, string? AccessibleName);

/// <summary>
/// Read-only page access used by conditions (§8.1). The Automation side implements it over the browser session
/// (<c>CountAsync</c>, <c>IsVisibleAsync</c>, <c>CurrentUrl</c>, <c>GetTextAsync</c>, <c>GetAttributeAsync</c>); missing
/// elements give 0, false or null rather than errors.
/// </summary>
public interface IPageProbe
{
    string CurrentUrl { get; }

    Task<int> CountAsync(ResolvedSelector selector, CancellationToken cancellationToken);

    Task<bool> IsVisibleAsync(ResolvedSelector selector, CancellationToken cancellationToken);

    /// <summary>The element's text, or null when there is no element.</summary>
    Task<string?> GetTextAsync(ResolvedSelector selector, CancellationToken cancellationToken);

    /// <summary>The attribute value, or null when there is no element or no such attribute.</summary>
    Task<string?> GetAttributeAsync(ResolvedSelector selector, string attribute, CancellationToken cancellationToken);
}

/// <summary>The boolean result of a condition, or the engine error that prevented evaluation.</summary>
public readonly record struct ConditionResult(bool Value, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}

/// <summary>
/// Pure evaluation of a <see cref="ConditionExpr"/> (§8.1): short-circuit, left to right; ordinal comparison with
/// optional ignore-case; element text is trimmed, template operands are not. No scripting and no regular expressions.
/// Only the boolean result may be logged by the caller, never the compared values.
/// </summary>
public static class ConditionEvaluator
{
    public static async Task<ConditionResult> CheckAsync(
        ConditionExpr expression,
        IPageProbe probe,
        ITemplateValues values,
        IReadOnlyDictionary<string, WorkflowInput> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(inputs);
        var context = new Context(probe, values, inputs);
        try
        {
            return new ConditionResult(await context.EvaluateAsync(expression, cancellationToken).ConfigureAwait(false), null);
        }
        catch (UnresolvedException ex)
        {
            return new ConditionResult(false, ex.Code);
        }
    }

    private sealed class UnresolvedException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private sealed class Context(IPageProbe probe, ITemplateValues values, IReadOnlyDictionary<string, WorkflowInput> inputs)
    {
        public async Task<bool> EvaluateAsync(ConditionExpr expression, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            switch (expression)
            {
                case CombinedCondition { Kind: Combinator.All } all:
                    foreach (var child in all.Children)
                    {
                        if (!await EvaluateAsync(child, ct).ConfigureAwait(false))
                        {
                            return false;
                        }
                    }

                    return true;
                case CombinedCondition any:
                    foreach (var child in any.Children)
                    {
                        if (await EvaluateAsync(child, ct).ConfigureAwait(false))
                        {
                            return true;
                        }
                    }

                    return false;
                case NotCondition not:
                    return !await EvaluateAsync(not.Child, ct).ConfigureAwait(false);
                case ElementCondition { Check: ElementCheck.Visible } visible:
                    return await probe.IsVisibleAsync(Resolve(visible.Selector), ct).ConfigureAwait(false);
                case ElementCondition element:
                    var count = await probe.CountAsync(Resolve(element.Selector), ct).ConfigureAwait(false);
                    return count >= (element.Check == ElementCheck.Exists ? 1 : element.Count);
                case TextCondition text:
                    var pageText = await probe.GetTextAsync(Resolve(text.Selector), ct).ConfigureAwait(false);
                    if (pageText is null)
                    {
                        return false;
                    }

                    var expected = Resolve(text.Value, TemplateSink.ConditionOperand);
                    var comparison = text.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    return text.Check == TextCheck.Equal
                        ? string.Equals(pageText.Trim(), expected, comparison)
                        : pageText.Trim().Contains(expected, comparison);
                case AttributeCondition attribute:
                    var actual = await probe.GetAttributeAsync(Resolve(attribute.Selector), attribute.Attribute, ct).ConfigureAwait(false);
                    return actual is not null && string.Equals(actual, Resolve(attribute.Value, TemplateSink.ConditionOperand),
                        attribute.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                case UrlCondition url:
                    return url.Pattern.IsMatch(probe.CurrentUrl);
                case ValueCondition value:
                    var left = Resolve(value.Left, TemplateSink.ConditionOperand);
                    var right = Resolve(value.Right, TemplateSink.ConditionOperand);
                    var valueComparison = value.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    return value.Check switch
                    {
                        ValueCheck.Equal => string.Equals(left, right, valueComparison),
                        ValueCheck.Contains => left.Contains(right, valueComparison),
                        _ => left.StartsWith(right, valueComparison),
                    };
                case IsEmptyCondition empty:
                    return Resolve(empty.Value, TemplateSink.ConditionOperand).Length == 0;
                default:
                    throw new InvalidOperationException("Unknown condition type.");
            }
        }

        private string Resolve(Template template, TemplateSink sink)
        {
            var resolution = TemplateResolver.Resolve(template, sink, values, inputs);
            return resolution.Succeeded ? resolution.Value!.Text : throw new UnresolvedException(resolution.ErrorCode!);
        }

        private ResolvedSelector Resolve(WorkflowSelector selector)
        {
            string ResolveText(string text)
            {
                var template = Template.Parse(text, out _, out _) ?? throw new UnresolvedException(EngineErrorCodes.VariableUnresolved);
                var resolved = Resolve(template, TemplateSink.SelectorValue);
                return resolved.Length == 0 ? throw new UnresolvedException(EngineErrorCodes.VariableUnresolved) : resolved;
            }

            var templated = selector.Kind is WorkflowSelectorKind.TestId or WorkflowSelectorKind.Text or WorkflowSelectorKind.Label or WorkflowSelectorKind.Placeholder;
            var value = templated ? ResolveText(selector.Value) : selector.Value;
            var name = selector.AccessibleName is null ? null : ResolveText(selector.AccessibleName);
            return new ResolvedSelector(selector.Kind, value, name);
        }
    }
}
