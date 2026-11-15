using AutomataFlow.Application.Execution;
using AutomataFlow.Application.Network;

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

/// <summary>
/// Per-call options (docs/PHASE4_PLAN.md §0, ADR-053). <see cref="Timeout"/> is ONE total deadline for everything the
/// operation does, including composite operations (submit = click + load wait). Expiry fails only this operation and
/// never closes the session (ADR-038). When <c>null</c>, the configured per-operation defaults apply.
/// </summary>
public sealed record OperationOptions
{
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    public OperationOptions(TimeSpan? timeout = null)
    {
        if (timeout is { } t && (t <= TimeSpan.Zero || t > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"Operation timeouts must be > 0 and <= {MaxTimeout.TotalMinutes} minutes.");
        }

        Timeout = timeout;
    }

    public TimeSpan? Timeout { get; }

    public static OperationOptions WithTimeout(TimeSpan timeout) => new(timeout);
}

/// <summary>
/// A file to upload, held in memory. The engine never accepts a filesystem path (ADR-038): the content is passed to the
/// browser as an in-memory payload and the file name is sanitized before use.
/// </summary>
public sealed record UploadFile
{
    public UploadFile(string fileName, string contentType, ReadOnlyMemory<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    /// <summary>Name as supplied; only its sanitized last segment is ever given to the browser.</summary>
    public string FileName { get; }

    public string ContentType { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public override string ToString() => $"[{FileNameSanitizer.Sanitize(FileName, "upload")}, {Content.Length} bytes]";
}

public enum RowValueKind
{
    /// <summary>An attribute of the row element itself.</summary>
    Attribute,

    /// <summary>The inner text of a child element inside the row (0 matches = null, more than 1 = ElementNotUnique).</summary>
    ChildText,
}

/// <summary>One value to read from every row (reconcileMatch keys and fields, docs/PHASE4_PLAN.md §10.3).</summary>
public sealed record RowValueSpec
{
    private RowValueSpec(RowValueKind kind, string? attribute, ElementSelector? child)
    {
        Kind = kind;
        Attribute = attribute;
        Child = child;
    }

    public RowValueKind Kind { get; }

    public string? Attribute { get; }

    public ElementSelector? Child { get; }

    public static RowValueSpec OfAttribute(string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        if (attribute.Length > 100 || !attribute.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
        {
            throw new ArgumentException("Attribute names are limited to 100 ASCII letters, digits and - _ : .", nameof(attribute));
        }

        return new RowValueSpec(RowValueKind.Attribute, attribute, null);
    }

    public static RowValueSpec OfChildText(ElementSelector child) =>
        new(RowValueKind.ChildText, null, child ?? throw new ArgumentNullException(nameof(child)));

    public override string ToString() => Kind == RowValueKind.Attribute ? $"@{Attribute}" : $"text({Child})";
}

/// <param name="Count">Number of rows matched at read time.</param>
/// <param name="Values">One list per row (in document order) with one value per requested spec; EMPTY when
/// <see cref="Truncated"/> because the row count exceeded the requested maximum.</param>
public sealed record RowSnapshot(int Count, IReadOnlyList<IReadOnlyList<string?>> Values)
{
    public const int MaxRowsLimit = 50;

    public bool Truncated => Count > 0 && Values.Count == 0;
}
