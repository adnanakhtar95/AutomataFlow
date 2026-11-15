using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Definition.Json;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 4 step 3: integrity and immutability of <see cref="ValidatedWorkflow"/> (docs/PHASE4_PLAN.md §11, §16 D5).</summary>
public class ValidatedWorkflowTests
{
    [Fact]
    public void A_valid_definition_yields_its_canonical_form_and_a_matching_hash()
    {
        var workflow = Valid(Full);

        Assert.True(CanonicalJson.IsWellFormedHash(workflow.DefinitionHash));
        Assert.Equal(CanonicalJson.ComputeHash(workflow.CanonicalUtf8.AsSpan()), workflow.DefinitionHash);
        Assert.Equal(Encoding.UTF8.GetBytes(workflow.CanonicalJson), workflow.CanonicalUtf8.ToArray());
        Assert.Equal(1, workflow.SchemaVersion);
        Assert.True(workflow.VerifyIntegrity());
    }

    [Fact]
    public void The_matching_expected_hash_is_accepted_even_for_a_differently_formatted_copy()
    {
        var stored = Valid(Full);
        var reformatted = With(Full, _ => { }); // compact, re-serialized by a different writer

        var result = ValidatedWorkflow.Load(reformatted, stored.DefinitionHash);

        Assert.True(result.IsValid);
        Assert.Equal(stored.DefinitionHash, result.Workflow!.DefinitionHash);
    }

    [Fact]
    public void A_semantic_change_under_an_old_hash_is_refused_as_tampered()
    {
        var stored = Valid(Full);
        var tampered = With(Full, r => r["allowedHosts"] = new JsonArray("{{params.crmBaseUrl}}", "https://attacker.example.test"));

        var result = ValidatedWorkflow.Load(tampered, stored.DefinitionHash);

        Assert.False(result.IsValid);
        Assert.Null(result.Workflow);
        var error = Assert.Single(result.Errors);
        Assert.Equal(WorkflowErrorCodes.WorkflowTampered, error.Code);
    }

