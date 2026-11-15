using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Records;
using AutomataFlow.Application.Workflows.Templates;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step4;

/// <summary>Phase 4 step 4: record validation, parameter binding and private-network rules (§4, §6.2, §16 D7, R3).</summary>
public class RecordAndBindingTests
{
    private static readonly WorkflowDefinition Typed = Valid(With(r => r["inputs"] = new JsonArray(
        new JsonObject { ["name"] = "text", ["type"] = "string", ["required"] = true },
        new JsonObject { ["name"] = "email", ["type"] = "email" },
        new JsonObject { ["name"] = "count", ["type"] = "integer" },
        new JsonObject { ["name"] = "amount", ["type"] = "decimal" },
        new JsonObject { ["name"] = "flag", ["type"] = "boolean" },
        new JsonObject { ["name"] = "since", ["type"] = "date" },
        new JsonObject { ["name"] = "doc", ["type"] = "file" },
        new JsonObject { ["name"] = "secretNote", ["type"] = "string", ["sensitive"] = true, ["maxLength"] = 5 }))).Definition;

    private static (ValidatedRecord? Record, string? Code) Check(params (string Name, string? Value)[] values)
    {
        var record = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        record.TryAdd("text", "x");
        var (validated, errors) = RecordValidator.Validate(Typed, record);
        return (validated, errors.FirstOrDefault()?.Code);
    }

    // ----- Records -----

    [Theory]
    [InlineData("email", "ada@example.test")]
    [InlineData("email", "a.b+tag@sub.example.test")]
    [InlineData("count", "42")]
    [InlineData("count", "-7")]
    [InlineData("count", "9223372036854775807")]
    [InlineData("amount", "12.50")]
    [InlineData("amount", "-0.5")]
    [InlineData("amount", "3")]
    [InlineData("flag", "true")]
    [InlineData("flag", "false")]
    [InlineData("since", "2026-02-28")]
    [InlineData("doc", "file-ref-123")]
    [InlineData("text", "tab\tseparated")]
    public void Valid_values_of_each_type_are_accepted(string name, string value)
    {
        Assert.Null(Check((name, value)).Code);
    }

    [Theory]
    [InlineData("email", "no-at-sign")]
    [InlineData("email", "a@b")]
    [InlineData("email", "a@@b.test")]
    [InlineData("email", "a b@example.test")]
    [InlineData("email", "@example.test")]
    [InlineData("count", "4.2")]
    [InlineData("count", "007")]
    [InlineData("count", "+7")]
    [InlineData("count", "-0")]
    [InlineData("count", "9223372036854775808")]
    [InlineData("count", "1e3")]
    [InlineData("amount", "1,5")]
    [InlineData("amount", "1e3")]
    [InlineData("amount", ".5")]
    [InlineData("amount", "5.")]
    [InlineData("flag", "True")]
    [InlineData("flag", "1")]
    [InlineData("since", "2026-02-30")]
    [InlineData("since", "15/11/2026")]
    [InlineData("doc", " padded ")]
    public void Values_that_do_not_match_their_type_are_rejected(string name, string value)
    {
        Assert.Equal("record-invalid-type", Check((name, value)).Code);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u007fb")]
    public void Control_characters_other_than_tab_and_lone_surrogates_are_rejected(string value)
    {
        Assert.Equal("record-invalid-characters", Check(("text", value)).Code);
    }

    /// <summary>Built in code: xUnit serializes [InlineData] strings and would replace lone surrogates with U+FFFD.</summary>
    [Fact]
    public void Lone_surrogates_are_rejected()
    {
        Assert.Equal("record-invalid-characters", Check(("text", "a" + (char)0xD800 + "b")).Code);
        Assert.Equal("record-invalid-characters", Check(("text", ((char)0xDC00).ToString())).Code);
        Assert.Null(Check(("text", "\U0001F600")).Code);
    }

    [Fact]
    public void Required_values_unknown_fields_and_max_length_are_enforced()
    {
        var (_, missing) = RecordValidator.Validate(Typed, new Dictionary<string, string?>());
        Assert.Equal("record-required", Assert.Single(missing).Code);

        Assert.Equal("record-unknown-field", Check(("injected", "x")).Code);
        Assert.Null(Check(("secretNote", "12345")).Code);
        Assert.Equal("record-too-long", Check(("secretNote", "123456")).Code);
    }

    [Fact]
    public void The_default_max_length_is_1000()
    {
        Assert.Null(Check(("text", new string('a', 1_000))).Code);
        Assert.Equal("record-too-long", Check(("text", new string('a', 1_001))).Code);
    }

    [Fact]
    public void Validated_records_flag_sensitive_inputs_and_errors_never_contain_values()
    {
        var (record, _) = Check(("secretNote", "pw1"), ("email", "ada@example.test"));
        Assert.Contains("secretNote", record!.SensitiveInputs);
        Assert.DoesNotContain("pw1", record.ToString(), StringComparison.Ordinal);

        var (_, errors) = RecordValidator.Validate(Typed, new Dictionary<string, string?> { ["text"] = "x", ["email"] = "leak-me-please", ["secretNote"] = "toolong-secret" });
        Assert.All(errors, e => Assert.DoesNotContain("leak-me", e.Message, StringComparison.Ordinal));
        Assert.All(errors, e => Assert.DoesNotContain("toolong-secret", e.Message, StringComparison.Ordinal));
    }

    // ----- Binding -----

    private static readonly WorkflowDefinition WithParameters = Valid(With(Full, r =>
    {
        r["parameters"] = new JsonArray(
            new JsonObject { ["name"] = "crmBaseUrl", ["type"] = "url" },
            new JsonObject { ["name"] = "crmUser", ["type"] = "string" });
        r["allowedHosts"] = new JsonArray("{{params.crmBaseUrl}}", "https://cdn.example.test");
    })).Definition;

    private static readonly BindingOptions Production = new() { IsDevelopment = false };

    private static readonly BindingOptions Development = new() { IsDevelopment = true, DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:5080"] };

    private static (BoundWorkflow? Bound, string? Code) Bind(BindingOptions options, string url = "https://crm.example.test", string? user = "agent")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["crmBaseUrl"] = url };
        if (user is not null)
        {
            parameters["crmUser"] = user;
        }

        var (bound, errors) = WorkflowBinder.Bind(WithParameters, parameters, options);
        return (bound, errors.FirstOrDefault()?.Code);
    }

