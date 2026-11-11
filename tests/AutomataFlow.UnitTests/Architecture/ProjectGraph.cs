using System.Xml.Linq;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>Reads the .csproj files of the repository to inspect the real project reference graph.</summary>
internal sealed class ProjectGraph
{
    private readonly Dictionary<string, HashSet<string>> _references;
    private readonly Dictionary<string, HashSet<string>> _packages;

    private ProjectGraph(Dictionary<string, HashSet<string>> references, Dictionary<string, HashSet<string>> packages)
    {
        _references = references;
        _packages = packages;
    }

    public IEnumerable<string> Projects => _references.Keys;

    public static ProjectGraph Load()
    {
        var root = FindRepositoryRoot();
        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var packages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var folder in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var doc = XDocument.Load(file);
                references[name] = doc.Descendants("ProjectReference")
                    .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
                    .ToHashSet(StringComparer.Ordinal);
                packages[name] = doc.Descendants("PackageReference")
                    .Select(e => (string)e.Attribute("Include")!)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        return new ProjectGraph(references, packages);
    }

    public IReadOnlySet<string> DirectReferences(string project) => _references[project];

    public IReadOnlySet<string> Packages(string project) => _packages[project];

    public IReadOnlySet<string> TransitiveReferences(string project)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(_references[project]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (result.Add(current) && _references.TryGetValue(current, out var next))
            {
                foreach (var n in next)
                {
                    pending.Push(n);
                }
            }
        }

        return result;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutomataFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (AutomataFlow.slnx).");
    }
}
