using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step4;

/// <summary>Phase 4 step 4: template syntax, references, sink rules and run-time resolution (§4, §5.1; ADR-044).</summary>
public class TemplateTests
{
    private static readonly WorkflowDefinition Declarations = Valid(With(Full, r =>
    {
        r["parameters"] = new JsonArray(
            new JsonObject { ["name"] = "crmBaseUrl", ["type"] = "url" },
            new JsonObject { ["name"] = "region", ["type"] = "string" });
        r["inputs"] = new JsonArray(
            new JsonObject { ["name"] = "email", ["type"] = "email", ["required"] = true },
            new JsonObject { ["name"] = "nickname", ["type"] = "string" },
            new JsonObject { ["name"] = "taxId", ["type"] = "string", ["sensitive"] = true },
            new JsonObject { ["name"] = "contract", ["type"] = "file" });
    })).Definition;

    private static TemplateScope Scope(params (string Id, StepOutputs Outputs)[] dominating) =>
        new TemplateScope(Declarations, new HashSet<string>(["read-id", "later", "secret-read", .. dominating.Select(d => d.Id)], StringComparer.Ordinal),
            dominating.ToDictionary(d => d.Id, d => d.Outputs, StringComparer.Ordinal));

    private static readonly StepOutputs ExtractOutputs = new(["value", "failed"]);
    private static readonly StepOutputs SensitiveExtractOutputs = new(["value", "failed"], ["value"]);

    private static string? Code(string text, TemplateSink sink, TemplateScope? scope = null) =>
        TemplateChecker.Check(text, sink, scope ?? Scope(), "$.x").Errors.FirstOrDefault()?.Code;

    // ----- Syntax -----

    [Fact]
    public void Templates_compile_into_literal_and_placeholder_segments()
    {
        var template = Template.Parse("Hello {{record.nickname}}, id {{steps.read-id.value}} op {{operation.id}}!", out var code, out _)!;

        Assert.Null(code);
        Assert.Equal(7, template.Segments.Length);
        var placeholders = template.Placeholders.ToList();
        Assert.Equal(TemplateNamespace.Record, placeholders[0].Namespace);
        Assert.Equal(("read-id", "value"), (placeholders[1].Name, placeholders[1].Output));
        Assert.Equal(TemplateNamespace.Operation, placeholders[2].Namespace);
        Assert.True(Template.Parse("plain text } and }}", out _, out _)!.IsLiteral);
    }

    [Theory]
    [InlineData("a {{ b", TemplateErrorCodes.Unterminated)]
    [InlineData("{{record.email", TemplateErrorCodes.Unterminated)]
    [InlineData("{{}}", TemplateErrorCodes.Empty)]
    [InlineData("{{env.HOME}}", TemplateErrorCodes.UnknownNamespace)]
    [InlineData("{{Record.email}}", TemplateErrorCodes.UnknownNamespace)]
    [InlineData("{{record}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{ record.email }}", TemplateErrorCodes.UnknownNamespace)]
    [InlineData("{{record.email.x}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{record.e-mail}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{steps.read-id}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{steps.Read.value}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{operation.key}}", TemplateErrorCodes.UnknownReference)]
    [InlineData("{{record.{{x}}}}", TemplateErrorCodes.InvalidReference)]
    [InlineData("{{record.a|upper}}", TemplateErrorCodes.InvalidReference)]
    public void Syntax_errors_are_rejected(string text, string code)
    {
        Assert.Null(Template.Parse(text, out var actual, out _));
        Assert.Equal(code, actual);
    }

    // ----- References -----

    [Theory]
    [InlineData("{{record.missing}}", TemplateErrorCodes.UndeclaredInput)]
    [InlineData("{{params.missing}}", TemplateErrorCodes.UndeclaredParameter)]
    [InlineData("{{secret.missing}}", TemplateErrorCodes.UndeclaredSecret)]
    [InlineData("{{steps.nowhere.value}}", TemplateErrorCodes.UnknownStep)]
    [InlineData("{{steps.later.value}}", TemplateErrorCodes.StepNotDominating)]
    public void Undeclared_and_non_dominating_references_are_rejected(string text, string code)
    {
        Assert.Equal(code, Code(text, TemplateSink.FillValue));
    }