    [Fact]
    public void Binding_canonicalizes_url_parameters_and_builds_the_egress_policy()
    {
        var (bound, code) = Bind(Production, "https://CRM.example.test:443/");

        Assert.Null(code);
        Assert.Equal("https://crm.example.test", bound!.UrlParameters["crmBaseUrl"].ToString());
        Assert.Equal("agent", bound.TextParameters["crmUser"]);
        Assert.Equal(["https://crm.example.test:443", "https://cdn.example.test:443"], bound.EgressPolicy.Rules.Select(r => r.ToString()));
        Assert.All(bound.EgressPolicy.Rules, r => Assert.False(r.AllowPrivateNetwork));
    }

    [Theory]
    [InlineData("http://crm.example.test")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.5:8443")]
    [InlineData("https://[::1]")]
    [InlineData("https://169.254.169.254")]
    public void Http_and_private_ip_parameters_are_rejected_outside_Development(string url)
    {
        Assert.Equal("parameter-invalid", Bind(Production, url).Code);
    }

    [Theory]
    [InlineData("https://crm.example.test/path")]
    [InlineData("https://crm.example.test?x=1")]
    [InlineData("https://user@crm.example.test")]
    [InlineData("ftp://crm.example.test")]
    public void Url_parameters_must_be_bare_origins(string url)
    {
        Assert.Equal("parameter-invalid", Bind(Development, url).Code);
    }

    [Fact]
    public void Private_network_is_granted_only_in_Development_and_only_for_an_exactly_trusted_origin()
    {
        var trusted = Bind(Development, "http://127.0.0.1:5080/").Bound!;
        Assert.True(trusted.EgressPolicy.Rules.Single(r => r.Host == "127.0.0.1").AllowPrivateNetwork);

        var otherPort = Bind(Development, "http://127.0.0.1:5081").Bound!;
        Assert.False(otherPort.EgressPolicy.Rules.Single(r => r.Host == "127.0.0.1").AllowPrivateNetwork);

        var productionWithList = new BindingOptions { IsDevelopment = false, DevelopmentPrivateNetworkOrigins = ["https://crm.example.test"] };
        Assert.All(Bind(productionWithList).Bound!.EgressPolicy.Rules, r => Assert.False(r.AllowPrivateNetwork));
    }

    [Fact]
    public void Parameters_must_be_complete_and_declared()
    {
        Assert.Equal("parameter-missing", Bind(Production, user: null).Code);

        var (_, errors) = WorkflowBinder.Bind(WithParameters, new Dictionary<string, string>
        {
            ["crmBaseUrl"] = "https://crm.example.test",
            ["crmUser"] = "agent",
            ["allowPrivateNetwork"] = "true",
        }, Production);
        Assert.Equal("parameter-unknown", Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb")]
    [InlineData("a\u0000")]
    public void String_parameters_are_validated(string value)
    {
        Assert.Equal("parameter-invalid", Bind(Production, user: value).Code);
    }

    [Fact]
    public void String_parameter_length_bounds()
    {
        Assert.Null(Bind(Production, user: new string('u', WorkflowBinder.MaxTextParameterChars)).Code);
        Assert.Equal("parameter-invalid", Bind(Production, user: new string('u', WorkflowBinder.MaxTextParameterChars + 1)).Code);
    }

    [Fact]
    public void The_workflow_timeout_may_not_exceed_the_server_session_limit()
    {
        var options = new BindingOptions { MaxSessionSeconds = 60 };
        var (_, errors) = WorkflowBinder.Bind(WithParameters, new Dictionary<string, string> { ["crmBaseUrl"] = "https://crm.example.test", ["crmUser"] = "a" }, options);

        Assert.Equal(EngineErrorCodes.WorkflowTimeoutExceedsServerLimit, Assert.Single(errors).Code);
    }

    [Fact]
    public void Binding_errors_never_contain_parameter_values()
    {
        var (_, errors) = WorkflowBinder.Bind(WithParameters, new Dictionary<string, string> { ["crmBaseUrl"] = "https://secret-host.example.test/path", ["crmUser"] = "x\ny" }, Production);

        Assert.All(errors, e => Assert.DoesNotContain("secret-host", e.Message, StringComparison.Ordinal));
    }

    // ----- Authoring-time allowedHosts and selector templates -----

    [Theory]
    [InlineData("https://CRM.example.test", "allowed-host-not-canonical")]
    [InlineData("https://crm.example.test:443", "allowed-host-not-canonical")]
    [InlineData("https://crm.example.test/", "allowed-host-not-canonical")]
    [InlineData("https://crm.example.test/app", "allowed-host-not-canonical")]
    [InlineData("https://crm.example.test.", "allowed-host-not-canonical")]
    [InlineData("crm.example.test", "allowed-host-not-canonical")]
    [InlineData("https://*.10.0.0.1", "allowed-host-not-canonical")]
    [InlineData("http://crm.example.test", "allowed-host-not-https")]
    [InlineData("https://10.0.0.1", "allowed-host-private")]
    [InlineData("https://127.0.0.1:5080", "allowed-host-private")]
    [InlineData("https://[::1]", "allowed-host-private")]
    [InlineData("https://203.0.113.10", "allowed-host-private")]
    [InlineData("{{params.crmUser}}", TemplateErrorCodes.ParameterMisuse)]
    [InlineData("{{params.nope}}", TemplateErrorCodes.UndeclaredParameter)]
    [InlineData("{{params.crmBaseUrl}}/x", "allowed-host-invalid")]
    [InlineData("https://{{record.email}}", "allowed-host-invalid")]
    public void Allowed_hosts_are_canonical_https_origins_or_url_parameters(string entry, string code)
    {
        AssertRejected(With(Full, r => r["allowedHosts"] = new JsonArray(entry)), "$.allowedHosts[0]", code);
    }

    [Theory]
    [InlineData("https://crm.example.test")]
    [InlineData("https://crm.example.test:8443")]
    [InlineData("https://*.example.test")]
    [InlineData("https://93.184.216.34")]
    [InlineData("{{params.crmBaseUrl}}")]
    public void Valid_allowed_hosts_are_accepted(string entry)
    {
        Valid(With(Full, r => r["allowedHosts"] = new JsonArray(entry)));
    }

    [Theory]
    [InlineData("{\"testId\":\"row-{{record.missing}}\"}", "$.maskSelectors[0].testId", TemplateErrorCodes.UndeclaredInput)]
    [InlineData("{\"text\":\"{{secret.crmPassword}}\"}", "$.maskSelectors[0].text", TemplateErrorCodes.SecretNotAllowed)]
    [InlineData("{\"label\":\"{{record.taxId}}\"}", "$.maskSelectors[0].label", TemplateErrorCodes.SensitiveNotAllowed)]
    [InlineData("{\"role\":\"row\",\"name\":\"{{params.crmBaseUrl}}\"}", "$.maskSelectors[0].name", TemplateErrorCodes.ParameterMisuse)]
    [InlineData("{\"placeholder\":\"{{steps.submit.value}}\"}", "$.maskSelectors[0].placeholder", TemplateErrorCodes.StepNotDominating)]
    [InlineData("{\"testId\":\"{{record.email\"}", "$.maskSelectors[0].testId", TemplateErrorCodes.Unterminated)]
    public void Selector_templates_follow_the_reference_and_sink_rules(string selector, string path, string code)
    {
        AssertRejected(With(Full, r => r["maskSelectors"] = new JsonArray(JsonNode.Parse(selector))), path, code);
    }

    [Fact]
    public void Sensitive_inputs_without_mask_selectors_produce_a_warning_not_an_error()
    {
        var workflow = Valid(With(Full, r => r.Remove("maskSelectors")));

        Assert.Contains(workflow.Warnings, w => w.Code == "mask-selectors-recommended");
        Assert.Empty(Valid(Full).Warnings);
    }
}
