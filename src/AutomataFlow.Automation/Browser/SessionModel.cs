using AutomataFlow.Application.Execution;
using AutomataFlow.Automation.Network;

namespace AutomataFlow.Automation.Browser;

public enum SelectorKind
{
    TestId,
    Css,
    Role,
    Text,
    Label,
    Placeholder,
    XPath,
}

/// <summary>
/// A typed element selector. Selectors are data, never code: they are mapped to Playwright locators and are never
/// evaluated as JavaScript. Custom selector engines are never registered.
/// </summary>
public sealed record ElementSelector
{
    public const int MaxLength = 500;

    private ElementSelector(SelectorKind kind, string value, string? accessibleName)
    {
        Kind = kind;
        Value = value;
        AccessibleName = accessibleName;
    }

    public SelectorKind Kind { get; }

    public string Value { get; }

    /// <summary>Accessible name for <see cref="SelectorKind.Role"/> selectors.</summary>
    public string? AccessibleName { get; }

    public static ElementSelector TestId(string value) => Create(SelectorKind.TestId, value, null);

    public static ElementSelector Css(string value) => Create(SelectorKind.Css, value, null);

    public static ElementSelector Role(string role, string? name = null) => Create(SelectorKind.Role, role, name);

    public static ElementSelector Text(string value) => Create(SelectorKind.Text, value, null);

    public static ElementSelector Label(string value) => Create(SelectorKind.Label, value, null);

    public static ElementSelector Placeholder(string value) => Create(SelectorKind.Placeholder, value, null);

    public static ElementSelector XPath(string value) => Create(SelectorKind.XPath, value, null);

    public static ElementSelector Create(SelectorKind kind, string value, string? accessibleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown selector kind.");
        }

        if (value.Length > MaxLength || (accessibleName?.Length ?? 0) > MaxLength)
        {
            throw new ArgumentException($"Selectors are limited to {MaxLength} characters.", nameof(value));
        }

        if (value.Contains('\0', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Selectors cannot contain NUL or newline characters.", nameof(value));
        }

        // CSS/XPath strings must not switch Playwright selector engines (e.g. "_react=", "internal:", ">>" chains):
        // only plain CSS and plain XPath are accepted.
        if (kind == SelectorKind.Css && (value.Contains(">>", StringComparison.Ordinal) || HasEnginePrefix(value)))
        {
            throw new ArgumentException("CSS selectors cannot contain selector-engine prefixes or '>>' chains.", nameof(value));
        }

        if (kind == SelectorKind.XPath && (value.Contains(">>", StringComparison.Ordinal) || !(value.StartsWith('/') || value.StartsWith('('))))
        {
            throw new ArgumentException("XPath selectors must start with '/' or '(' and cannot contain '>>' chains.", nameof(value));
        }

        if (kind == SelectorKind.Role && !value.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("Role selectors must be an ARIA role name.", nameof(value));
        }

        return new ElementSelector(kind, value, accessibleName);
    }

    public override string ToString() => AccessibleName is null ? $"{Kind}({Value})" : $"{Kind}({Value}, name: {AccessibleName})";

    private static bool HasEnginePrefix(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return false;
        }

        var prefix = value[..equals];
        return prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':') && !prefix.Contains('[', StringComparison.Ordinal);
    }
}

/// <summary>A value to type into a field. <see cref="IsSensitive"/> values are never logged and are masked in screenshots.</summary>
public sealed record InputValue
{
    private InputValue(string value, bool isSensitive)
    {
        Value = value;
        IsSensitive = isSensitive;
    }

    public string Value { get; }

    public bool IsSensitive { get; }

    public static InputValue Plain(string value) => new(value ?? throw new ArgumentNullException(nameof(value)), false);

    public static InputValue Sensitive(string value) => new(value ?? throw new ArgumentNullException(nameof(value)), true);

    public override string ToString() => IsSensitive ? $"[sensitive, {Value.Length} chars]" : $"[{Value.Length} chars]";
}

/// <summary>Option to choose in a &lt;select&gt;: by value, by visible label or by index.</summary>
public sealed record SelectChoice(string? Value = null, string? Label = null, int? Index = null)
{
    public static SelectChoice ByValue(string value) => new(Value: value);

    public static SelectChoice ByLabel(string label) => new(Label: label);

    public static SelectChoice ByIndex(int index) => new(Index: index);
}

public enum WaitKind
{
    Visible,
    Hidden,
    Attached,
    Detached,
    UrlMatches,
    LoadComplete,
}

/// <summary>Something to wait for; uses Playwright auto-wait, never fixed sleeps.</summary>
public sealed record WaitCondition(WaitKind Kind, ElementSelector? Selector = null, string? UrlPattern = null)
{
    public static WaitCondition ElementVisible(ElementSelector selector) => new(WaitKind.Visible, selector);

    public static WaitCondition ElementHidden(ElementSelector selector) => new(WaitKind.Hidden, selector);

    public static WaitCondition ElementAttached(ElementSelector selector) => new(WaitKind.Attached, selector);

    public static WaitCondition ElementDetached(ElementSelector selector) => new(WaitKind.Detached, selector);

    /// <summary>Glob pattern such as <c>**/Customers/Confirmation/**</c>.</summary>
    public static WaitCondition Url(string globPattern) => new(WaitKind.UrlMatches, UrlPattern: globPattern);

    public static WaitCondition Loaded() => new(WaitKind.LoadComplete);
}

/// <summary>Request for a new isolated browser session (one per record attempt).</summary>
public sealed record BrowserSessionRequest(ExecutionScope Scope, EgressPolicy Policy)
{
    public bool AcceptDownloads { get; init; }

    /// <summary>Extra CSS selectors to mask in screenshots for this session (e.g. areas showing secrets as text).</summary>
    public IReadOnlyList<string> MaskSelectors { get; init; } = [];
}

/// <param name="FinalUrl">Redacted (no query values).</param>
/// <param name="RedirectChain">Redacted URLs of every hop, in order.</param>
public sealed record NavigationResult(int Status, string FinalUrl, IReadOnlyList<string> RedirectChain);

public sealed record DownloadResult(ArtifactDescriptor Artifact, string SuggestedFileName);

public sealed record ScreenshotRequest(bool FullPage = false, string Label = "screenshot");