    [Fact]
    public void Step_outputs_must_be_declared_by_the_dominating_step()
    {
        Assert.Null(Code("{{steps.read-id.value}}", TemplateSink.FillValue, Scope(("read-id", ExtractOutputs))));
        Assert.Equal(TemplateErrorCodes.UndeclaredOutput, Code("{{steps.read-id.status}}", TemplateSink.FillValue, Scope(("read-id", ExtractOutputs))));
    }

    [Theory]
    [InlineData(TemplateSink.FillValue)]
    [InlineData(TemplateSink.SelectorValue)]
    [InlineData(TemplateSink.UrlPathSegment)]
    [InlineData(TemplateSink.ResultField)]
    public void Url_parameters_are_never_ordinary_template_values(TemplateSink sink)
    {
        Assert.Equal(TemplateErrorCodes.ParameterMisuse, Code("{{params.crmBaseUrl}}", sink));
    }

    [Fact]
    public void String_parameters_are_allowed_in_value_sinks_including_url_values()
    {
        foreach (var sink in Enum.GetValues<TemplateSink>().Where(s => s != TemplateSink.UploadFile))
        {
            Assert.Null(Code("x-{{params.region}}", sink));
        }
    }

    // ----- Sink matrix (§5.1) -----

    public static TheoryData<TemplateSink, bool, bool> SinkMatrix() => new()
    {
        // sink, secret allowed, sensitive allowed
        { TemplateSink.FillValue, true, true },
        { TemplateSink.SelectValue, false, true },
        { TemplateSink.ConditionOperand, false, true },
        { TemplateSink.UrlPathSegment, false, false },
        { TemplateSink.UrlQueryValue, false, false },
        { TemplateSink.ResultField, false, false },
        { TemplateSink.ReconcileEquals, false, false },
        { TemplateSink.SelectorValue, false, false },
        { TemplateSink.ScreenshotLabel, false, false },
    };

    [Theory]
    [MemberData(nameof(SinkMatrix))]
    public void The_sink_matrix_is_enforced_for_secrets_sensitive_inputs_and_sensitive_extracts(TemplateSink sink, bool secretAllowed, bool sensitiveAllowed)
    {
        var scope = Scope(("secret-read", SensitiveExtractOutputs));
        Assert.Equal(secretAllowed ? null : TemplateErrorCodes.SecretNotAllowed, Code("{{secret.crmPassword}}", sink, scope));
        Assert.Equal(sensitiveAllowed ? null : TemplateErrorCodes.SensitiveNotAllowed, Code("{{record.taxId}}", sink, scope));
        Assert.Equal(sensitiveAllowed ? null : TemplateErrorCodes.SensitiveNotAllowed, Code("{{steps.secret-read.value}}", sink, scope));
        Assert.Null(Code("{{record.nickname}}", sink, scope));
    }

    [Fact]
    public void Taint_propagates_through_mixed_templates()
    {
        var scope = Scope(("secret-read", SensitiveExtractOutputs));

        var (_, plain, _) = TemplateChecker.Check("a {{record.nickname}} b", TemplateSink.FillValue, scope, "$");
        var (_, sensitive, _) = TemplateChecker.Check("a {{record.nickname}} {{record.taxId}}", TemplateSink.FillValue, scope, "$");
        var (_, secret, _) = TemplateChecker.Check("{{record.nickname}}:{{secret.crmPassword}}", TemplateSink.FillValue, scope, "$");

        Assert.Equal(new TemplateTaint(false, false), plain);
        Assert.Equal(new TemplateTaint(true, false), sensitive);
        Assert.Equal(new TemplateTaint(true, true), secret);
        Assert.Equal(TemplateErrorCodes.SensitiveNotAllowed, Code("prefix {{record.taxId}} suffix", TemplateSink.ResultField, scope));
    }

