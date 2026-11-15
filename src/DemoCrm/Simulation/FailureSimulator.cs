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

/// <summary>
/// Counts every POST to /Customers/Register, incremented at the start of the handler before validation, so tests can
/// detect any resubmission, including rejected ones (test-fixture extension, ADR-054). Exposed as a count only.
/// </summary>
public sealed class RegistrationCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
