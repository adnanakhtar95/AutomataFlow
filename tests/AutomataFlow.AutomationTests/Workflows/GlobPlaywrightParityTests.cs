using AutomataFlow.Application.Workflows.Urls;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Workflows;

/// <summary>
/// Phase 4 step 9 (§8.2, R2, ADR-062): for a shared corpus of patterns and REAL page URLs (paths, query strings with
/// encoded characters), our regex-free <see cref="UrlGlob"/> must agree with what Playwright's own URL wait accepts,
/// so a <c>urlMatches</c> condition and a <c>wait.url</c> step can never disagree.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class GlobPlaywrightParityTests(BrowserFixture fixture)
{
    private static readonly string[] Paths =
    [
        "/Customers/Search?Field=Email&Term=ann%2Bcommit-fail%40example.test",
        "/Customers/Details/3",
        "/Customers",
        "/Customers/Search?Field=OperationId&Term=6f9619ff-8b86-d011-b42d-00c04fc964ff",
    ];

    private static readonly string[] Patterns =
    [
        "**/Customers/*",
        "**/Customers/**",
        "**/Customers/Search*",
        "**/Customers/Details/*",
        "**/Details/**",
        "**/Customers/**/3",
        "**/Customers/**/Details/*",
        "**/customers/*",
        "**/Customers",
        "**/*=Email*",
        "**/*Term=ann%2Bcommit-fail%40example.test",
        "{base}/Customers/*",
        "{base}/**",
        "{base}/*/Details/3",
        "{base}/Customers",
        "{base}/Customers/Search*",
    ];

    [Fact]
    public async Task Our_glob_agrees_with_Playwrights_url_wait_on_real_pages()
    {
        await using var session = await fixture.Engine.CreateSessionAsync(fixture.DemoCrmPolicy);
        var baseUrl = fixture.DemoCrm.BaseUrl;
        await session.NavigateAsync(new Uri(baseUrl + "/Login"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("login-email"), InputValue.Plain("agent@democrm.test"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("login-password"), InputValue.Sensitive("DemoPass-2026!"), CancellationToken.None);
        await session.SubmitAsync(ElementSelector.TestId("login-submit"), CancellationToken.None);

        var disagreements = new List<string>();
        var (matches, nonMatches) = (0, 0);
        foreach (var path in Paths)
        {
            await session.NavigateAsync(new Uri(baseUrl + path), CancellationToken.None);
            var url = session.CurrentUrl;
            foreach (var pattern in Patterns.Select(p => p.Replace("{base}", baseUrl, StringComparison.Ordinal)))
            {
                var glob = UrlGlob.Parse(pattern, out var error);
                Assert.True(glob is not null, $"{pattern}: {error}");
                var ours = glob!.IsMatch(url);
                bool playwright;
                try
                {
                    await session.WaitForAsync(WaitCondition.Url(pattern), OperationOptions.WithTimeout(TimeSpan.FromMilliseconds(400)), CancellationToken.None);
                    playwright = true;
                }
                catch (BrowserOperationException)
                {
                    playwright = false;
                }

                if (ours != playwright)
                {
                    disagreements.Add($"{pattern} vs {url}: ours={ours}, playwright={playwright}");
                }

                (matches, nonMatches) = ours ? (matches + 1, nonMatches) : (matches, nonMatches + 1);
            }
        }

        Assert.Empty(disagreements);
        Assert.True(matches > 10 && nonMatches > 10, $"the corpus must exercise both outcomes ({matches} matches, {nonMatches} non-matches)");
    }
}
