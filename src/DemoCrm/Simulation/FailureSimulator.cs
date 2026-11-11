using System.Collections.Concurrent;

namespace DemoCrm.Simulation;

/// <summary>Counts submission attempts per e-mail address so failure scenarios are deterministic.</summary>
public sealed class FailureSimulator
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public int TrackedAddresses => _attempts.Count;

    /// <summary>Records one more attempt for the address and returns its 1-based attempt number.</summary>
    public int RecordAttempt(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return _attempts.AddOrUpdate(email.Trim(), 1, static (_, current) => current + 1);
    }

    public void Reset() => _attempts.Clear();
}
