using System.Text.Json;
using System.Xml.Linq;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests;

/// <summary>ADR-033: the Playwright package, and the Chromium baked into the verification image, must match.</summary>
public class VersionLockTests
{
    [Fact]
    public void Playwright_package_matches_the_central_pin_and_the_verification_image()
    {
        // The resolved package version is authoritative in the test's deps.json (the assembly's own
        // informational version does not carry the package version).
        var depsFile = Path.Combine(AppContext.BaseDirectory, "AutomataFlow.AutomationTests.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var resolved = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name)
            .Single(n => n.StartsWith("Microsoft.Playwright/", StringComparison.Ordinal))
            .Split('/')[1];

        var pinned = XDocument.Load(Path.Combine(DemoCrmProcess.RepositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(e => (string?)e.Attribute("Include") == "Microsoft.Playwright")
            .Attribute("Version")!.Value;
        var image = Environment.GetEnvironmentVariable("AUTOMATAFLOW_VERIFY_PLAYWRIGHT_VERSION");

        Assert.Equal(pinned, resolved);
        Assert.False(string.IsNullOrEmpty(image), "Browser tests must run in the verification image (scripts/verify-in-container.ps1).");
        Assert.Equal(pinned, image);
    }
}
