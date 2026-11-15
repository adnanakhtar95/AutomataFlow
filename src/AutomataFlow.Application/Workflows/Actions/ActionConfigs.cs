using System.Collections.Immutable;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;

namespace AutomataFlow.Application.Workflows.Actions;

/// <summary>
/// The typed configuration of one action (docs/PHASE4_PLAN.md §2). One sealed class per action; instances are created
/// only by the action catalog's strict parsers and are immutable. Templates are compiled; their references and sinks are
/// checked by the semantic validator.
/// </summary>
public abstract class ActionConfig
{
    private protected ActionConfig()
    {
    }
}

public sealed class NavigateConfig : ActionConfig
{
    internal NavigateConfig(UrlTemplate url, ImmutableArray<int> acceptStatus)
    {
        Url = url;
        AcceptStatus = acceptStatus;
    }

    public UrlTemplate Url { get; }

    /// <summary>4xx/5xx statuses that do not fail the step.</summary>
    public ImmutableArray<int> AcceptStatus { get; }
}

public sealed class ClickConfig : ActionConfig
{
    internal ClickConfig(WorkflowSelector selector, bool waitForNavigation)
    {
        Selector = selector;
        WaitForNavigation = waitForNavigation;
    }

    public WorkflowSelector Selector { get; }

    public bool WaitForNavigation { get; }
}

public sealed class FillConfig : ActionConfig
{
    internal FillConfig(WorkflowSelector selector, Template value)
    {
        Selector = selector;
        Value = value;
    }

    public WorkflowSelector Selector { get; }

    public Template Value { get; }
}

public enum SelectBy
{
    Value,
    Label,
    Index,
}

public sealed class SelectConfig : ActionConfig
{
    internal SelectConfig(WorkflowSelector selector, SelectBy by, Template? text, int? index)
    {
        Selector = selector;
        By = by;
        Text = text;
        Index = index;
    }

    public WorkflowSelector Selector { get; }

    public SelectBy By { get; }

    /// <summary>The value or label template (<see cref="SelectBy.Value"/> / <see cref="SelectBy.Label"/>).</summary>
    public Template? Text { get; }

    public int? Index { get; }
}

public enum WaitFor
{
    Visible,
    Hidden,
    Attached,
    Detached,
    Load,
    Url,
}

public sealed class WaitConfig : ActionConfig
{
    internal WaitConfig(WaitFor kind, WorkflowSelector? selector, UrlGlob? url)
    {
        Kind = kind;
        Selector = selector;
        Url = url;
    }

    public WaitFor Kind { get; }

    public WorkflowSelector? Selector { get; }

    public UrlGlob? Url { get; }
}

public enum ExtractSource
{
    Text,
    Attribute,
}

public sealed class ExtractConfig : ActionConfig
{
    internal ExtractConfig(WorkflowSelector selector, ExtractSource source, string? attribute, bool sensitive, int maxLength)
    {
        Selector = selector;
        Source = source;
        Attribute = attribute;
        Sensitive = sensitive;
        MaxLength = maxLength;
    }

    public WorkflowSelector Selector { get; }

    public ExtractSource Source { get; }

    public string? Attribute { get; }

    public bool Sensitive { get; }

    public int MaxLength { get; }
}

public sealed class ScreenshotConfig : ActionConfig
{
    internal ScreenshotConfig(Template label, bool fullPage)
    {
        Label = label;
        FullPage = fullPage;
    }

    public Template Label { get; }

    public bool FullPage { get; }
}

public sealed class UploadConfig : ActionConfig
{
    internal UploadConfig(WorkflowSelector selector, Template file, ImmutableArray<string> allowedExtensions, long? maxBytes)
    {
        Selector = selector;
        File = file;
        AllowedExtensions = allowedExtensions;
        MaxBytes = maxBytes;
    }

    public WorkflowSelector Selector { get; }

    /// <summary>Exactly one <c>{{record.x}}</c> of a <c>file</c> input.</summary>
    public Template File { get; }

    /// <summary>Lowercase extensions with the dot (e.g. <c>.pdf</c>); a subset of the server list (checked at binding).</summary>
    public ImmutableArray<string> AllowedExtensions { get; }

    /// <summary>Null means the server maximum (checked at binding).</summary>
    public long? MaxBytes { get; }
}

public sealed class DownloadConfig : ActionConfig
{
    internal DownloadConfig(WorkflowSelector trigger) => Trigger = trigger;

    public WorkflowSelector Trigger { get; }
}

public sealed class ConditionConfig : ActionConfig
{
    internal ConditionConfig(ConditionExpr condition, ImmutableArray<WorkflowStep> then, ImmutableArray<WorkflowStep> @else)
    {
        If = condition;
        Then = then;
        Else = @else;
    }

    public ConditionExpr If { get; }

    /// <summary>At least one step.</summary>
    public ImmutableArray<WorkflowStep> Then { get; }

    /// <summary>May be empty.</summary>
    public ImmutableArray<WorkflowStep> Else { get; }
}

public sealed class DelayConfig : ActionConfig
{
    internal DelayConfig(int milliseconds) => Milliseconds = milliseconds;

    public int Milliseconds { get; }
}

public enum OutcomeStatus
{
    Succeeded,
    Failed,
}

