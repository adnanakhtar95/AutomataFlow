using System.Text.RegularExpressions;

namespace AutomataFlow.UnitTests.Architecture;

/// <summary>
/// Phase 4 step 3: the definition model, parser and canonicalizer are pure Application code without regular
/// expressions (§16 D6) and without browser, I/O or clock access.
/// </summary>
public class WorkflowDefinitionSourceTests
{
    private static string DefinitionSource =>
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "AutomataFlow.Application", "Workflows", "Definition");

    [Theory]
    [InlineData(@"\bRegex\b")]
    [InlineData(@"System\.Text\.RegularExpressions")]
    [InlineData(@"GeneratedRegex")]
    [InlineData(@"\bAutomataFlow\.(Automation|Infrastructure|Api|Worker)\b")]
    [InlineData(@"\bMicrosoft\.Playwright\b")]
    [InlineData(@"\bDemoCrm\b")]
    [InlineData(@"\bFile\.")]
    [InlineData(@"\bDirectory\.")]
    [InlineData(@"\bHttpClient\b")]
    [InlineData(@"\bEnvironment\.")]
    [InlineData(@"\bDateTime(Offset)?\.(Utc)?Now\b")]
    [InlineData(@"\bJsonSerializer\b")]
    [InlineData(@"\bJsonDocument\b")]
    public void The_definition_code_is_pure_and_regex_free(string forbiddenPattern)
    {
        var files = Directory.EnumerateFiles(DefinitionSource, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .Where(f => Regex.IsMatch(File.ReadAllText(f), forbiddenPattern))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
