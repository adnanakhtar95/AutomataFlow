using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Automation.Browser;
using AutomataFlow.AutomationTests.Support;

namespace AutomataFlow.AutomationTests.Workflows;

/// <summary>
/// Phase 4 §13.3 / §13.5 through the REAL executor, engine, proxy and Chromium against the public fixture site:
/// uploads, downloads, screenshots and masking, conditions, waits, URL parity (U8), step and session timeouts, and
/// SSRF through the workflow path.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class FixtureWorkflowTests(BrowserFixture fixture)
{
    private const string Succeed = """{ "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }""";

    private static bool IsMaskColor((byte R, byte G, byte B) p) => p.R > 240 && p.G < 20 && p.B > 240;

    private int PostsTo(string path) => fixture.Servers.PublicHits.Count(h => h.StartsWith("POST ", StringComparison.Ordinal) && h.EndsWith(path, StringComparison.Ordinal));

    // ----- uploads and downloads -----

    [Fact]
    public async Task Upload_round_trips_with_the_same_content_hash()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var content = Encoding.UTF8.GetBytes("id,name\n7,Fictional Upload\n");
        rig.Files["ref-ok"] = ("../../evil/notes.csv", "text/csv", content);
        var before = PostsTo("/upload-echo");

        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/upload" } },
              { "id": "attach", "type": "upload", "sideEffect": false, "config": { "selector": { "testId": "file" }, "file": "{{record.doc}}", "allowedExtensions": [".csv"] } },
              { "id": "send", "type": "click", "sideEffect": true, "config": { "selector": { "testId": "send" }, "waitForNavigation": true } },
              { "id": "sha", "type": "extract", "config": { "selector": { "testId": "sha" }, "source": "text", "maxLength": 64 } },
              { "id": "length", "type": "extract", "config": { "selector": { "testId": "length" }, "source": "text", "maxLength": 10 } },
              { "id": "name", "type": "extract", "config": { "selector": { "testId": "name" }, "source": "text", "maxLength": 100 } },
              { "id": "done", "type": "captureResult", "config": {
                  "fields": { "sha": "{{steps.sha.value}}", "length": "{{steps.length.value}}", "name": "{{steps.name.value}}" },
                  "outcome": { "status": "succeeded" } } }
            ]
            """, """[{ "name": "doc", "type": "file", "required": true }]"""), new() { ["doc"] = "ref-ok" });

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), result.Results["sha"]);
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Results["length"]);
        Assert.DoesNotContain("/", result.Results["name"], StringComparison.Ordinal); // sanitized file name
        Assert.EndsWith(".csv", result.Results["name"], StringComparison.Ordinal);
        Assert.Equal(before + 1, PostsTo("/upload-echo"));
    }

    [Fact]
    public async Task Upload_with_a_forged_signature_is_rejected_before_the_checkpoint_and_nothing_is_posted()
    {
        var rig = new FixtureWorkflowRig(fixture);
        rig.Files["ref-fake"] = ("invoice.pdf", "application/pdf", Encoding.UTF8.GetBytes("MZ this is not a PDF"));
        var before = PostsTo("/upload-echo");

        var (result, operation) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/upload" } },
              { "id": "attach", "type": "upload", "sideEffect": true, "config": { "selector": { "testId": "file" }, "file": "{{record.doc}}", "allowedExtensions": [".pdf"] } },
              { "id": "send", "type": "click", "sideEffect": true, "config": { "selector": { "testId": "send" }, "waitForNavigation": true } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """, """[{ "name": "doc", "type": "file", "required": true }]"""), new() { ["doc"] = "ref-fake" });

        Assert.Equal(WorkflowOutcome.Failed, result.Outcome);
        Assert.Equal(ExecutionErrorCodes.UploadRejected, result.Error!.Code);
        Assert.Equal(SideEffectState.NotStarted, result.SideEffect);
        Assert.DoesNotContain(rig.Journal.History(rig.Organization, operation), e => e.SideEffect != SideEffectState.NotStarted);
        Assert.Equal(before, PostsTo("/upload-echo"));
    }

    [Fact]
    public async Task Download_round_trips_with_its_sha256_and_artifact()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/downloads" } },
              { "id": "report", "type": "download", "config": { "trigger": { "testId": "dl-csv" } } },
              { "id": "done", "type": "captureResult", "config": {
                  "fields": { "sha": "{{steps.report.sha256}}", "file": "{{steps.report.fileName}}", "artifact": "{{steps.report.artifactId}}" },
                  "outcome": { "status": "succeeded" } } }
            ]
            """));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.True(Assert.Single(rig.Factory.Requests).AcceptDownloads, "a workflow with a download step must request downloads");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("id,name\n1,Fictional Person\n")));
        Assert.Equal(expected, result.Results["sha"]);
        Assert.Equal("report.csv", result.Results["file"]);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(artifact.Id.ToString("D"), result.Results["artifact"]);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(fixture.Engine.Store.Items.Single(i => i.Descriptor.Id == artifact.Id).Content)));
    }

    // ----- screenshots, masking and secrets -----

    [Fact]
    public async Task Screenshots_mask_secrets_and_a_sensitive_extract_from_the_moment_it_is_read()
    {
        var rig = new FixtureWorkflowRig(fixture);
        rig.Factory.TrackedTestIds.AddRange(["username", "plain-text", "card"]);
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/form" } },
              { "id": "user", "type": "fill", "config": { "selector": { "testId": "username" }, "value": "{{secret.pw}}" } },
              { "id": "card", "type": "fill", "config": { "selector": { "testId": "card" }, "value": "visible-card-text" } },
              { "id": "before", "type": "screenshot", "config": { "label": "before" } },
              { "id": "secret-read", "type": "extract", "config": { "selector": { "testId": "plain-text" }, "source": "text", "sensitive": true, "maxLength": 20 } },
              { "id": "after", "type": "screenshot", "config": { "label": "after" } },
              { "id": "done", "type": "captureResult", "config": { "fields": { "shot": "{{steps.after.artifactId}}" }, "outcome": { "status": "succeeded" } } }
            ]
            """));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Artifacts.Length);
        Assert.Equal(result.Artifacts[1].Id.ToString("D"), result.Results["shot"]);

        (byte R, byte G, byte B) Center(int shot, string testId)
        {
            var image = PngImage.Decode(fixture.Engine.Store.Items.Single(i => i.Descriptor.Id == result.Artifacts[shot].Id).Content);
            var (x, y, w, h) = rig.Factory.BoxesPerScreenshot[shot][testId];
            return image.Pixel((int)(x + (w / 2)), (int)(y + (h / 2)));
        }

        Assert.True(IsMaskColor(Center(0, "username")), "a field filled from a secret must be masked");
        Assert.False(IsMaskColor(Center(0, "card")), "a plainly filled field stays visible (the test can distinguish)");
        Assert.False(IsMaskColor(Center(0, "plain-text")), "not yet read as sensitive: still visible");
        Assert.True(IsMaskColor(Center(1, "plain-text")), "a sensitive extract must be masked in every later screenshot");
        Assert.True(IsMaskColor(Center(1, "username")));

        Assert.DoesNotContain(fixture.Engine.Sink.AllText(), t => t.Contains(FixtureWorkflowRig.SecretValue, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Engine.Logs.Lines, l => l.Contains(FixtureWorkflowRig.SecretValue, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Results.Values, v => v.Contains(FixtureWorkflowRig.SecretValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sensitive_inputs_secrets_and_sensitive_extracts_never_reach_any_output_channel()
    {
        const string SensitiveInput = "FICTIONAL-SSN-555-01-2345";
        const string PageSecret = "visible-secret-text";
        var rig = new FixtureWorkflowRig(fixture);
        var (result, operation) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/form" } },
              { "id": "ssn", "type": "fill", "config": { "selector": { "testId": "card" }, "value": "{{record.ssn}}" } },
              { "id": "user", "type": "fill", "config": { "selector": { "testId": "username" }, "value": "{{secret.pw}}" } },
              { "id": "read", "type": "extract", "config": { "selector": { "testId": "secret-text" }, "source": "text", "sensitive": true, "maxLength": 40 } },
              { "id": "shot", "type": "screenshot", "config": { "label": "evidence" } },
              { "id": "again", "type": "fill", "timeoutMs": 500, "onError": "continue", "config": { "selector": { "testId": "never" }, "value": "{{secret.pw}}" } },
              { "id": "check", "type": "condition", "config": { "if": { "valueEquals": { "left": "{{steps.read.value}}", "right": "visible-secret-text" } },
                  "then": [ { "id": "seen", "type": "captureResult", "config": { "fields": { "matched": "yes" } } } ] } },
              { "id": "broken", "type": "fill", "timeoutMs": 500, "config": { "selector": { "testId": "never" }, "value": "{{record.ssn}}" } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """, """[{ "name": "ssn", "type": "string", "required": true, "sensitive": true, "maxLength": 40 }]"""), new() { ["ssn"] = SensitiveInput });

        // The run really used the values (the comparison saw the extract) and then failed on the final fill.
        Assert.Equal("yes", result.Results["matched"]);
        Assert.Equal(WorkflowOutcome.Failed, result.Outcome);
        Assert.Equal("broken", result.Error!.StepId);

        var channels = new List<string>();
        channels.AddRange(result.Results.Keys.Concat(result.Results.Values));
        channels.AddRange(result.Steps.Select(s => s.ToString()));
        channels.AddRange(result.Diagnostics);
        channels.Add(result.Error.ToString());
        channels.AddRange(result.Artifacts.Select(a => a.ToString()));
        channels.AddRange(rig.Journal.History(rig.Organization, operation).Select(e => e.ToString()));
        channels.AddRange(fixture.Engine.Sink.AllText());
        channels.AddRange(fixture.Engine.Logs.Lines);
        channels.AddRange(rig.ExecutorLogs.Lines);
        Assert.NotEmpty(rig.ExecutorLogs.Lines);

        foreach (var value in new[] { SensitiveInput, FixtureWorkflowRig.SecretValue, PageSecret })
        {
            Assert.DoesNotContain(channels, c => c.Contains(value, StringComparison.Ordinal));
        }
    }

    // ----- conditions, waits and delays on real pages -----

    [Fact]
    public async Task Conditions_evaluate_against_real_pages()
    {
        var rig = new FixtureWorkflowRig(fixture);
        static string Check(string id, string expression) =>
            $$"""
            { "id": "{{id}}", "type": "condition", "config": { "if": {{expression}},
                "then": [ { "id": "{{id}}-yes", "type": "captureResult", "config": { "fields": { "{{id}}": "yes" } } } ],
                "else": [ { "id": "{{id}}-no", "type": "captureResult", "config": { "fields": { "{{id}}": "no" } } } ] } }
            """;

        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("[" + string.Join(",",
        [
            """{ "id": "probes", "type": "navigate", "config": { "url": "{{params.site}}/probes" } }""",
            Check("shown", """{ "elementVisible": { "testId": "shown" } }"""),
            Check("hidden", """{ "elementVisible": { "testId": "hidden" } }"""),
            """{ "id": "rows", "type": "navigate", "config": { "url": "{{params.site}}/rows" } }""",
            Check("attr", """{ "attributeEquals": { "selector": { "css": "[data-key=op-2]" }, "attribute": "data-id", "value": "C-2" } }"""),
            Check("attrno", """{ "attributeEquals": { "selector": { "css": "[data-key=op-1]" }, "attribute": "data-id", "value": "C-2" } }"""),
            """{ "id": "query", "type": "navigate", "config": { "url": "{{params.site}}/query?term=a%2Bb&page=2" } }""",
            Check("url", """{ "urlMatches": "**/query*" }"""),
            Check("urlq", """{ "urlMatches": "**/*page=2" }"""),
            Check("urlno", """{ "urlMatches": "**/rows*" }"""),
            Succeed,
        ]) + "]"));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("yes", result.Results["shown"]);
        Assert.Equal("no", result.Results["hidden"]);
        Assert.Equal("yes", result.Results["attr"]);
        Assert.Equal("no", result.Results["attrno"]);
        Assert.Equal("yes", result.Results["url"]);
        Assert.Equal("yes", result.Results["urlq"]);
        Assert.Equal("no", result.Results["urlno"]);
    }

    [Fact]
    public async Task Wait_and_delay_work_on_a_page_that_renders_late()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var clock = Stopwatch.StartNew();
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/late" } },
              { "id": "arrive", "type": "wait", "timeoutMs": 5000, "config": { "for": "visible", "selector": { "testId": "late" } } },
              { "id": "pause", "type": "delay", "config": { "milliseconds": 300 } },
              { "id": "text", "type": "extract", "config": { "selector": { "testId": "late" }, "source": "text", "maxLength": 20 } },
              { "id": "done", "type": "captureResult", "config": { "fields": { "late": "{{steps.text.value}}" }, "outcome": { "status": "succeeded" } } }
            ]
            """));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("arrived", result.Results["late"]);
        Assert.True(result.Steps.Single(s => s.StepId == "pause").DurationMs >= 290);
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(300));
    }

    // ----- U8: the URL Chromium reports matches the validated URI -----

    [Fact]
    public async Task U8_reported_page_url_keeps_the_origin_and_path_segments_of_the_validated_uri()
    {
        string[] corpus = ["plain", "a b", "a+b", "a/b", "ä-ü", "x%y", "~tilde", "semi;colon", "q?mark", "hash#tag", "comma,quote'"];
        var workflow = FixtureWorkflowRig.Workflow(
            """
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/items/{{record.segment}}/view?q={{record.segment}}" } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """,
            """[{ "name": "segment", "type": "string", "required": true, "maxLength": 40 }]""");

        var rig = new FixtureWorkflowRig(fixture);
        foreach (var value in corpus)
        {
            await rig.RunAsync(workflow, new() { ["segment"] = value });
        }

        Assert.Equal(corpus.Length, rig.Factory.Navigations.Count);
        foreach (var (requested, reported) in rig.Factory.Navigations)
        {
            var actual = new Uri(reported);
            Assert.Equal(requested.GetLeftPart(UriPartial.Authority), actual.GetLeftPart(UriPartial.Authority));
            Assert.Equal(PathSegments(requested.AbsoluteUri), PathSegments(reported));
            Assert.Equal(4, PathSegments(reported).Length); // "", items, <value>, view: the value never adds or removes a segment
        }
    }

    private static string[] PathSegments(string url)
    {
        var start = url.IndexOf('/', url.IndexOf("//", StringComparison.Ordinal) + 2);
        var end = url.IndexOfAny(['?', '#'], start);
        return url[start..(end < 0 ? url.Length : end)].Split('/');
    }

    // ----- step and session timeouts -----

    [Fact]
    public async Task A_timed_out_wait_or_extract_with_continue_is_followed_by_successful_steps_in_the_same_session()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/probes" } },
              { "id": "missing-wait", "type": "wait", "timeoutMs": 500, "onError": "continue", "config": { "for": "visible", "selector": { "testId": "never" } } },
              { "id": "missing-read", "type": "extract", "timeoutMs": 500, "onError": "continue", "config": { "selector": { "testId": "never" }, "source": "text" } },
              { "id": "shown", "type": "extract", "config": { "selector": { "testId": "shown" }, "source": "text", "maxLength": 10 } },
              { "id": "done", "type": "captureResult", "config": { "fields": { "shown": "{{steps.shown.value}}" }, "outcome": { "status": "succeeded" } } }
            ]
            """));

        Assert.Equal(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal("shown", result.Results["shown"]);
        Assert.Equal(StepStatus.FailedContinued, result.Steps.Single(s => s.StepId == "missing-wait").Status);
        Assert.Equal(StepStatus.FailedContinued, result.Steps.Single(s => s.StepId == "missing-read").Status);
        Assert.Single(rig.Factory.Requests);
    }

    [Fact]
    public async Task A_side_effect_timeout_against_a_form_that_never_responds_is_Indeterminate_with_a_checkpoint_at_about_5_seconds()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var before = PostsTo("/hang");
        var (result, operation) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/hang-form" } },
              { "id": "go", "type": "click", "sideEffect": true, "timeoutMs": 5000, "config": { "selector": { "testId": "go" }, "waitForNavigation": true } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """));

        Assert.Equal(WorkflowOutcome.Indeterminate, result.Outcome);
        Assert.Equal(nameof(BrowserErrorCode.Timeout), result.Error!.Code);
        Assert.Equal(SideEffectState.Started, result.SideEffect);
        Assert.Contains(rig.Journal.History(rig.Organization, operation), e => e.SideEffect == SideEffectState.Started && e.SideEffectCheckpointAt is not null);
        Assert.Equal(before + 1, PostsTo("/hang"));

        // T4: click + navigation share ONE 5 s budget, not ActionSeconds + NavigationSeconds.
        var go = result.Steps.Single(s => s.StepId == "go");
        Assert.InRange(go.DurationMs, 4500, 8000);
    }

    [Fact]
    public async Task The_session_deadline_cancels_a_run_without_a_side_effect()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var clock = Stopwatch.StartNew();
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/probes" } },
              { "id": "first", "type": "wait", "timeoutMs": 2500, "onError": "continue", "config": { "for": "visible", "selector": { "testId": "never" } } },
              { "id": "second", "type": "wait", "timeoutMs": 2500, "config": { "for": "visible", "selector": { "testId": "never" } } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """, timeoutSeconds: 3));

        Assert.Equal(WorkflowOutcome.Cancelled, result.Outcome);
        Assert.Equal(SideEffectState.NotStarted, result.SideEffect);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the deadline must end the run promptly ({clock.Elapsed})");
        Assert.True(result.ExecutionFenced);
    }

    [Fact]
    public async Task The_session_deadline_after_a_dispatched_side_effect_is_Indeterminate()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var (result, operation) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/hang-form" } },
              { "id": "settle", "type": "delay", "config": { "milliseconds": 3000 } },
              { "id": "go", "type": "click", "sideEffect": true, "timeoutMs": 5000, "config": { "selector": { "testId": "go" }, "waitForNavigation": true } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """, timeoutSeconds: 6));

        // The 6 s session deadline expires about 3 s into the 5 s click: cancellation, not the step timeout, ends it.
        Assert.Null(result.Steps.FirstOrDefault(s => s.StepId == "go"));

        Assert.Equal(WorkflowOutcome.Indeterminate, result.Outcome);
        Assert.Equal(SideEffectState.Started, result.SideEffect);
        Assert.Contains(rig.Journal.History(rig.Organization, operation), e => e.SideEffect == SideEffectState.Started && e.SideEffectCheckpointAt is not null);
    }

    // ----- §13.5 SSRF through the workflow path -----

    [Fact]
    public async Task A_redirect_from_an_allowed_page_to_an_internal_address_is_blocked()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var internalHits = fixture.Servers.InternalHits.Count;
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/redirect?to={{record.target}}" } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """, """[{ "name": "target", "type": "string", "required": true, "maxLength": 100 }]"""), new() { ["target"] = $"http://127.0.0.1:{fixture.Servers.InternalPort}/admin" });

        Assert.Equal(WorkflowOutcome.Failed, result.Outcome);
        Assert.Equal(nameof(BrowserErrorCode.NavigationBlocked), result.Error!.Code);
        Assert.Equal(internalHits, fixture.Servers.InternalHits.Count);
    }

    [Fact]
    public async Task A_private_address_parameter_is_refused_by_the_binder_outside_Development()
    {
        var rig = new FixtureWorkflowRig(fixture, ExecutionMode.SelfHosted);
        var internalHits = fixture.Servers.InternalHits.Count;
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/" } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """), site: $"http://127.0.0.1:{fixture.Servers.InternalPort}");

        Assert.Equal(WorkflowOutcome.Refused, result.Outcome);
        Assert.Equal("PolicyViolation", result.Error!.Code);
        Assert.Empty(rig.Factory.Requests); // no browser was ever started
        Assert.Equal(internalHits, fixture.Servers.InternalHits.Count);
    }

    [Fact]
    public async Task An_untrusted_private_address_parameter_never_reaches_the_target_in_Development()
    {
        var rig = new FixtureWorkflowRig(fixture);
        var internalHits = fixture.Servers.InternalHits.Count;
        var (result, _) = await rig.RunAsync(FixtureWorkflowRig.Workflow("""
            [
              { "id": "open", "type": "navigate", "config": { "url": "{{params.site}}/" } },
              { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
            ]
            """), site: $"http://127.0.0.1:{fixture.Servers.InternalPort}");

        // Either the binder refuses it, or the egress layer blocks it; never a request to the internal service.
        Assert.Contains(result.Outcome, new[] { WorkflowOutcome.Refused, WorkflowOutcome.Failed });
        Assert.NotEqual(WorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal(internalHits, fixture.Servers.InternalHits.Count);
    }
}
