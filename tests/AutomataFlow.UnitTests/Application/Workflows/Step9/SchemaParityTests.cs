using System.Text.Json;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.UnitTests.Architecture;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;
using L = AutomataFlow.Application.Workflows.Definition.WorkflowLimits;

namespace AutomataFlow.UnitTests.Application.Workflows.Step9;

/// <summary>
/// Phase 4 step 9: <c>docs/workflow-schema-v1.json</c> stays consistent with the code (§1.5, ADR-055, L1): every numeric
/// bound equals its constant (and no bound is unmapped), every enum equals the code's list, and every property the
/// schema declares is known to the binder. Plus the DemoCrm sample's golden value.
/// </summary>
public class SchemaParityTests
{
    private const int NonEmpty = 1;

    private static readonly JsonNode Schema = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectGraph.RepositoryRoot, "docs", "workflow-schema-v1.json")))!;

    private static readonly string[] NumericKeywords = ["minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "default"];

    /// <summary>Every numeric bound in the schema → the constant it must equal (L1).</summary>
    private static readonly Dictionary<string, long> Expected = new(StringComparer.Ordinal)
    {
        ["/properties/name/minLength"] = L.MinNameChars,
        ["/properties/name/maxLength"] = L.MaxNameChars,
        ["/properties/description/maxLength"] = L.MaxDescriptionChars,
        ["/properties/timeoutSeconds/minimum"] = L.MinTimeoutSeconds,
        ["/properties/timeoutSeconds/maximum"] = L.MaxTimeoutSeconds,
        ["/properties/timeoutSeconds/default"] = SchemaV1Defaults.TimeoutSeconds,
        ["/properties/parameters/maxItems"] = L.MaxParameters,
        ["/properties/allowedHosts/minItems"] = NonEmpty,
        ["/properties/allowedHosts/maxItems"] = L.MaxAllowedHosts,
        ["/properties/allowedHosts/items/minLength"] = NonEmpty,
        ["/properties/allowedHosts/items/maxLength"] = L.MaxAllowedHostChars,
        ["/properties/inputs/maxItems"] = L.MaxInputs,
        ["/properties/secrets/maxItems"] = L.MaxSecrets,
        ["/properties/maskSelectors/maxItems"] = L.MaxMaskSelectors,
        ["/$defs/declaredName/minLength"] = NonEmpty,
        ["/$defs/declaredName/maxLength"] = L.MaxDeclaredNameChars,
        ["/$defs/template/maxLength"] = L.MaxGenericStringChars,
        ["/$defs/selectorValue/minLength"] = NonEmpty,
        ["/$defs/selectorValue/maxLength"] = L.MaxSelectorChars,
        ["/$defs/attributeName/minLength"] = NonEmpty,
        ["/$defs/attributeName/maxLength"] = L.MaxAttributeNameChars,
        ["/$defs/urlPattern/minLength"] = NonEmpty,
        ["/$defs/urlPattern/maxLength"] = L.MaxUrlPatternChars,
        ["/$defs/input/properties/maxLength/minimum"] = L.MinInputMaxLength,
        ["/$defs/input/properties/maxLength/maximum"] = L.MaxInputMaxLength,
        ["/$defs/input/properties/maxLength/default"] = SchemaV1Defaults.InputMaxLength,
        ["/$defs/reconciliation/properties/authority/properties/maxCommitLatencySeconds/minimum"] = L.MinCommitLatencySeconds,
        ["/$defs/reconciliation/properties/authority/properties/maxCommitLatencySeconds/maximum"] = L.MaxCommitLatencySeconds,
        ["/$defs/reconciliation/properties/authority/properties/contractReference/minLength"] = L.MinContractReferenceChars,
        ["/$defs/reconciliation/properties/authority/properties/contractReference/maxLength"] = L.MaxContractReferenceChars,
        ["/$defs/reconciliation/properties/authority/properties/maxEvidenceAgeSeconds/minimum"] = L.MinEvidenceAgeSeconds,
        ["/$defs/reconciliation/properties/authority/properties/maxEvidenceAgeSeconds/maximum"] = L.MaxEvidenceAgeSeconds,
        ["/$defs/reconciliation/properties/authority/properties/maxEvidenceAgeSeconds/default"] = SchemaV1Defaults.MaxEvidenceAgeSeconds,
        ["/$defs/steps/maxItems"] = L.MaxSteps,
        ["/$defs/step/properties/id/minLength"] = NonEmpty,
        ["/$defs/step/properties/id/maxLength"] = L.MaxStepIdChars,
        ["/$defs/step/properties/timeoutMs/minimum"] = L.MinStepTimeoutMs,
        ["/$defs/step/properties/timeoutMs/maximum"] = L.MaxStepTimeoutMs,
        ["/$defs/step/properties/description/maxLength"] = L.MaxDescriptionChars,
        ["/$defs/selector/minProperties"] = NonEmpty,
        ["/$defs/selector/maxProperties"] = 2, // one kind, plus "name" for role
        ["/$defs/navigate/properties/url/minLength"] = NonEmpty,
        ["/$defs/navigate/properties/url/maxLength"] = L.MaxUrlTemplateChars,
        ["/$defs/navigate/properties/acceptStatus/maxItems"] = L.MaxAcceptStatuses,
        ["/$defs/navigate/properties/acceptStatus/items/minimum"] = L.MinAcceptStatus,
        ["/$defs/navigate/properties/acceptStatus/items/maximum"] = L.MaxAcceptStatus,
        ["/$defs/select/properties/index/minimum"] = 0,
        ["/$defs/select/properties/index/maximum"] = L.MaxSelectIndex,
        ["/$defs/extract/properties/maxLength/minimum"] = L.MinExtractMaxLength,
        ["/$defs/extract/properties/maxLength/maximum"] = L.MaxExtractMaxLength,
        ["/$defs/extract/properties/maxLength/default"] = SchemaV1Defaults.ExtractMaxLength,
        ["/$defs/screenshot/properties/label/minLength"] = NonEmpty,
        ["/$defs/screenshot/properties/label/maxLength"] = L.MaxScreenshotLabelChars,
        ["/$defs/upload/properties/file/minLength"] = NonEmpty,
        ["/$defs/upload/properties/file/maxLength"] = L.MaxGenericStringChars,
        ["/$defs/upload/properties/allowedExtensions/minItems"] = NonEmpty,
        ["/$defs/upload/properties/allowedExtensions/maxItems"] = L.MaxUploadExtensions,
        ["/$defs/upload/properties/maxBytes/minimum"] = 1,
        ["/$defs/condition/properties/then/minItems"] = NonEmpty,
        ["/$defs/expression/minProperties"] = 1,
        ["/$defs/expression/maxProperties"] = 1,
        ["/$defs/expression/properties/all/minItems"] = NonEmpty,
        ["/$defs/expression/properties/all/maxItems"] = L.MaxConditionChildren,
        ["/$defs/expression/properties/any/minItems"] = NonEmpty,
        ["/$defs/expression/properties/any/maxItems"] = L.MaxConditionChildren,
        ["/$defs/expression/properties/elementCountAtLeast/properties/count/minimum"] = 1,
        ["/$defs/expression/properties/elementCountAtLeast/properties/count/maximum"] = L.MaxElementCount,
        ["/$defs/delay/properties/milliseconds/minimum"] = L.MinDelayMilliseconds,
        ["/$defs/delay/properties/milliseconds/maximum"] = L.MaxDelayMilliseconds,
        ["/$defs/captureResult/minProperties"] = NonEmpty,
        ["/$defs/captureResult/properties/fields/maxProperties"] = L.MaxResultFields,
        ["/$defs/captureResult/properties/fields/propertyNames/maxLength"] = L.MaxResultFieldNameChars,
        ["/$defs/captureResult/properties/fields/additionalProperties/maxLength"] = L.MaxResultFieldChars,
        ["/$defs/outcome/properties/code/maxLength"] = L.MaxOutcomeCodeChars,
        ["/$defs/reconcileMatch/properties/key/minProperties"] = 1,
        ["/$defs/reconcileMatch/properties/key/maxProperties"] = 1,
        ["/$defs/reconcileMatch/properties/maxRows/minimum"] = L.MinReconcileMaxRows,
        ["/$defs/reconcileMatch/properties/maxRows/maximum"] = L.MaxReconcileMaxRows,
        ["/$defs/reconcileMatch/properties/maxRows/default"] = SchemaV1Defaults.ReconcileMaxRows,
        ["/$defs/reconcileMatch/properties/fields/maxProperties"] = L.MaxResultFields,
        ["/$defs/reconcileMatch/properties/fields/propertyNames/maxLength"] = L.MaxResultFieldNameChars,
    };

    private static Dictionary<string, long> NumericBounds()
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string pointer)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        var child = pointer + "/" + key;
                        if (NumericKeywords.Contains(key) && value is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                        {
                            found[child] = v.GetValue<long>();
                        }
                        else
                        {
                            Walk(value, child);
                        }
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        Walk(array[i], pointer + "/" + i);
                    }

                    break;
            }
        }

        Walk(Schema, string.Empty);
        return found;
    }

    [Fact]
    public void L1_every_numeric_bound_in_the_schema_equals_its_constant_and_none_is_unmapped()
    {
        var bounds = NumericBounds();

        Assert.Empty(bounds.Keys.Except(Expected.Keys));   // a new bound in the schema must be mapped to a constant
        Assert.Empty(Expected.Keys.Except(bounds.Keys));   // a mapped bound must still exist
        Assert.All(Expected, e => Assert.True(bounds[e.Key] == e.Value, $"{e.Key}: schema {bounds[e.Key]}, code {e.Value}"));
    }

    private static IEnumerable<string> Enum(string pointer)
    {
        var node = Schema;
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : node[part]!;
        }

        return node["enum"]!.AsArray().Select(n => (string)n!);
    }

    [Fact]
    public void Every_enum_equals_the_codes_closed_list()
    {
        Assert.Equal(WorkflowJsonNames.ActionTypes.Select(p => p.Key), Enum("/$defs/step/properties/type"));
        Assert.Equal(WorkflowJsonNames.ErrorModes.Select(p => p.Key), Enum("/$defs/step/properties/onError"));
        Assert.Equal(WorkflowJsonNames.Guarantees.Select(p => p.Key), Enum("/$defs/step/properties/idempotency/properties/guarantee"));
        Assert.Equal(WorkflowJsonNames.ParameterTypes.Select(p => p.Key), Enum("/$defs/parameter/properties/type"));
        Assert.Equal(WorkflowJsonNames.InputTypes.Select(p => p.Key), Enum("/$defs/input/properties/type"));
        Assert.Equal(WorkflowJsonNames.EvidenceLevels.Select(p => p.Key), Enum("/$defs/reconciliation/properties/evidence"));
        Assert.Equal(WorkflowJsonNames.MatchKeys.Select(p => p.Key), Enum("/$defs/reconciliation/properties/matchKey"));
        Assert.Equal(["succeeded", "failed"], Enum("/$defs/outcome/properties/status"));
        Assert.Equal(["visible", "hidden", "attached", "detached", "load"], Enum("/$defs/wait/properties/for"));
        Assert.Equal(["text", "attribute"], Enum("/$defs/extract/properties/source"));
        Assert.Equal(["wait", "extract", "screenshot", "fill", "select", "condition"],
            Enum("/$defs/step/allOf/13/then/properties/type"));
        Assert.Equal(["click", "upload"], Enum("/$defs/step/allOf/14/then/properties/type"));
        Assert.Equal(SchemaV1Defaults.OnError == StepErrorMode.Fail ? "fail" : "continue", (string)Schema["$defs"]!["step"]!["properties"]!["onError"]!["default"]!);
    }

    /// <summary>Each schema property is known to the binder: a wrong-typed value is never reported as an unknown property.</summary>
    [Fact]
    public void Every_schema_property_is_known_to_the_binder_and_unknown_names_are_rejected()
    {
        foreach (var (name, _) in Schema["properties"]!.AsObject())
        {
            Assert.DoesNotContain(Errors(With(r => r[name] = new JsonObject { ["__wrong"] = 1 })), e => e.Path == "$." + name && e.Code == WorkflowErrorCodes.UnknownProperty);
        }

        foreach (var (name, _) in Schema["$defs"]!["step"]!["properties"]!.AsObject())
        {
            Assert.DoesNotContain(Errors(With(r => FirstStep(r)[name] = new JsonObject { ["__wrong"] = 1 })), e => e.Path == "$.steps[0]." + name && e.Code == WorkflowErrorCodes.UnknownProperty);
        }

        foreach (var (action, definition) in WorkflowJsonNames.ActionTypes.Select(a => (a.Key, Schema["$defs"]![a.Key]!)))
        {
            foreach (var (name, _) in definition["properties"]!.AsObject())
            {
                var config = new JsonObject { [name] = new JsonObject { ["__wrong"] = 1 } };
                var json = action == "reconcileMatch"
                    ? With(Full, r => r["reconcile"]![0]!["config"] = config)
                    : With(r => r["steps"] = new JsonArray(new JsonObject { ["id"] = "s", ["type"] = action, ["config"] = config }, Terminal()));
                var configPath = action == "reconcileMatch" ? "$.reconcile[0].config." : "$.steps[0].config.";
                Assert.DoesNotContain(Errors(json), e => e.Path == configPath + name && e.Code == WorkflowErrorCodes.UnknownProperty);
            }

            var bogus = action == "reconcileMatch"
                ? With(Full, r => r["reconcile"]![0]!["config"]!["bogus"] = 1)
                : With(r => r["steps"] = new JsonArray(new JsonObject { ["id"] = "s", ["type"] = action, ["config"] = new JsonObject { ["bogus"] = 1 } }, Terminal()));
            Assert.Contains(Errors(bogus), e => e.Path.EndsWith(".config.bogus", StringComparison.Ordinal) && e.Code == WorkflowErrorCodes.UnknownProperty);
        }
    }

    // ----- DemoCrm sample golden value -----

    private static string Sample => File.ReadAllText(Path.Combine(ProjectGraph.RepositoryRoot, "docs", "workflows", "democrm-register-customer.json"));

    /// <summary>Computed independently by the Python reference canonicalizer (the same one as the step 3 golden).</summary>
    [Fact]
    public void The_DemoCrm_sample_has_a_pinned_golden_hash()
    {
        var workflow = Valid(Sample);

        Assert.Equal(4_683, workflow.CanonicalUtf8.Length);
        Assert.Equal("sha256:df0bfb3593eb38e812532f1aaf18bcab9eea365bb089c58f4899856b7e35865c", workflow.DefinitionHash);
        Assert.True(workflow.Safety.TargetEnforced);
        Assert.True(workflow.Safety.OperationIdSent);
        Assert.Empty(workflow.Warnings);
    }
}