    [Theory]
    [InlineData("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha256:")]
    [InlineData("")]
    public void A_malformed_expected_hash_is_refused_before_parsing(string hash)
    {
        var result = ValidatedWorkflow.Load(Minimal, hash);

        Assert.Null(result.Workflow);
        Assert.Equal(WorkflowErrorCodes.HashMalformed, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Hash_verification_happens_only_after_full_validation()
    {
        // An invalid definition is refused for being invalid, never accepted because a hash happens to match.
        var invalid = With(r => r["extra"] = 1);
        var hashOfInvalidCanonicalForm = CanonicalJson.ComputeHash(CanonicalJson.Serialize(StrictJsonParser.Parse(Encoding.UTF8.GetBytes(invalid)).Root!));

        var result = ValidatedWorkflow.Load(invalid, hashOfInvalidCanonicalForm);

        Assert.Null(result.Workflow);
        Assert.Contains(result.Errors, e => e.Code == WorkflowErrorCodes.UnknownProperty);
    }

    public static TheoryData<string> InvalidDefinitions() => new()
    {
        "",
        "not json",
        "{\"schemaVersion\":1,\"schemaVersion\":1}",
        "﻿" + Minimal,
        Minimal.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal),
        Minimal.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
        Minimal.Replace("\"name\":\"minimal-flow\",", string.Empty, StringComparison.Ordinal),
        Minimal.Replace("\"type\":\"captureResult\"", "\"type\":\"javascript\"", StringComparison.Ordinal),
        Minimal.Replace("\"config\":{\"outcome\"", "\"config\":{\"loop\":{},\"outcome\"", StringComparison.Ordinal),
        Minimal.Replace("{\"schemaVersion\"", "{\"x\":1,\"schemaVersion\"", StringComparison.Ordinal),
        "[" + Minimal + "]",
    };

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void Invalid_input_never_produces_a_validated_workflow(string json)
    {
        var fromText = ValidatedWorkflow.CreateForPublication(json);
        var fromBytes = ValidatedWorkflow.CreateForPublication(Encoding.UTF8.GetBytes(json));

        Assert.False(fromText.IsValid);
        Assert.Null(fromText.Workflow);
        Assert.NotEmpty(fromText.Errors);
        Assert.Null(fromBytes.Workflow);
        Assert.NotEmpty(fromBytes.Errors);
    }

    [Fact]
    public void Oversized_text_is_rejected_before_encoding()
    {
        var oversized = new string(' ', WorkflowLimits.MaxDefinitionBytes) + Minimal;

        var result = ValidatedWorkflow.CreateForPublication(oversized);

        Assert.Equal("json-too-large", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Text_with_a_lone_surrogate_is_rejected()
    {
        var result = ValidatedWorkflow.CreateForPublication(With(r => r["description"] = "ok").Replace("ok", "o\ud800k", StringComparison.Ordinal));

        Assert.Equal("json-invalid-string", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Text_and_byte_inputs_produce_the_same_workflow_identity()
    {
        var fromText = Valid(Full);
        var fromBytes = ValidatedWorkflow.CreateForPublication(Encoding.UTF8.GetBytes(Full)).Workflow!;

        Assert.Equal(fromText.DefinitionHash, fromBytes.DefinitionHash);
    }

    [Fact]
    public void The_canonical_form_round_trips_to_the_same_identity()
    {
        var original = Valid(Full);

        var reloaded = ValidatedWorkflow.Load(original.CanonicalJson, original.DefinitionHash);

        Assert.True(reloaded.IsValid);
        Assert.Equal(original.CanonicalUtf8.ToArray(), reloaded.Workflow!.CanonicalUtf8.ToArray());
    }

    [Fact]
    public void Mutating_the_caller_buffer_after_creation_does_not_affect_the_workflow()
    {
        var buffer = Encoding.UTF8.GetBytes(Minimal);
        var workflow = ValidatedWorkflow.CreateForPublication(buffer).Workflow!;
        var hash = workflow.DefinitionHash;
        var canonical = workflow.CanonicalJson;

        Array.Fill(buffer, (byte)' ');

        Assert.Equal(hash, workflow.DefinitionHash);
        Assert.Equal(canonical, workflow.CanonicalJson);
        Assert.True(workflow.VerifyIntegrity());
    }

    [Fact]
    public void ValidatedWorkflow_has_no_public_constructor_and_no_public_setter()
    {
        var type = typeof(ValidatedWorkflow);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(type.GetProperties(), p => Assert.True(p.SetMethod is null, $"{p.Name} has a setter"));
        Assert.All(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), f => Assert.True(f.IsInitOnly, $"{f.Name} is mutable"));
    }

    /// <summary>Every type reachable from the definition is sealed, has no public constructor and no public setter, and exposes only immutable collections.</summary>
    [Fact]
    public void The_definition_model_is_deeply_immutable_and_constructible_only_inside_Application()
    {
        var modelTypes = new[]
        {
            typeof(WorkflowDefinition), typeof(WorkflowParameter), typeof(WorkflowInput), typeof(ReconciliationPolicy), typeof(ReconciliationAuthority),
            typeof(WorkflowStep), typeof(IdempotencyAttestation), typeof(WorkflowSelector),
            typeof(JObject), typeof(JArray), typeof(JString), typeof(JInteger), typeof(JBoolean), typeof(JNull),
        };

        foreach (var type in modelTypes)
        {
            Assert.True(type.IsSealed, $"{type.Name} is not sealed");
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic, $"{type.Name}.{property.Name} has a public setter");
                var propertyType = property.PropertyType;
                var isMutableCollection = propertyType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType)
                    && !(propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(ImmutableArray<>));
                Assert.False(isMutableCollection, $"{type.Name}.{property.Name} is a mutable collection type ({propertyType.Name})");
            }
        }
    }

    [Fact]
    public void The_JSON_tree_cannot_be_built_outside_Application()
    {
        var jsonNodeTypes = typeof(JNode).Assembly.GetTypes().Where(t => typeof(JNode).IsAssignableFrom(t)).ToList();

        Assert.All(jsonNodeTypes, t => Assert.Empty(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)));
        Assert.DoesNotContain(
            typeof(JNode).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly);
    }
}
