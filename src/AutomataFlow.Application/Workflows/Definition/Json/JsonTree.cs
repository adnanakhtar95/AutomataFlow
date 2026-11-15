using System.Collections.Immutable;

namespace AutomataFlow.Application.Workflows.Definition.Json;

/// <summary>
/// An immutable JSON value produced only by <see cref="StrictJsonParser"/>: objects without duplicate properties,
/// integer-only numbers, valid UTF-16 strings and bounded depth. This closed set is the input of both the definition
/// binder and the canonical writer, so hashing and validation always see the same document. The hierarchy is closed:
/// constructors are internal (or private), so no other assembly can create or derive a node.
/// </summary>
public abstract class JNode
{
    private protected JNode()
    {
    }
}

/// <summary>A JSON object. <see cref="Properties"/> are kept sorted by ordinal UTF-16 order of their names.</summary>
public sealed class JObject : JNode
{
    internal JObject(ImmutableArray<KeyValuePair<string, JNode>> sortedProperties) => Properties = sortedProperties;

    public ImmutableArray<KeyValuePair<string, JNode>> Properties { get; }

    public bool TryGet(string name, out JNode value)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }
}

public sealed class JArray : JNode
{
    internal JArray(ImmutableArray<JNode> items) => Items = items;

    public ImmutableArray<JNode> Items { get; }
}

public sealed class JString : JNode
{
    internal JString(string value) => Value = value;

    public string Value { get; }
}

public sealed class JInteger : JNode
{
    internal JInteger(long value) => Value = value;

    public long Value { get; }
}

public sealed class JBoolean : JNode
{
    internal JBoolean(bool value) => Value = value;

    public bool Value { get; }
}

public sealed class JNull : JNode
{
    internal static readonly JNull Instance = new();

    private JNull()
    {
    }
}
