using System.Globalization;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>The fields of <c>/proc/&lt;pid&gt;/status</c> the supervisor uses (world-readable; no ptrace access needed).</summary>
public sealed record ProcStatus(int PPid, IReadOnlyList<int> Uids, ulong CapInh, ulong CapPrm, ulong CapEff, ulong CapBnd, ulong CapAmb, bool NoNewPrivs, IReadOnlyList<int> NsPids)
{
    public bool HoldsNoCapability => (CapInh | CapPrm | CapEff | CapBnd | CapAmb) == 0;

    public static ProcStatus Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fields = text.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        static IReadOnlyList<int> Ints(string? value) =>
            value is null ? [] : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
        ulong Cap(string name) => fields.TryGetValue(name, out var v) ? ulong.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ulong.MaxValue;
        return new ProcStatus(
            fields.TryGetValue("PPid", out var ppid) ? int.Parse(ppid, CultureInfo.InvariantCulture) : -1,
            Ints(fields.GetValueOrDefault("Uid")),
            Cap("CapInh"), Cap("CapPrm"), Cap("CapEff"), Cap("CapBnd"), Cap("CapAmb"),
            fields.TryGetValue("NoNewPrivs", out var nnp) && nnp == "1",
            Ints(fields.GetValueOrDefault("NSpid")));
    }
}

/// <summary>Reads of <c>/proc</c>. A process that vanished mid-read is reported as gone (null), never as anything else.</summary>
public static class ProcFs
{
    public const string Root = "/proc";

    /// <summary>All visible PIDs (thread-group leaders). Throws if <c>/proc</c> itself cannot be enumerated.</summary>
    public static IReadOnlyList<int> Pids() =>
        Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(pid => pid > 0).ToList();

    public static ProcStatus? Status(int pid)
    {
        try
        {
            return ProcStatus.Parse(File.ReadAllText($"{Root}/{pid}/status"));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null;
        }
    }

    public static ProcStatus SelfStatus() => ProcStatus.Parse(File.ReadAllText($"{Root}/self/status"));

    /// <summary>The namespace identity (e.g. <c>pid:[4026532447]</c>), or null if the task is gone or not inspectable.</summary>
    public static string? Namespace(int pid, string kind) => Link($"{Root}/{pid}/ns/{kind}");

    public static string? SelfNamespace(string kind) => Link($"{Root}/self/ns/{kind}");

    /// <summary>Inodes of every socket in this process's file descriptor table (the supervisor's own sockets).</summary>
    public static IReadOnlySet<uint> OwnSocketInodes()
    {
        var inodes = new HashSet<uint>();
        foreach (var fd in Directory.EnumerateFileSystemEntries($"{Root}/self/fd"))
        {
            if (Link(fd) is { } target && target.StartsWith("socket:[", StringComparison.Ordinal)
                && uint.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                inodes.Add(inode);
            }
        }

        return inodes;
    }

    private static string? Link(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