/// <summary>A declared terminal outcome (§16 D3). <see cref="Code"/>, <see cref="Transient"/> and <see cref="TransientWhen"/> exist only for failures.</summary>
public sealed class OutcomeSpec
{
    internal OutcomeSpec(OutcomeStatus status, string? code, bool transient, ConditionExpr? transientWhen)
    {
        Status = status;
        Code = code;
        Transient = transient;
        TransientWhen = transientWhen;
    }

    public OutcomeStatus Status { get; }

    public string? Code { get; }

    /// <summary>Static transience when <see cref="TransientWhen"/> is null (frozen default <c>false</c>).</summary>
    public bool Transient { get; }

    /// <summary>Transience decided at run time by a condition.</summary>
    public ConditionExpr? TransientWhen { get; }
}

public sealed class CaptureResultConfig : ActionConfig
{
    internal CaptureResultConfig(ImmutableArray<KeyValuePair<string, Template>> fields, OutcomeSpec? outcome)
    {
        Fields = fields;
        Outcome = outcome;
    }

    public ImmutableArray<KeyValuePair<string, Template>> Fields { get; }

    /// <summary>Present on a terminal capture.</summary>
    public OutcomeSpec? Outcome { get; }

    public bool IsTerminal => Outcome is not null;
}

/// <summary>How a row's key is read: an attribute of the row, or the text of a descendant (never XPath).</summary>
public sealed class RowKey
{
    internal RowKey(string? attribute, WorkflowSelector? text)
    {
        Attribute = attribute;
        Text = text;
    }

    public string? Attribute { get; }

    public WorkflowSelector? Text { get; }
}

public sealed class ReconcileMatchConfig : ActionConfig
{
    internal ReconcileMatchConfig(WorkflowSelector rows, RowKey key, Template equalsTemplate, bool ignoreCase, WorkflowSelector emptyIndicator, int maxRows, ImmutableArray<KeyValuePair<string, string>> fields)
    {
        Rows = rows;
        Key = key;
        EqualsTemplate = equalsTemplate;
        IgnoreCase = ignoreCase;
        EmptyIndicator = emptyIndicator;
        MaxRows = maxRows;
        Fields = fields;
    }

    public WorkflowSelector Rows { get; }

    public RowKey Key { get; }

    /// <summary>The <c>equals</c> template.</summary>
    public Template EqualsTemplate { get; }

    public bool IgnoreCase { get; }

    public WorkflowSelector EmptyIndicator { get; }

    public int MaxRows { get; }

    /// <summary>Result field name → row attribute.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Fields { get; }
}

// ----- Conditions (§8.1): a closed set; no scripting, no regular expressions -----

public abstract class ConditionExpr
{
    private protected ConditionExpr()
    {
    }
}

public enum Combinator
{
    All,
    Any,
}

public sealed class CombinedCondition : ConditionExpr
{
    internal CombinedCondition(Combinator kind, ImmutableArray<ConditionExpr> children)
    {
        Kind = kind;
        Children = children;
    }

    public Combinator Kind { get; }

    public ImmutableArray<ConditionExpr> Children { get; }
}

public sealed class NotCondition : ConditionExpr
{
    internal NotCondition(ConditionExpr child) => Child = child;

    public ConditionExpr Child { get; }
}

public enum ElementCheck
{
    Exists,
    Visible,
    CountAtLeast,
}

public sealed class ElementCondition : ConditionExpr
{
    internal ElementCondition(ElementCheck check, WorkflowSelector selector, int count)
    {
        Check = check;
        Selector = selector;
        Count = count;
    }

    public ElementCheck Check { get; }

    public WorkflowSelector Selector { get; }

    /// <summary>For <see cref="ElementCheck.CountAtLeast"/>.</summary>
    public int Count { get; }
}

public enum TextCheck
{
    Equal,
    Contains,
}

/// <summary><c>textEquals</c> / <c>textContains</c>: the element's trimmed text against a template operand.</summary>
public sealed class TextCondition : ConditionExpr
{
    internal TextCondition(TextCheck check, WorkflowSelector selector, Template value, bool ignoreCase)
    {
        Check = check;
        Selector = selector;
        Value = value;
        IgnoreCase = ignoreCase;
    }

    public TextCheck Check { get; }

    public WorkflowSelector Selector { get; }

    public Template Value { get; }

    public bool IgnoreCase { get; }
}

public sealed class AttributeCondition : ConditionExpr
{
    internal AttributeCondition(WorkflowSelector selector, string attribute, Template value, bool ignoreCase)
    {
        Selector = selector;
        Attribute = attribute;
        Value = value;
        IgnoreCase = ignoreCase;
    }

    public WorkflowSelector Selector { get; }

    public string Attribute { get; }

    public Template Value { get; }

    public bool IgnoreCase { get; }
}

public sealed class UrlCondition : ConditionExpr
{
    internal UrlCondition(UrlGlob pattern) => Pattern = pattern;

    public UrlGlob Pattern { get; }
}

public enum ValueCheck
{
    Equal,
    Contains,
    StartsWith,
}

public sealed class ValueCondition : ConditionExpr
{
    internal ValueCondition(ValueCheck check, Template left, Template right, bool ignoreCase)
    {
        Check = check;
        Left = left;
        Right = right;
        IgnoreCase = ignoreCase;
    }

    public ValueCheck Check { get; }

    public Template Left { get; }

    public Template Right { get; }

    public bool IgnoreCase { get; }
}

public sealed class IsEmptyCondition : ConditionExpr
{
    internal IsEmptyCondition(Template value) => Value = value;

    public Template Value { get; }
}
