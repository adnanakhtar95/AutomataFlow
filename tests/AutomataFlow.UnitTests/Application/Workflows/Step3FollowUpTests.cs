using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Regression tests for the Phase 4 step 3 audit follow-up (M1–M3, L1–L5; ADR-061).</summary>
public class Step3FollowUpTests
{
    // ----- M1: no allocation amplification -----

    /// <summary>
    /// 31 nested objects with 64-character names, then an array filling the rest of the 256 KB budget. With per-value
    /// path strings this allocated about 530 MB; with lazy paths the work is linear in the input size.
    /// </summary>
    private static string DeepWidePayload(bool insideConfig)
    {
        var name = new string('n', WorkflowLimits.MaxJsonPropertyNameChars);
        // Depth used before the chain: root object (+ steps array, step object and config object when inside a config).
        // The final array then sits exactly at the maximum depth.
        var openLevels = insideConfig ? WorkflowLimits.MaxJsonDepth - 5 : WorkflowLimits.MaxJsonDepth - 2;
        var prefix = new StringBuilder();
        for (var i = 0; i < openLevels; i++)
        {
            prefix.Append("{\"").Append(name).Append("\":");
        }

        var head = insideConfig
            ? "{\"schemaVersion\":1,\"name\":\"deep-flow\",\"allowedHosts\":[\"https://crm.example.test\"],\"steps\":[{\"id\":\"done\",\"type\":\"delay\",\"config\":{\"x\":"
            : "{\"x\":";
        var tail = new string('}', openLevels) + (insideConfig ? "}}]}" : "}");
        var budget = WorkflowLimits.MaxDefinitionBytes - head.Length - prefix.Length - tail.Length - 2;
        var items = string.Join(",", Enumerable.Repeat("1", budget / 2));
        return head + prefix + "[" + items + "]" + tail;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worst_case_nesting_and_width_do_not_amplify_allocations(bool insideConfig)
    {
        var json = DeepWidePayload(insideConfig);
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(bytes.Length <= WorkflowLimits.MaxDefinitionBytes);

        // The parser must accept the document (so the whole input is really processed). Whether the full pipeline
        // accepts the config depends on the action schema; only its cost is asserted.
        Assert.True(StrictJsonParser.Parse(bytes).IsValid);

        var before = GC.GetAllocatedBytesForCurrentThread();
        if (insideConfig)
        {
            ValidatedWorkflow.CreateForPublication(bytes);
        }
        else
        {
            StrictJsonParser.Parse(bytes);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated / (1024 * 1024)} MB for a {bytes.Length}-byte definition.");
    }

    // ----- M2: publication and loading are separate; loading requires the recorded hash -----

    [Fact]
    public void Loading_requires_a_recorded_hash()
    {
        Assert.Throws<ArgumentNullException>(() => ValidatedWorkflow.Load(Minimal, null!));
        Assert.Throws<ArgumentNullException>(() => ValidatedWorkflow.Load(Encoding.UTF8.GetBytes(Minimal), null!));
    }

    [Fact]
    public void The_only_entry_points_are_publication_and_hash_verified_loading()
    {
        var factories = typeof(ValidatedWorkflow)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(WorkflowValidationResult))
            .Select(m => m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(["CreateForPublication", "Load"], factories);
        Assert.All(
            typeof(ValidatedWorkflow).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Load"),
            m => Assert.Contains(m.GetParameters(), p => p.Name == "recordedHash" && p.ParameterType == typeof(string)));
    }

    [Fact]
    public void A_published_definition_loads_with_its_recorded_hash()
    {
        var published = Valid(Full);

        var loaded = ValidatedWorkflow.Load(Full, published.DefinitionHash);

        Assert.True(loaded.IsValid);
        Assert.Equal(published.DefinitionHash, loaded.Workflow!.DefinitionHash);
    }

    // ----- L2: execution-start verification against an external hash -----

    [Fact]
    public void VerifyAgainst_compares_with_an_externally_recorded_hash()
    {
        var workflow = Valid(Full);
        var other = Valid(Minimal);

        Assert.True(workflow.VerifyAgainst(workflow.DefinitionHash));
        Assert.False(workflow.VerifyAgainst(other.DefinitionHash));
        Assert.False(workflow.VerifyAgainst(workflow.DefinitionHash.ToUpperInvariant()));
        Assert.False(workflow.VerifyAgainst(string.Empty));
        Assert.True(workflow.VerifyIntegrity());
    }

    // ----- M3: frozen schema v1 defaults -----

    [Fact]
    public void Schema_v1_defaults_are_frozen_to_their_published_values()
    {
        Assert.Equal(120, SchemaV1Defaults.TimeoutSeconds);
        Assert.False(SchemaV1Defaults.InputRequired);
        Assert.Equal(1_000, SchemaV1Defaults.InputMaxLength);
        Assert.False(SchemaV1Defaults.InputSensitive);
        Assert.Equal(300, SchemaV1Defaults.MaxEvidenceAgeSeconds);
        Assert.Equal(StepErrorMode.Fail, SchemaV1Defaults.OnError);
        Assert.False(SchemaV1Defaults.ClickWaitForNavigation);
        Assert.False(SchemaV1Defaults.ExtractSensitive);
        Assert.Equal(1_000, SchemaV1Defaults.ExtractMaxLength);
        Assert.False(SchemaV1Defaults.ScreenshotFullPage);
        Assert.False(SchemaV1Defaults.ReconcileIgnoreCase);
        Assert.Equal(20, SchemaV1Defaults.ReconcileMaxRows);
    }

    [Fact]
    public void Omitted_properties_bind_to_the_frozen_literal_values()
    {
        var definition = Valid(With(Full, r =>
        {
            r.Remove("timeoutSeconds");
            r["inputs"] = new JsonArray(new JsonObject { ["name"] = "plain", ["type"] = "string" });
            ((JsonObject)r["reconciliation"]!["authority"]!).Remove("maxEvidenceAgeSeconds");
        })).Definition;

        Assert.Equal(120, definition.TimeoutSeconds);
        Assert.False(definition.Inputs[0].Required);
        Assert.Equal(1_000, definition.Inputs[0].MaxLength);
        Assert.False(definition.Inputs[0].Sensitive);
        Assert.Equal(300, definition.Reconciliation!.Authority!.MaxEvidenceAgeSeconds);
        Assert.Equal(StepErrorMode.Fail, definition.Steps[0].OnError);
    }

    // ----- L1: loop/regex constructs versus author-named data -----

    [Theory]
    [InlineData("matches")]
    [InlineData("repeat")]
    [InlineData("while")]
    [InlineData("Regex")]
    public void Result_field_names_are_data_and_may_use_any_name(string fieldName)
    {
        Valid(With(r => FirstStep(r)["config"] = new JsonObject { ["fields"] = new JsonObject { [fieldName] = "x" } }));
        Valid(With(Full, r => r["reconcile"]![0]!["config"] = new JsonObject
        {
            ["fields"] = new JsonObject { [fieldName] = new JsonObject { ["attribute"] = "data-id" } },
        }));
    }

    [Theory]
    [InlineData("Loop")]
    [InlineData("FOREACH")]
    [InlineData("Matches")]
    public void Forbidden_constructs_are_matched_case_insensitively(string key)
    {
        AssertRejected(With(r => FirstStep(r)["config"] = new JsonObject { [key] = true }), "$.steps[0].config." + key, WorkflowErrorCodes.ForbiddenConstruct);
    }

    [Fact]
    public void Constructs_inside_data_map_values_are_still_rejected()
    {
        AssertRejected(
            With(Full, r => r["reconcile"]![0]!["config"] = new JsonObject
            {
                ["fields"] = new JsonObject { ["id"] = new JsonObject { ["regex"] = "\\d+" } },
            }),
            "$.reconcile[0].config.fields.id.regex",
            WorkflowErrorCodes.ForbiddenConstruct);
    }

    [Fact]
    public void A_fields_map_of_other_actions_is_not_exempt()
    {
        AssertRejected(
            With(r => FirstStep(r)["type"] = "extract").Replace("\"config\":{}", "\"config\":{\"fields\":{\"loop\":1}}", StringComparison.Ordinal),
            "$.steps[0].config.fields.loop",
            WorkflowErrorCodes.ForbiddenConstruct);
    }

    // ----- L5: escaped, unambiguous, bounded diagnostics -----

    [Theory]
    [InlineData("a\nb", "$['a\\u000ab']")]
    [InlineData("a.b", "$['a.b']")]
    [InlineData("it's", "$['it\\u0027s']")]
    [InlineData("[0]", "$['[0]']")]
    [InlineData("‮", "$['\\u202e']")]
    public void Untrusted_property_names_are_escaped_in_paths(string name, string expectedPath)
    {
        var errors = Errors(With(r => r[name] = 1));

        var error = Assert.Single(errors);
        Assert.Equal(WorkflowErrorCodes.UnknownProperty, error.Code);
        Assert.Equal(expectedPath, error.Path);
        Assert.DoesNotContain(error.Message, c => char.IsControl(c) || c == '‮');
    }

    [Fact]
    public void Parser_paths_escape_untrusted_names()
    {
        var result = StrictJsonParser.Parse("{\"a\\nb\":{\"x\":1,\"x\":2}}"u8);

        Assert.Equal("$['a\\u000ab'].x", result.Error!.Path);
    }

    [Fact]
    public void The_number_of_reported_errors_is_bounded()
    {
        var errors = Errors(With(r =>
        {
            for (var i = 0; i < 500; i++)
            {
                r["extra" + i] = i;
            }
        }));

        Assert.Equal(WorkflowLimits.MaxValidationErrors + 1, errors.Length);
        Assert.Equal(WorkflowErrorCodes.TooManyErrors, errors[^1].Code);
        Assert.Single(errors, e => e.Code == WorkflowErrorCodes.TooManyErrors);
    }

    // ----- Item 10: timeout interplay and parser regressions -----

    [Fact]
    public void An_invalid_workflow_timeout_skips_the_step_ceiling_but_step_bounds_still_apply()
    {
        var errors = Errors(With(r =>
        {
            r["timeoutSeconds"] = 0;
            FirstStep(r)["timeoutMs"] = WorkflowLimits.MaxStepTimeoutMs;
        }));

        Assert.Contains(errors, e => e.Path == "$.timeoutSeconds" && e.Code == WorkflowErrorCodes.OutOfRange);
        Assert.DoesNotContain(errors, e => e.Code == WorkflowErrorCodes.ExceedsWorkflowTimeout);

        AssertRejected(
            With(r =>
            {
                r["timeoutSeconds"] = "x";
                FirstStep(r)["timeoutMs"] = WorkflowLimits.MaxStepTimeoutMs + 1;
            }),
            "$.steps[0].timeoutMs",
            WorkflowErrorCodes.OutOfRange);
    }

    [Fact]
    public void The_default_workflow_timeout_is_the_step_ceiling_when_omitted()
    {
        Valid(With(r => FirstStep(r)["timeoutMs"] = WorkflowLimits.MaxStepTimeoutMs)); // 120,000 = 120 s default
    }

    public static TheoryData<byte[]> InvalidUtf8Documents() => new()
    {
        Bytes("{\"", [0xC3, 0x28], "\":1}"),          // invalid sequence in a property name
        Bytes("{\"s\":\"", [0xC0, 0x80], "\"}"),      // overlong encoding of NUL
        Bytes("{\"s\":\"", [0xED, 0xA0, 0x80], "\"}"), // UTF-8-encoded surrogate (CESU-8)
        Bytes("{\"s\":\"", [0xF4, 0x90, 0x80, 0x80], "\"}"), // beyond U+10FFFF
        Bytes("{\"s\":\"", [0xE2, 0x82], "\"}"),       // truncated sequence
        Bytes("{\"", [0xFF], "\":1}"),                 // never-valid byte in a name
    };

    [Theory]
    [MemberData(nameof(InvalidUtf8Documents))]
    public void Invalid_UTF8_is_rejected_everywhere(byte[] document)
    {
        var result = StrictJsonParser.Parse(document);

        Assert.False(result.IsValid);
        Assert.True(result.Error!.Code is "json-invalid-string" or "json-malformed", result.Error.Code);
        Assert.Null(ValidatedWorkflow.CreateForPublication(document).Workflow);
    }

    private static byte[] Bytes(string before, byte[] middle, string after) =>
        [.. Encoding.ASCII.GetBytes(before), .. middle, .. Encoding.ASCII.GetBytes(after)];
}
