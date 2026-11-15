namespace AutomataFlow.Domain.Workflows;

/// <summary>
/// The single definition of the workflow definition hash format (ADR-041, ADR-061): <c>sha256:</c> followed by exactly
/// 64 lowercase hexadecimal digits. Used by the Domain entity and by the Application canonicalizer.
/// </summary>
public static class DefinitionHashFormat
{
    public const string Prefix = "sha256:";

    public const int Length = 71;

    public static bool IsWellFormed(string? hash)
    {
        if (hash is not { Length: Length } || !hash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in hash.AsSpan(Prefix.Length))
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