    [Theory]
    [InlineData("{{record.contract}}", null)]
    [InlineData("{{record.nickname}}", TemplateErrorCodes.PlaceholderNotAllowed)]
    [InlineData("files/{{record.contract}}", TemplateErrorCodes.PlaceholderNotAllowed)]
    [InlineData("C:\\report.pdf", TemplateErrorCodes.PlaceholderNotAllowed)]
    [InlineData("{{params.region}}", TemplateErrorCodes.PlaceholderNotAllowed)]
    public void Upload_file_must_be_exactly_one_file_input(string text, string? code)
    {
        Assert.Equal(code, Code(text, TemplateSink.UploadFile));
    }

    // ----- Run time -----

    private sealed class Values : ITemplateValues
    {
        public Guid OperationId { get; } = Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E");

        public Dictionary<string, string?> Record { get; } = new(StringComparer.Ordinal) { ["email"] = "ada@example.test", ["taxId"] = "TX-1" };

        public bool TryGetRecordValue(string name, out string? value) => Record.TryGetValue(name, out value);

        public bool TryGetParameter(string name, out string value)
        {
            value = "eu";
            return name == "region";
        }

        public bool TryGetSecret(string name, out string value)
        {
            value = "s3cr3t";
            return name == "crmPassword";
        }

        public bool TryGetStepOutput(string stepId, string output, out string value, out bool isSensitive)
        {
            value = "C-42";
            isSensitive = stepId == "secret-read";
            return true;
        }
    }

    private static TemplateResolution Resolve(string text, TemplateSink sink) =>
        TemplateResolver.Resolve(Template.Parse(text, out _, out _)!, sink, new Values(), TemplateResolver.InputLookup(Declarations));

    [Fact]
    public void Resolution_substitutes_values_and_renders_the_operation_id_as_lowercase_D()
    {
        var resolved = Resolve("{{record.email}}|{{params.region}}|{{steps.read-id.value}}|{{operation.id}}", TemplateSink.FillValue);

        Assert.Equal("ada@example.test|eu|C-42|0f8fad5b-d9cb-469f-a165-70867728950e", resolved.Value!.Text);
        Assert.False(resolved.Value.IsSensitive);
    }

    [Fact]
    public void Missing_optional_inputs_resolve_to_empty_unless_the_sink_requires_a_value()
    {
        Assert.Equal("a--b", Resolve("a-{{record.nickname}}-b", TemplateSink.FillValue).Value!.Text);
        Assert.Equal(EngineErrorCodes.VariableUnresolved, Resolve("{{record.nickname}}", TemplateSink.UrlPathSegment).ErrorCode);
        Assert.Equal(EngineErrorCodes.VariableUnresolved, Resolve("{{record.nickname}}", TemplateSink.ReconcileEquals).ErrorCode);
    }

    [Fact]
    public void The_run_time_sink_check_is_a_backstop()
    {
        var secret = Resolve("{{secret.crmPassword}}", TemplateSink.FillValue);
        Assert.True(secret.Value!.IsSensitive);
        Assert.True(secret.Value.ContainsSecret);

        Assert.Equal(EngineErrorCodes.SinkViolation, Resolve("{{secret.crmPassword}}", TemplateSink.SelectValue).ErrorCode);
        Assert.Equal(EngineErrorCodes.SinkViolation, Resolve("{{record.taxId}}", TemplateSink.ResultField).ErrorCode);
        Assert.Equal(EngineErrorCodes.SinkViolation, Resolve("{{steps.secret-read.value}}", TemplateSink.SelectorValue).ErrorCode);
        Assert.True(Resolve("{{record.taxId}}", TemplateSink.SelectValue).Value!.IsSensitive);
    }

    [Fact]
    public void Resolved_values_and_errors_never_print_their_text()
    {
        var secret = Resolve("{{secret.crmPassword}}", TemplateSink.FillValue).Value!;
        var plain = Resolve("{{record.email}}", TemplateSink.FillValue).Value!;
        var failure = Resolve("{{secret.crmPassword}}", TemplateSink.ResultField);

        Assert.DoesNotContain("s3cr3t", secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ada@", plain.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", failure.ErrorMessage!, StringComparison.Ordinal);
    }
}
