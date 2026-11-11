using System.Security.Cryptography;
using System.Text;
using AutomataFlow.Application.Execution;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Browser;

[Collection(BrowserTestGroup.Name)]
public sealed class ArtifactTests(BrowserFixture fixture)
{
    private static Uri Public(string path) => new($"http://public.test{path}");

    private static bool IsMaskColor((byte R, byte G, byte B) p) => p.R > 240 && p.G < 20 && p.B > 240;

    [Fact]
    public async Task Explicit_screenshots_are_stored_as_scoped_png_artifacts()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);

        var artifact = await session.ScreenshotAsync(new ScreenshotRequest(Label: "after-login"), CancellationToken.None);

        var stored = Assert.Single(engine.Store.Items);
        Assert.NotNull(artifact);
        Assert.Equal(ArtifactKind.Screenshot, artifact.Kind);
        Assert.Equal("after-login.png", artifact.FileName);
        Assert.Equal(EngineHarness.Scope, stored.Request.Scope);
        Assert.Equal(session.Id, stored.Request.SessionId);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], stored.Content.Take(4));
    }

    [Fact]
    public async Task A_failed_operation_captures_a_screenshot_by_default()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Timeouts.ActionSeconds = 1);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);

        await Assert.ThrowsAsync<BrowserOperationException>(() => session.ClickAsync(ElementSelector.TestId("missing"), CancellationToken.None));

        var stored = Assert.Single(engine.Store.Items);
        Assert.Equal("failure-click.png", stored.Descriptor.FileName);
    }

    [Fact]
    public async Task Screenshot_policy_never_disables_all_screenshots()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o =>
        {
            o.Screenshots = ScreenshotPolicy.Never;
            o.Timeouts.ActionSeconds = 1;
        });
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/"), CancellationToken.None);

        var explicitRequest = await Assert.ThrowsAsync<BrowserOperationException>(() => session.ScreenshotAsync(new ScreenshotRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<BrowserOperationException>(() => session.ClickAsync(ElementSelector.TestId("missing"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.PolicyViolation, explicitRequest.Code);
        Assert.Empty(engine.Store.Items);
    }

    [Fact]
    public async Task Sensitive_fields_password_fields_and_marked_regions_are_masked_in_screenshots()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/form"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("username"), InputValue.Plain("visible-user"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("card"), InputValue.Sensitive("4111111111111111"), CancellationToken.None);
        await session.FillAsync(ElementSelector.TestId("password"), InputValue.Plain("typed-into-password-field"), CancellationToken.None);

        await session.ScreenshotAsync(new ScreenshotRequest(Label: "masking"), CancellationToken.None);
        var image = PngImage.Decode(Assert.Single(engine.Store.Items).Content);

        async Task<(byte R, byte G, byte B)> CenterOf(string testId)
        {
            var box = await ((BrowserSession)session).BoundingBoxForTestingAsync(ElementSelector.TestId(testId));
            return image.Pixel((int)(box.X + (box.Width / 2)), (int)(box.Y + (box.Height / 2)));
        }

        Assert.True(IsMaskColor(await CenterOf("card")), "Sensitive-filled field must be masked.");
        Assert.True(IsMaskColor(await CenterOf("password")), "Password fields must always be masked.");
        Assert.True(IsMaskColor(await CenterOf("secret-text")), "Elements marked data-af-sensitive must be masked.");
        Assert.False(IsMaskColor(await CenterOf("plain-text")), "Ordinary content must stay visible.");
        Assert.False(IsMaskColor(await CenterOf("username")), "Plain fields must stay visible.");
    }

    [Fact]
    public async Task Session_mask_selectors_cover_site_specific_secret_areas()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy, maskSelectors: ["[data-testid=plain-text]"]);
        await session.NavigateAsync(Public("/form"), CancellationToken.None);

        await session.ScreenshotAsync(new ScreenshotRequest(), CancellationToken.None);
        var image = PngImage.Decode(Assert.Single(engine.Store.Items).Content);
        var box = await ((BrowserSession)session).BoundingBoxForTestingAsync(ElementSelector.TestId("plain-text"));

        Assert.True(IsMaskColor(image.Pixel((int)(box.X + (box.Width / 2)), (int)(box.Y + (box.Height / 2)))));
    }

    [Fact]
    public async Task Allowed_downloads_are_stored_with_sanitized_names_and_hashes_and_temp_files_are_removed()
    {
        await using var engine = EngineHarness.Create(fixture.Servers);
        var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy, acceptDownloads: true);
        await session.NavigateAsync(Public("/downloads"), CancellationToken.None);

        var csv = await session.DownloadAsync(ElementSelector.TestId("dl-csv"), CancellationToken.None);
        var traversal = await session.DownloadAsync(ElementSelector.TestId("dl-traversal"), CancellationToken.None);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "automataflow", session.Id.ToString("N"));
        await session.DisposeAsync();

        var expected = Encoding.UTF8.GetBytes("id,name\n1,Fictional Person\n");
        Assert.Equal("report.csv", csv.SuggestedFileName);
        Assert.Equal(ArtifactKind.Download, csv.Artifact.Kind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), csv.Artifact.Sha256);
        Assert.Equal(expected, engine.Store.Items.First(i => i.Descriptor.Id == csv.Artifact.Id).Content);
        Assert.DoesNotContain('/', traversal.SuggestedFileName);
        Assert.DoesNotContain('\\', traversal.SuggestedFileName);
        Assert.DoesNotContain("..", traversal.SuggestedFileName, StringComparison.Ordinal);
        Assert.EndsWith(".csv", traversal.SuggestedFileName, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tempDirectory));
    }

    [Fact]
    public async Task Executable_and_oversized_downloads_are_rejected()
    {
        await using var engine = EngineHarness.Create(fixture.Servers, o => o.Downloads.MaxBytes = 1024 * 1024);
        await using var session = await engine.CreateSessionAsync(BrowserFixture.PublicPolicy, acceptDownloads: true);
        await session.NavigateAsync(Public("/downloads"), CancellationToken.None);

        var exe = await Assert.ThrowsAsync<BrowserOperationException>(() => session.DownloadAsync(ElementSelector.TestId("dl-exe"), CancellationToken.None));
        var big = await Assert.ThrowsAsync<BrowserOperationException>(() => session.DownloadAsync(ElementSelector.TestId("dl-big"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.DownloadRejected, exe.Code);
        Assert.Equal(BrowserErrorCode.DownloadRejected, big.Code);
        Assert.DoesNotContain(engine.Store.Items, i => i.Request.Kind == ArtifactKind.Download);
    }

    [Fact]
    public async Task Downloads_must_be_enabled_per_session()
    {
        await using var session = await fixture.Engine.CreateSessionAsync(BrowserFixture.PublicPolicy);
        await session.NavigateAsync(Public("/downloads"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BrowserOperationException>(() => session.DownloadAsync(ElementSelector.TestId("dl-csv"), CancellationToken.None));

        Assert.Equal(BrowserErrorCode.PolicyViolation, error.Code);
    }
}
