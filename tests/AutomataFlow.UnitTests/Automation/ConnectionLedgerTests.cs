using System.Net;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Automation;

/// <summary>Connection ledger (docs/PHASE4_PLAN.md §7.6.5, W5): durable, checked, fail-closed on damage.</summary>
public sealed class ConnectionLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "af-ledger-tests", Guid.NewGuid().ToString("N"));

    private static readonly ConnectionIdentity Cookie = new(ConnectionIdentityKind.LinuxSocketCookie, 4242);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileConnectionLedger NewLedger() => new(_directory, Guid.NewGuid());

    private static IPEndPoint Ep(string text) => IPEndPoint.Parse(text);

    [Fact]
    public void Appended_entries_round_trip_in_sequence_with_their_identity()
    {
        var session = Guid.NewGuid();
        string path;
        using (var ledger = NewLedger())
        {
            path = ledger.Path;
            ledger.Append(session, Ep("10.1.1.1:40000"), Ep("8.8.8.8:443"), Cookie);
            ledger.Append(session, Ep("[2001:db8::1]:40001"), Ep("[2001:db8::2]:443"), new ConnectionIdentity(ConnectionIdentityKind.WindowsCreateTimestamp, 133_000_000_000));
        }

        var result = ConnectionLedgerReader.Read(path);

        Assert.Equal(LedgerReadStatus.Ok, result.Status);
        Assert.False(result.TornTail);
        Assert.Equal([1L, 2L], result.Entries.Select(e => e.Sequence));
        Assert.Equal(Ep("8.8.8.8:443"), result.Entries[0].Remote);
        Assert.Equal(Cookie, result.Entries[0].Identity);
        Assert.Equal(ConnectionIdentityKind.WindowsCreateTimestamp, result.Entries[1].Identity.Kind);
        Assert.Equal(session, result.Entries[1].SessionId);
    }

    [Fact]
    public void A_ledger_record_without_a_real_identity_is_refused()
    {
        using var ledger = NewLedger();

        Assert.Throws<ArgumentException>(() => ledger.Append(Guid.NewGuid(), Ep("10.1.1.1:1"), Ep("8.8.8.8:443"), new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, 0)));
    }

    [Fact]
    public void A_ledger_file_is_never_reused_or_overwritten()
    {
        var id = Guid.NewGuid();
        using var first = new FileConnectionLedger(_directory, id);

        Assert.ThrowsAny<IOException>(() => new FileConnectionLedger(_directory, id));
    }

    [Fact]
    public void A_missing_ledger_is_reported_as_missing()
    {
        Assert.Equal(LedgerReadStatus.Missing, ConnectionLedgerReader.Read(Path.Combine(_directory, "none.ledger")).Status);
    }

    [Fact]
    public void An_unterminated_final_record_is_a_torn_tail_and_ignored()
    {
        var text = Lines(2) + "v1|3|half-written";

        var result = ConnectionLedgerReader.Parse(text);

        Assert.Equal(LedgerReadStatus.Ok, result.Status);
        Assert.True(result.TornTail);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public void A_terminated_but_damaged_final_record_is_a_torn_tail()
    {
        var lines = Lines(3).Split('\n');
        lines[2] = lines[2][..^1] + (lines[2][^1] == '0' ? "1" : "0"); // break the check of the last record only

        var result = ConnectionLedgerReader.Parse(string.Join('\n', lines));

        Assert.Equal(LedgerReadStatus.Ok, result.Status);
        Assert.True(result.TornTail);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public void Damage_before_the_final_record_makes_the_ledger_corrupt()
    {
        var lines = Lines(3).Split('\n');
        lines[1] = lines[1].Replace("8.8.8.8:443", "8.8.8.9:443", StringComparison.Ordinal);

        var result = ConnectionLedgerReader.Parse(string.Join('\n', lines));

        Assert.Equal(LedgerReadStatus.Corrupt, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void A_sequence_gap_makes_the_ledger_corrupt()
    {
        var lines = Lines(3).Split('\n').ToList();
        lines.RemoveAt(1); // records 1, 3

        var result = ConnectionLedgerReader.Parse(string.Join('\n', lines));

        Assert.Equal(LedgerReadStatus.Corrupt, result.Status);
    }

    [Fact]
    public void A_damaged_final_record_followed_by_a_torn_tail_is_corruption()
    {
        var lines = Lines(3).Split('\n');
        lines[2] = lines[2][..^1] + (lines[2][^1] == '0' ? "1" : "0");

        var result = ConnectionLedgerReader.Parse(string.Join('\n', lines) + "v1|4|partial");

        Assert.Equal(LedgerReadStatus.Corrupt, result.Status);
    }

    /// <summary>A valid ledger text with <paramref name="count"/> newline-terminated records.</summary>
    private string Lines(int count)
    {
        using var ledger = NewLedger();
        for (var i = 0; i < count; i++)
        {
            ledger.Append(Guid.NewGuid(), Ep($"10.0.0.{i + 1}:{40000 + i}"), Ep("8.8.8.8:443"), new ConnectionIdentity(ConnectionIdentityKind.LinuxSocketCookie, (ulong)(1000 + i)));
        }

        ledger.Dispose();
        return File.ReadAllText(ledger.Path);
    }
}
