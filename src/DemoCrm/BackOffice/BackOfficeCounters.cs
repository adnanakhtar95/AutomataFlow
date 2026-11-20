namespace DemoCrm.BackOffice;

/// <summary>Counts every POST to /Orders/New (before validation), so tests can detect any resubmission (like RegistrationCounter).</summary>
public sealed class OrderCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

/// <summary>Counts every approval POST, so tests can detect any resubmission.</summary>
public sealed class ApprovalCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
