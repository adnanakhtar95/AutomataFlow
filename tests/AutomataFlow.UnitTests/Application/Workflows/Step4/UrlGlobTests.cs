using System.Diagnostics;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Urls;

namespace AutomataFlow.UnitTests.Application.Workflows.Step4;

/// <summary>Phase 4 step 4: the regex-free <c>urlMatches</c> / <c>wait.url</c> glob (§8.2, ADR-046, ADR-062).</summary>
public class UrlGlobTests
{
    private static bool Matches(string pattern, string url)
    {
        var glob = UrlGlob.Parse(pattern, out var error);
        Assert.True(glob is not null, error);
        return glob!.IsMatch(url);
    }

    /// <summary>Shared corpus: the expected values are those of Playwright's globToRegex for the same patterns.</summary>
    [Theory]
    [InlineData("**/Customers/Confirmation/**", "https://crm.test/Customers/Confirmation/5", true)]
    [InlineData("**/Customers/Confirmation/**", "https://crm.test/Customers/Confirmation/5/details?x=1#top", true)]
    [InlineData("**/Customers/Confirmation/**", "https://crm.test/Customers/Confirmation/", true)]
    [InlineData("**/Customers/Confirmation/**", "https://crm.test/Customers/Confirmation", false)]
    [InlineData("**/Customers/*", "https://crm.test/Customers/5", true)]
    [InlineData("**/Customers/*", "https://crm.test/Customers/5/edit", false)]
    [InlineData("**/Customers/*", "https://crm.test/Customers/", true)]
    [InlineData("https://crm.test/**", "https://crm.test/a/b/c", true)]
    [InlineData("https://crm.test/**", "https://crm.test.evil/a", false)]
    [InlineData("https://crm.test/*", "https://crm.test/a", true)]
    [InlineData("https://crm.test/*", "https://crm.test/a/b", false)]
    [InlineData("https://crm.test/a/**/b", "https://crm.test/a/b", true)]
    [InlineData("https://crm.test/a/**/b", "https://crm.test/a/x/y/b", true)]
    [InlineData("https://crm.test/a/**/b", "https://crm.test/a/xb", false)]
    [InlineData("https://*.crm.test/**", "https://eu.crm.test/x", true)]
    [InlineData("https://*.crm.test/**", "https://a.b.crm.test/x", true)] // '*' is not domain-aware: it only excludes '/'
    [InlineData("https://*.crm.test/**", "https://crm.test/x", false)]
    [InlineData("**/search?q=*", "https://crm.test/search?q=abc", false)] // '?' is rejected; see the rejection test
    public void Glob_semantics(string pattern, string url, bool expected)
    {
        if (pattern.Contains('?', StringComparison.Ordinal))
        {
            Assert.Null(UrlGlob.Parse(pattern, out _));
            return;
        }

        Assert.Equal(expected, Matches(pattern, url));
    }

    [Theory]
    [InlineData("**/a.b", "https://crm.test/a.b", true)]
    [InlineData("**/a.b", "https://crm.test/axb", false)]
    [InlineData("**/a+b", "https://crm.test/a+b", true)]
    [InlineData("**/a+b", "https://crm.test/aab", false)]
    [InlineData("**/(x)", "https://crm.test/(x)", true)]
    [InlineData("**/Search", "https://crm.test/search", false)]
    [InlineData("**/done#ok", "https://crm.test/done#ok", true)]
    [InlineData("**/s=*", "https://crm.test/s=1&t=2", true)]
    public void Every_other_character_is_literal_and_comparison_is_ordinal(string pattern, string url, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, url));
    }

    [Theory]
    [InlineData("**/a?b", "glob-invalid-character")]
    [InlineData("**/{a,b}", "glob-invalid-character")]
    [InlineData("**/[ab]", "glob-invalid-character")]
    [InlineData("**/a\\b", "glob-invalid-character")]
    [InlineData("**/a b", "glob-invalid-character")]
    [InlineData("**/café", "glob-invalid-character")]
    [InlineData("https://CRM.test/**", "glob-uppercase-host")]
    [InlineData("https://crm.test:443/**", "glob-default-port")]
    [InlineData("/Customers/**", "glob-not-anchored")]
    [InlineData("*/Customers", "glob-not-anchored")]
    [InlineData("crm.test/**", "glob-not-anchored")]
    [InlineData("**/a**", "glob-deep-not-segment")]
    [InlineData("**/**.html", "glob-deep-not-segment")]
    [InlineData("**/***", "glob-too-many-stars")]
    [InlineData("", "glob-length")]
    public void Unsupported_patterns_are_rejected(string pattern, string error)
    {
        Assert.Null(UrlGlob.Parse(pattern, out var actual));
        Assert.Equal(error, actual);
    }

    [Fact]
    public void Pattern_length_is_limited()
    {
        var atLimit = "**/" + new string('a', WorkflowLimits.MaxUrlPatternChars - 3);
        Assert.NotNull(UrlGlob.Parse(atLimit, out _));
        Assert.Equal("glob-length", UrlGlob.Parse(atLimit + "a", out var error) is null ? error : null);
    }

    [Fact]
    public void Pathological_patterns_complete_within_a_bound()
    {
        var pattern = "**/" + string.Concat(Enumerable.Repeat("a*", 200)) + "b";
        var url = "https://crm.test/" + new string('a', WorkflowLimits.MaxFinalUrlChars - 17);
        var glob = UrlGlob.Parse(pattern, out _)!;

        var watch = Stopwatch.StartNew();
        var result = glob.IsMatch(url);
        watch.Stop();

        Assert.False(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.ElapsedMilliseconds} ms");
    }
}
