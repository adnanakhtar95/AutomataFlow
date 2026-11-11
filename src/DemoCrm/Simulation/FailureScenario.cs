namespace DemoCrm.Simulation;

public enum FailureScenarioKind
{
    None,

    /// <summary>The first <see cref="FailureScenario.FailCount"/> submissions fail with a temporary error BEFORE anything is stored.</summary>
    FailBeforeCommit,

    /// <summary>The first submission is stored, then a temporary error is returned (the response is "lost").</summary>
    CommitThenFail,

    /// <summary>The submission succeeds, but only after <c>SlowDelayMs</c>.</summary>
    Slow,
}

/// <summary>
/// A predictable failure scenario selected by the plus-tag of the e-mail address:
/// <c>name+fail-2@example.test</c>, <c>name+commit-fail@example.test</c>, <c>name+slow@example.test</c>.
/// </summary>
public sealed record FailureScenario(FailureScenarioKind Kind, int FailCount = 0)
{
    public const int MaxFailCount = 5;

    public static FailureScenario None { get; } = new(FailureScenarioKind.None);

    public static FailureScenario Parse(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return None;
        }

        var at = email.LastIndexOf('@');
        var local = at > 0 ? email[..at] : email;
        var plus = local.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return None;
        }

        var tag = local[(plus + 1)..].Trim().ToLowerInvariant();
        if (tag == "commit-fail")
        {
            return new FailureScenario(FailureScenarioKind.CommitThenFail);
        }

        if (tag == "slow")
        {
            return new FailureScenario(FailureScenarioKind.Slow);
        }

        if (tag.StartsWith("fail-", StringComparison.Ordinal)
            && int.TryParse(tag.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            && count is >= 1 and <= MaxFailCount)
        {
            return new FailureScenario(FailureScenarioKind.FailBeforeCommit, count);
        }

        return None;
    }
}
