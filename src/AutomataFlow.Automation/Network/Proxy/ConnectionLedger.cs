using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AutomataFlow.Automation.Network.Proxy;

/// <summary>One upstream connection of a Worker incarnation (docs/PHASE4_PLAN.md §7.6.5, ADR-060).</summary>
public sealed record ConnectionLedgerEntry(
    long Sequence,
    Guid SessionId,
    AddressFamily Family,
    IPEndPoint Local,
    IPEndPoint Remote,
    ConnectionIdentity Identity);

/// <summary>
/// Durable, append-only record of every upstream connection, written BEFORE any payload is admitted on it (W5). The
/// Phase 5 process-death supervisor drains exactly these connections after a Worker dies.
/// </summary>
public interface IConnectionLedger
{
    Guid IncarnationId { get; }

    string Path { get; }

    /// <summary>Appends and flushes to stable storage before returning. Throws if the record cannot be made durable.</summary>
    ConnectionLedgerEntry Append(Guid sessionId, IPEndPoint local, IPEndPoint remote, ConnectionIdentity identity);
}

/// <summary>
/// File ledger: one line per connection, <c>v1|seq|session|family|local|remote|kind|value|check</c>, where
/// <c>check</c> is the first 16 hex digits of SHA-256 over the rest of the line. Opened with write-through and
/// flushed to disk (fsync) on every append. One file per incarnation; never truncated or rewritten.
/// </summary>
public sealed class FileConnectionLedger : IConnectionLedger, IDisposable
{
    private readonly Lock _gate = new();
    private readonly FileStream _file;
    private long _sequence;

    public FileConnectionLedger(string directory, Guid incarnationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        IncarnationId = incarnationId;
        Path = System.IO.Path.Combine(directory, incarnationId.ToString("N") + ".ledger");
        _file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    public Guid IncarnationId { get; }

    public string Path { get; }

    public ConnectionLedgerEntry Append(Guid sessionId, IPEndPoint local, IPEndPoint remote, ConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (identity.Value == 0 || !Enum.IsDefined(identity.Kind))
        {
            throw new ArgumentException("A ledger record requires a non-zero OS connection identity.", nameof(identity));
        }

        lock (_gate)
        {
            var entry = new ConnectionLedgerEntry(_sequence + 1, sessionId, local.AddressFamily, local, remote, identity);
            var bytes = Encoding.ASCII.GetBytes(ConnectionLedgerFormat.Format(entry) + "\n");
            _file.Write(bytes);
            _file.Flush(flushToDisk: true);
            _sequence = entry.Sequence;
            return entry;
        }
    }

    public void Dispose() => _file.Dispose();
}

public enum LedgerReadStatus
{
    Ok,
    Missing,
    Corrupt,
}

/// <param name="TornTail">The last line was incomplete or failed its check. It is ignored: by W5 no payload was ever admitted on that connection.</param>
public sealed record LedgerReadResult(LedgerReadStatus Status, IReadOnlyList<ConnectionLedgerEntry> Entries, bool TornTail, string? Reason);

/// <summary>Reads a ledger with fail-closed semantics (§7.6.5): anything but a torn final record makes it Corrupt.</summary>
public static class ConnectionLedgerReader
{
    public static LedgerReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new LedgerReadResult(LedgerReadStatus.Missing, [], false, "ledger-missing");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LedgerReadResult(LedgerReadStatus.Corrupt, [], false, "ledger-unreadable");
        }

        return Parse(text);
    }

    public static LedgerReadResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Records are newline-terminated. Everything after the last newline is an unterminated (torn) tail.
        var lastNewline = text.LastIndexOf('\n');
        var terminated = lastNewline < 0 ? [] : text[..lastNewline].Split('\n');
        var torn = lastNewline < text.Length - 1;

        var entries = new List<ConnectionLedgerEntry>(terminated.Length);
        for (var i = 0; i < terminated.Length; i++)
        {
            if (ConnectionLedgerFormat.TryParse(terminated[i], out var entry))
            {
                if (entry!.Sequence != entries.Count + 1)
                {
                    // A record that passes its check but is out of sequence cannot come from a torn write.
                    return new LedgerReadResult(LedgerReadStatus.Corrupt, [], false, "ledger-sequence-gap");
                }

                entries.Add(entry);
                continue;
            }

            // Only the final record may fail its check (a torn last write); earlier damage is corruption.
            if (i == terminated.Length - 1 && !torn)
            {
                torn = true;
                break;
            }

            return new LedgerReadResult(LedgerReadStatus.Corrupt, [], false, "ledger-record-damaged-or-out-of-sequence");
        }

        return new LedgerReadResult(LedgerReadStatus.Ok, entries, torn, null);
    }
}

internal static class ConnectionLedgerFormat
{
    public static string Format(ConnectionLedgerEntry e)
    {
        var body = string.Join('|',
            "v1",
            e.Sequence.ToString(CultureInfo.InvariantCulture),
            e.SessionId.ToString("N"),
            ((int)e.Family).ToString(CultureInfo.InvariantCulture),
            e.Local.ToString(),
            e.Remote.ToString(),
            ((int)e.Identity.Kind).ToString(CultureInfo.InvariantCulture),
            e.Identity.Value.ToString(CultureInfo.InvariantCulture));
        return body + "|" + Check(body);
    }

    public static bool TryParse(string line, out ConnectionLedgerEntry? entry)
    {
        entry = null;
        var cut = line.LastIndexOf('|');
        if (cut <= 0)
        {
            return false;
        }

        var body = line[..cut];
        if (!string.Equals(line[(cut + 1)..], Check(body), StringComparison.Ordinal))
        {
            return false;
        }

        var parts = body.Split('|');
        if (parts.Length != 8 || parts[0] != "v1"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || !Guid.TryParseExact(parts[2], "N", out var session)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var family)
            || !IPEndPoint.TryParse(parts[4], out var local)
            || !IPEndPoint.TryParse(parts[5], out var remote)
            || !int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var kind)
            || !ulong.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || !Enum.IsDefined((ConnectionIdentityKind)kind)
            || value == 0)
        {
            return false;
        }

        entry = new ConnectionLedgerEntry(sequence, session, (AddressFamily)family, local, remote, new ConnectionIdentity((ConnectionIdentityKind)kind, value));
        return true;
    }

    private static string Check(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..16];
}
