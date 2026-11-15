using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Definition;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 4 step 3: binding and structural validation of schemaVersion 1 (docs/PHASE4_PLAN.md §1, §13.1).</summary>
public class WorkflowDefinitionParserTests
{
    private const string RequiredCode = WorkflowErrorCodes.Required;

    // ----- Valid definitions -----

    [Fact]
    public void A_minimal_definition_binds_with_documented_defaults()
    {
        var definition = Valid(Minimal).Definition;

        Assert.Equal(1, definition.SchemaVersion);
        Assert.Equal("minimal-flow", definition.Name);
        Assert.Null(definition.Description);
        Assert.Equal(SchemaV1Defaults.TimeoutSeconds, definition.TimeoutSeconds);
        Assert.Equal("https://crm.example.test", Assert.Single(definition.AllowedHosts));
        Assert.Empty(definition.Parameters);
        Assert.Empty(definition.Inputs);
        Assert.Empty(definition.Secrets);
        Assert.Empty(definition.MaskSelectors);
        Assert.Empty(definition.Setup);
        Assert.False(definition.HasReconcileSection);
        Assert.Empty(definition.Reconcile);
        Assert.Null(definition.Reconciliation);

        var step = Assert.Single(definition.Steps);
        Assert.Equal("done", step.Id);
        Assert.Equal(ActionType.CaptureResult, step.Type);
        Assert.Equal("outcome", Assert.Single(step.Config.Properties).Key);
        var capture = Assert.IsType<AutomataFlow.Application.Workflows.Actions.CaptureResultConfig>(step.Action);
        Assert.True(capture.IsTerminal);
        Assert.Empty(capture.Fields);
        Assert.Null(step.TimeoutMs);
        Assert.Equal(StepErrorMode.Fail, step.OnError);
        Assert.Null(step.SideEffect);
        Assert.Null(step.Idempotency);
        Assert.Null(step.Description);
    }

    [Fact]
    public void A_full_definition_binds_every_field()
    {
        var definition = Valid(Full).Definition;

        Assert.Equal("democrm-register-customer", definition.Name);
        Assert.Equal("Registers one customer in a fictional CRM.", definition.Description);
        Assert.Equal(90, definition.TimeoutSeconds);
        Assert.Equal(
            [("crmBaseUrl", WorkflowParameterType.Url), ("crmUser", WorkflowParameterType.Text)],
            definition.Parameters.Select(p => (p.Name, p.Type)));
        Assert.Equal("{{params.crmBaseUrl}}", Assert.Single(definition.AllowedHosts));

        Assert.Equal(
            [WorkflowInputType.Email, WorkflowInputType.Text, WorkflowInputType.Text, WorkflowInputType.WholeNumber, WorkflowInputType.DecimalNumber,
             WorkflowInputType.Boolean, WorkflowInputType.Date, WorkflowInputType.File],
            definition.Inputs.Select(i => i.Type));
        var email = definition.Inputs[0];
        Assert.True(email.Required);
        Assert.Equal(120, email.MaxLength);
        Assert.False(email.Sensitive);
        var company = definition.Inputs[1];
        Assert.False(company.Required);
        Assert.Equal(SchemaV1Defaults.InputMaxLength, company.MaxLength);
        Assert.True(definition.Inputs[2].Sensitive);

        Assert.Equal("crmPassword", Assert.Single(definition.Secrets));
        Assert.Equal(WorkflowSelectorKind.Css, definition.MaskSelectors[0].Kind);
        Assert.Equal("[data-testid=detail-tax-id]", definition.MaskSelectors[0].Value);
        Assert.Equal(WorkflowSelectorKind.Role, definition.MaskSelectors[1].Kind);
        Assert.Equal("textbox", definition.MaskSelectors[1].Value);
        Assert.Equal("Tax id", definition.MaskSelectors[1].AccessibleName);

        var reconciliation = definition.Reconciliation!;
        Assert.Equal(EvidenceLevel.Authoritative, reconciliation.Evidence);
        Assert.Equal(MatchKeyKind.OperationId, reconciliation.MatchKey);
        Assert.Equal(30, reconciliation.Authority!.MaxCommitLatencySeconds);
        Assert.Equal("Fictional CRM runbook 4.2: synchronous commit", reconciliation.Authority.ContractReference);
        Assert.Equal(SchemaV1Defaults.MaxEvidenceAgeSeconds, reconciliation.Authority.MaxEvidenceAgeSeconds);

        Assert.Equal(["login", "op"], definition.Setup.Select(s => s.Id));
        Assert.Equal(ActionType.Navigate, definition.Setup[0].Type);
        Assert.Equal("Open the login page", definition.Setup[0].Description);
        Assert.True(definition.HasReconcileSection);
        Assert.Equal(ActionType.ReconcileMatch, Assert.Single(definition.Reconcile).Type);

        var submit = definition.Steps[0];
        Assert.Equal(ActionType.Click, submit.Type);
        Assert.Equal(15_000, submit.TimeoutMs);
        Assert.True(submit.SideEffect);
        Assert.Equal(IdempotencyAttestation.OperationIdKey, submit.Idempotency!.Key);
        Assert.Equal(IdempotencyGuarantee.TargetEnforced, submit.Idempotency.Guarantee);
        Assert.True(submit.Config.TryGet("selector", out _));
        Assert.Equal(StepErrorMode.Continue, definition.Steps[1].OnError);
    }

    [Fact]
    public void Every_action_type_name_binds()
    {
        // The kitchen-sink definition places every action type in a valid position; each binds to its type.
        var bound = Valid(KitchenSink).Definition.AllSteps().Select(s => s.Type).ToHashSet();
        Assert.Equal(Enum.GetValues<ActionType>().ToHashSet(), bound);

        Assert.Equal(13, WorkflowJsonNames.ActionTypes.Length);
        Assert.Equal(Enum.GetValues<ActionType>().Order(), WorkflowJsonNames.ActionTypes.Select(p => p.Value).Order());
    }

    // ----- Root and schema version -----

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"workflow\"")]
    [InlineData("null")]
    public void The_root_must_be_an_object(string json)
    {
        AssertRejected(json, "$", WorkflowErrorCodes.InvalidType);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("-1")]
    public void Unknown_schema_versions_are_refused(string version)
    {
        AssertRejected(With(r => r["schemaVersion"] = JsonNode.Parse(version)), "$.schemaVersion", WorkflowErrorCodes.UnsupportedSchemaVersion);
    }

    [Fact]
    public void Schema_version_must_be_present_and_an_integer()
    {
        AssertRejected(With(r => r.Remove("schemaVersion")), "$.schemaVersion", RequiredCode);
        AssertRejected(With(r => r["schemaVersion"] = "1"), "$.schemaVersion", WorkflowErrorCodes.InvalidType);
        AssertRejected(With(r => r["schemaVersion"] = true), "$.schemaVersion", WorkflowErrorCodes.InvalidType);
    }

    // ----- Required fields -----

    [Theory]
    [InlineData("name")]
    [InlineData("allowedHosts")]
    [InlineData("steps")]
    public void Required_top_level_fields(string property)
    {
        AssertRejected(With(r => r.Remove(property)), "$." + property, RequiredCode);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("config")]
    public void Required_step_fields(string property)
    {
        AssertRejected(With(r => FirstStep(r).Remove(property)), "$.steps[0]." + property, RequiredCode);
    }

    [Fact]
    public void Required_declaration_fields()
    {
        AssertRejected(With(r => r["parameters"] = new JsonArray(new JsonObject { ["type"] = "url" })), "$.parameters[0].name", RequiredCode);
        AssertRejected(With(r => r["parameters"] = new JsonArray(new JsonObject { ["name"] = "p" })), "$.parameters[0].type", RequiredCode);
        AssertRejected(With(r => r["inputs"] = new JsonArray(new JsonObject { ["type"] = "string" })), "$.inputs[0].name", RequiredCode);
        AssertRejected(With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = "email" })), "$.inputs[0].type", RequiredCode);
    }

    [Fact]
    public void Required_reconciliation_fields()
    {
        AssertRejected(With(Full, r => r["reconciliation"]!.AsObject().Remove("evidence")), "$.reconciliation.evidence", RequiredCode);
        AssertRejected(With(Full, r => r["reconciliation"]!.AsObject().Remove("matchKey")), "$.reconciliation.matchKey", RequiredCode);
        AssertRejected(With(Full, r => Authority(r).Remove("maxCommitLatencySeconds")), "$.reconciliation.authority.maxCommitLatencySeconds", RequiredCode);
        AssertRejected(With(Full, r => Authority(r).Remove("contractReference")), "$.reconciliation.authority.contractReference", RequiredCode);
        AssertRejected(With(Full, r => FirstStep(r)["idempotency"]!.AsObject().Remove("key")), "$.steps[0].idempotency.key", RequiredCode);
        AssertRejected(With(Full, r => FirstStep(r)["idempotency"]!.AsObject().Remove("guarantee")), "$.steps[0].idempotency.guarantee", RequiredCode);
    }

    // ----- Unknown and forbidden properties at every level -----

    [Fact]
    public void Unknown_properties_are_rejected_at_every_level()
    {
        const string unknown = WorkflowErrorCodes.UnknownProperty;
        AssertRejected(With(r => r["extra"] = 1), "$.extra", unknown);
        AssertRejected(With(r => r["Name"] = "x"), "$.Name", unknown);
        AssertRejected(With(r => FirstStep(r)["script"] = "alert(1)"), "$.steps[0].script", unknown);
        AssertRejected(With(Full, r => r["parameters"]![0]!.AsObject()["default"] = "x"), "$.parameters[0].default", unknown);
        AssertRejected(With(Full, r => r["inputs"]![0]!.AsObject()["pattern"] = ".*"), "$.inputs[0].pattern", unknown);
        AssertRejected(With(Full, r => r["reconciliation"]!.AsObject()["minDelaySeconds"] = 5), "$.reconciliation.minDelaySeconds", unknown);
        AssertRejected(With(Full, r => Authority(r)["extra"] = 1), "$.reconciliation.authority.extra", unknown);
        AssertRejected(With(Full, r => FirstStep(r)["idempotency"]!.AsObject()["scope"] = "x"), "$.steps[0].idempotency.scope", unknown);
        AssertRejected(With(Full, r => r["maskSelectors"]![0]!.AsObject()["nth"] = 1), "$.maskSelectors[0].nth", unknown);
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("repeat")]
    [InlineData("forEach")]
    [InlineData("while")]
    [InlineData("regex")]
    [InlineData("matches")]
    public void Loop_and_regex_keys_are_forbidden_constructs_anywhere(string key)
    {
        AssertRejected(With(r => r[key] = new JsonObject()), "$." + key, WorkflowErrorCodes.ForbiddenConstruct);
        AssertRejected(With(r => FirstStep(r)[key] = true), "$.steps[0]." + key, WorkflowErrorCodes.ForbiddenConstruct);
        AssertRejected(
            With(r => r["steps"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "check",
                    ["type"] = "condition",
                    ["config"] = new JsonObject
                    {
                        ["if"] = new JsonObject { ["all"] = new JsonArray(new JsonObject { [key] = "x" }) },
                        ["then"] = new JsonArray(Step("inner")),
                    },
                },
                Terminal())),
            "$.steps[0].config.if.all[0]." + key,
            WorkflowErrorCodes.ForbiddenConstruct);
    }

    // ----- Types and enum values -----

    [Fact]
    public void Wrong_types_are_rejected_with_their_path()
    {
        const string invalid = WorkflowErrorCodes.InvalidType;
        AssertRejected(With(r => r["name"] = 5), "$.name", invalid);
        AssertRejected(With(r => r["description"] = new JsonArray()), "$.description", invalid);
        AssertRejected(With(r => r["timeoutSeconds"] = "120"), "$.timeoutSeconds", invalid);
        AssertRejected(With(r => r["allowedHosts"] = "https://crm.example.test"), "$.allowedHosts", invalid);
        AssertRejected(With(r => r["allowedHosts"] = new JsonArray(1)), "$.allowedHosts[0]", invalid);
        AssertRejected(With(r => r["steps"] = new JsonObject()), "$.steps", invalid);
        AssertRejected(With(r => r["steps"] = new JsonArray("done")), "$.steps[0]", invalid);
        AssertRejected(With(r => r["secrets"] = new JsonArray(new JsonObject())), "$.secrets[0]", invalid);
        AssertRejected(With(r => r["reconciliation"] = "authoritative"), "$.reconciliation", invalid);
        AssertRejected(With(r => FirstStep(r)["config"] = new JsonArray()), "$.steps[0].config", invalid);
        AssertRejected(With(r => FirstStep(r)["config"] = JsonValue.Create((string?)null)), "$.steps[0].config", invalid);
        AssertRejected(With(r => FirstStep(r)["timeoutMs"] = "1000"), "$.steps[0].timeoutMs", invalid);
        AssertRejected(With(r => FirstStep(r)["sideEffect"] = "true"), "$.steps[0].sideEffect", invalid);
        AssertRejected(With(r => FirstStep(r)["id"] = 1), "$.steps[0].id", invalid);
        AssertRejected(With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = "a", ["type"] = "string", ["required"] = "yes" })), "$.inputs[0].required", invalid);
        AssertRejected(With(r => r["maskSelectors"] = new JsonArray("#tax")), "$.maskSelectors[0]", invalid);
    }

    [Fact]
    public void Unsupported_enum_values_are_rejected_case_sensitively()
    {
        const string invalid = WorkflowErrorCodes.InvalidValue;
        AssertRejected(With(r => FirstStep(r)["type"] = "script"), "$.steps[0].type", invalid);
        AssertRejected(With(r => FirstStep(r)["type"] = "Click"), "$.steps[0].type", invalid);
        AssertRejected(With(r => FirstStep(r)["onError"] = "ignore"), "$.steps[0].onError", invalid);
        AssertRejected(With(r => r["parameters"] = new JsonArray(new JsonObject { ["name"] = "p", ["type"] = "uri" })), "$.parameters[0].type", invalid);
        AssertRejected(With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = "p", ["type"] = "number" })), "$.inputs[0].type", invalid);
        AssertRejected(With(Full, r => r["reconciliation"]!["evidence"] = "strong"), "$.reconciliation.evidence", invalid);
        AssertRejected(With(Full, r => r["reconciliation"]!["matchKey"] = "id"), "$.reconciliation.matchKey", invalid);
        AssertRejected(With(Full, r => FirstStep(r)["idempotency"]!["guarantee"] = "bestEffort"), "$.steps[0].idempotency.guarantee", invalid);
        AssertRejected(With(Full, r => FirstStep(r)["idempotency"]!["key"] = "{{record.email}}"), "$.steps[0].idempotency.key", invalid);
    }

    // ----- Formats -----

    [Theory]
    [InlineData("Bad-Name")]
    [InlineData("bad_name")]
    [InlineData("-abc")]
    [InlineData("1abc")]
    [InlineData("abc def")]
    public void Workflow_names_have_a_strict_format(string name)
    {
        AssertRejected(With(r => r["name"] = name), "$.name", WorkflowErrorCodes.InvalidFormat);
    }

    [Theory]
    [InlineData("Submit")]
    [InlineData("1st")]
    [InlineData("a_b")]
    [InlineData("-a")]
    public void Step_ids_have_a_strict_format(string id)
    {
        AssertRejected(With(r => FirstStep(r)["id"] = id), "$.steps[0].id", WorkflowErrorCodes.InvalidFormat);
    }

    [Theory]
    [InlineData("crm-url")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("a.b")]
    public void Declared_names_have_a_strict_format(string name)
    {
        AssertRejected(With(r => r["secrets"] = new JsonArray(name)), "$.secrets[0]", WorkflowErrorCodes.InvalidFormat);
        AssertRejected(With(r => r["parameters"] = new JsonArray(new JsonObject { ["name"] = name, ["type"] = "url" })), "$.parameters[0].name", WorkflowErrorCodes.InvalidFormat);
        AssertRejected(With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = name, ["type"] = "string" })), "$.inputs[0].name", WorkflowErrorCodes.InvalidFormat);
    }

    [Theory]
    [InlineData("https://crm.example.test ")]
    [InlineData("https://crm .example.test")]
    [InlineData("https://crm.example.test\n")]
    public void Allowed_hosts_cannot_contain_whitespace_or_control_characters(string host)
    {
        AssertRejected(With(r => r["allowedHosts"] = new JsonArray(host)), "$.allowedHosts[0]", WorkflowErrorCodes.InvalidFormat);
    }

    [Fact]
    public void Descriptions_reject_control_characters_other_than_layout_whitespace()
    {
        Valid(With(r => r["description"] = "line one\nline two\ttabbed\r\n"));
        AssertRejected(With(r => r["description"] = "bell\u0007"), "$.description", WorkflowErrorCodes.InvalidFormat);
        AssertRejected(With(r => FirstStep(r)["description"] = "nul\u0000"), "$.steps[0].description", WorkflowErrorCodes.InvalidFormat);
    }

    // ----- Duplicates -----

    [Fact]
    public void Step_ids_are_unique_across_setup_reconcile_and_steps()
    {
        AssertRejected(With(r => r["setup"] = new JsonArray(Step("done"))), "$.steps[0].id", WorkflowErrorCodes.Duplicate);
        AssertRejected(With(Full, r => r["reconcile"]![0]!["id"] = "login"), "$.reconcile[0].id", WorkflowErrorCodes.Duplicate);
        AssertRejected(With(r => r["steps"] = new JsonArray(Step("a"), Step("a"))), "$.steps[1].id", WorkflowErrorCodes.Duplicate);
    }

    [Fact]
    public void Declarations_and_allowed_hosts_are_unique()
    {
        const string duplicate = WorkflowErrorCodes.Duplicate;
        AssertRejected(With(r => r["secrets"] = new JsonArray("pwd", "pwd")), "$.secrets[1]", duplicate);
        AssertRejected(With(r => r["allowedHosts"] = new JsonArray("https://a.example.test", "https://a.example.test")), "$.allowedHosts[1]", duplicate);
        AssertRejected(
            With(r => r["parameters"] = new JsonArray(new JsonObject { ["name"] = "p", ["type"] = "url" }, new JsonObject { ["name"] = "p", ["type"] = "string" })),
            "$.parameters[1].name",
            duplicate);
        AssertRejected(
            With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = "e", ["type"] = "email" }, new JsonObject { ["name"] = "e", ["type"] = "string" })),
            "$.inputs[1].name",
            duplicate);
    }

    // ----- Reconciliation structure -----

    [Fact]
    public void Reconcile_and_reconciliation_require_each_other()
    {
        AssertRejected(With(Full, r => r.Remove("reconciliation")), "$.reconciliation", WorkflowErrorCodes.MissingDependency);
        AssertRejected(With(Full, r => r.Remove("reconcile")), "$.reconcile", WorkflowErrorCodes.MissingDependency);
    }

    [Fact]
    public void Authority_is_required_for_authoritative_and_forbidden_for_advisory_evidence()
    {
        AssertRejected(With(Full, r => r["reconciliation"]!.AsObject().Remove("authority")), "$.reconciliation.authority", RequiredCode);
        AssertRejected(With(Full, r => r["reconciliation"]!["evidence"] = "advisory"), "$.reconciliation.authority", WorkflowErrorCodes.NotAllowed);

        var advisory = Valid(With(Full, r =>
        {
            r["reconciliation"]!["evidence"] = "advisory";
            r["reconciliation"]!.AsObject().Remove("authority");
        })).Definition.Reconciliation!;
        Assert.Equal(EvidenceLevel.Advisory, advisory.Evidence);
        Assert.Null(advisory.Authority);
    }

    [Fact]
    public void Contract_references_cannot_contain_placeholders()
    {
        AssertRejected(
            With(Full, r => Authority(r)["contractReference"] = "See {{params.crmUser}} runbook"),
            "$.reconciliation.authority.contractReference",
            WorkflowErrorCodes.InvalidFormat);
    }

    // ----- Selectors -----

    [Theory]
    [InlineData("{}", "$.maskSelectors[0]")]
    [InlineData("{\"css\":\"a\",\"testId\":\"b\"}", "$.maskSelectors[0]")]
    [InlineData("{\"css\":\"xpath=//a\"}", "$.maskSelectors[0].css")]
    [InlineData("{\"css\":\"internal:role=button\"}", "$.maskSelectors[0].css")]
    [InlineData("{\"css\":\"div >> span\"}", "$.maskSelectors[0].css")]
    [InlineData("{\"css\":\"[data-id={{record.id}}]\"}", "$.maskSelectors[0].css")]
    [InlineData("{\"xpath\":\"a/b\"}", "$.maskSelectors[0].xpath")]
    [InlineData("{\"xpath\":\"//a >> //b\"}", "$.maskSelectors[0].xpath")]
    [InlineData("{\"xpath\":\"//a[@id='{{record.id}}']\"}", "$.maskSelectors[0].xpath")]
    [InlineData("{\"role\":\"button-1\"}", "$.maskSelectors[0].role")]
    [InlineData("{\"text\":\"   \"}", "$.maskSelectors[0].text")]
    [InlineData("{\"label\":\"a\\nb\"}", "$.maskSelectors[0].label")]
    [InlineData("{\"placeholder\":\"a\\u0000b\"}", "$.maskSelectors[0].placeholder")]
    [InlineData("{\"role\":\"button\",\"name\":\"a\\nb\"}", "$.maskSelectors[0].name")]
    public void Selectors_follow_the_Phase3_rules(string selector, string path)
    {
        var json = With(r => r["maskSelectors"] = new JsonArray(JsonNode.Parse(selector)));

        AssertRejected(json, path, WorkflowErrorCodes.InvalidSelector);
    }

    [Fact]
    public void A_name_is_only_allowed_with_a_role_selector()
    {
        AssertRejected(With(r => r["maskSelectors"] = new JsonArray(JsonNode.Parse("{\"css\":\"a\",\"name\":\"b\"}"))), "$.maskSelectors[0].name", WorkflowErrorCodes.NotAllowed);
    }

    [Fact]
    public void Every_selector_kind_binds()
    {
        var selectors = new JsonArray(
            JsonNode.Parse("{\"testId\":\"save-{{record.id}}\"}"),
            JsonNode.Parse("{\"css\":\"form > button.primary[type=submit]\"}"),
            JsonNode.Parse("{\"role\":\"button\"}"),
            JsonNode.Parse("{\"text\":\"Sign in\"}"),
            JsonNode.Parse("{\"label\":\"Email\"}"),
            JsonNode.Parse("{\"placeholder\":\"Search\"}"),
            JsonNode.Parse("{\"xpath\":\"(//table//tr)[1]\"}"));

        var bound = Valid(With(r =>
        {
            r["inputs"] = new JsonArray(new JsonObject { ["name"] = "id", ["type"] = "string" });
            r["maskSelectors"] = selectors;
        })).Definition.MaskSelectors;

        Assert.Equal(Enum.GetValues<WorkflowSelectorKind>(), bound.Select(s => s.Kind));
        Assert.All(bound, s => Assert.Null(s.AccessibleName));
    }

    // ----- Limits: the boundary is accepted, one past it is rejected (§13.1 L2) -----

    [Fact]
    public void Name_length_bounds()
    {
        Valid(With(r => r["name"] = "a" + Repeat('b', WorkflowLimits.MinNameChars - 1)));
        Valid(With(r => r["name"] = "a" + Repeat('b', WorkflowLimits.MaxNameChars - 1)));
        AssertRejected(With(r => r["name"] = "a" + Repeat('b', WorkflowLimits.MinNameChars - 2)), "$.name", WorkflowErrorCodes.TooShort);
        AssertRejected(With(r => r["name"] = "a" + Repeat('b', WorkflowLimits.MaxNameChars)), "$.name", WorkflowErrorCodes.TooLong);
    }

    [Fact]
    public void Description_length_bounds()
    {
        Valid(With(r => r["description"] = Repeat('d', WorkflowLimits.MaxDescriptionChars)));
        Valid(With(r => FirstStep(r)["description"] = Repeat('d', WorkflowLimits.MaxDescriptionChars)));
        AssertRejected(With(r => r["description"] = Repeat('d', WorkflowLimits.MaxDescriptionChars + 1)), "$.description", WorkflowErrorCodes.TooLong);
        AssertRejected(With(r => FirstStep(r)["description"] = Repeat('d', WorkflowLimits.MaxDescriptionChars + 1)), "$.steps[0].description", WorkflowErrorCodes.TooLong);
    }

    [Theory]
    [InlineData(WorkflowLimits.MinTimeoutSeconds, true)]
    [InlineData(WorkflowLimits.MaxTimeoutSeconds, true)]
    [InlineData(WorkflowLimits.MinTimeoutSeconds - 1, false)]
    [InlineData(WorkflowLimits.MaxTimeoutSeconds + 1, false)]
    public void Workflow_timeout_bounds(int seconds, bool valid)
    {
        AssertBound(With(r => r["timeoutSeconds"] = seconds), valid, "$.timeoutSeconds");
    }

    [Theory]
    [InlineData(WorkflowLimits.MinStepTimeoutMs, true)]
    [InlineData(WorkflowLimits.MaxStepTimeoutMs, true)]
    [InlineData(WorkflowLimits.MinStepTimeoutMs - 1, false)]
    [InlineData(WorkflowLimits.MaxStepTimeoutMs + 1, false)]
    public void Step_timeout_bounds(int milliseconds, bool valid)
    {
        AssertBound(With(r => FirstStep(r)["timeoutMs"] = milliseconds), valid, "$.steps[0].timeoutMs");
    }

    [Fact]
    public void A_step_timeout_cannot_exceed_the_workflow_timeout()
    {
        Valid(With(r =>
        {
            r["timeoutSeconds"] = 2;
            FirstStep(r)["timeoutMs"] = 2_000;
        }));

        AssertRejected(
            With(r =>
            {
                r["timeoutSeconds"] = 2;
                FirstStep(r)["timeoutMs"] = 2_001;
            }),
            "$.steps[0].timeoutMs",
            WorkflowErrorCodes.ExceedsWorkflowTimeout);
    }

    [Theory]
    [InlineData(WorkflowLimits.MinInputMaxLength, true)]
    [InlineData(WorkflowLimits.MaxInputMaxLength, true)]
    [InlineData(WorkflowLimits.MinInputMaxLength - 1, false)]
    [InlineData(WorkflowLimits.MaxInputMaxLength + 1, false)]
    public void Input_max_length_bounds(int maxLength, bool valid)
    {
        AssertBound(With(r => r["inputs"] = new JsonArray(new JsonObject { ["name"] = "a", ["type"] = "string", ["maxLength"] = maxLength })), valid, "$.inputs[0].maxLength");
    }

    [Theory]
    [InlineData(WorkflowLimits.MinCommitLatencySeconds, true)]
    [InlineData(WorkflowLimits.MaxCommitLatencySeconds, true)]
    [InlineData(WorkflowLimits.MinCommitLatencySeconds - 1, false)]
    [InlineData(WorkflowLimits.MaxCommitLatencySeconds + 1, false)]
    public void Commit_latency_bounds(int seconds, bool valid)
    {
        AssertBound(With(Full, r => Authority(r)["maxCommitLatencySeconds"] = seconds), valid, "$.reconciliation.authority.maxCommitLatencySeconds");
    }

    [Theory]
    [InlineData(WorkflowLimits.MinEvidenceAgeSeconds, true)]
    [InlineData(WorkflowLimits.MaxEvidenceAgeSeconds, true)]
    [InlineData(WorkflowLimits.MinEvidenceAgeSeconds - 1, false)]
    [InlineData(WorkflowLimits.MaxEvidenceAgeSeconds + 1, false)]
    public void Evidence_age_bounds(int seconds, bool valid)
    {
        AssertBound(With(Full, r => Authority(r)["maxEvidenceAgeSeconds"] = seconds), valid, "$.reconciliation.authority.maxEvidenceAgeSeconds");
    }

    [Fact]
    public void Contract_reference_length_bounds()
    {
        const string path = "$.reconciliation.authority.contractReference";
        Valid(With(Full, r => Authority(r)["contractReference"] = Repeat('c', WorkflowLimits.MinContractReferenceChars)));
        Valid(With(Full, r => Authority(r)["contractReference"] = Repeat('c', WorkflowLimits.MaxContractReferenceChars)));
        AssertRejected(With(Full, r => Authority(r)["contractReference"] = Repeat('c', WorkflowLimits.MinContractReferenceChars - 1)), path, WorkflowErrorCodes.TooShort);
        AssertRejected(With(Full, r => Authority(r)["contractReference"] = Repeat('c', WorkflowLimits.MaxContractReferenceChars + 1)), path, WorkflowErrorCodes.TooLong);
    }

    [Fact]
    public void Step_id_length_bounds()
    {
        Valid(With(r => FirstStep(r)["id"] = "s" + Repeat('1', WorkflowLimits.MaxStepIdChars - 1)));
        AssertRejected(With(r => FirstStep(r)["id"] = "s" + Repeat('1', WorkflowLimits.MaxStepIdChars)), "$.steps[0].id", WorkflowErrorCodes.TooLong);
        AssertRejected(With(r => FirstStep(r)["id"] = ""), "$.steps[0].id", WorkflowErrorCodes.TooShort);
    }

    [Fact]
    public void Allowed_host_count_and_length_bounds()
    {
        const string path = "$.allowedHosts";
        JsonArray Hosts(int count) => [.. Enumerable.Range(0, count).Select(i => (JsonNode)$"https://h{i}.example.test")];

        Valid(With(r => r["allowedHosts"] = Hosts(WorkflowLimits.MaxAllowedHosts)));
        AssertRejected(With(r => r["allowedHosts"] = Hosts(WorkflowLimits.MaxAllowedHosts + 1)), path, WorkflowErrorCodes.TooManyItems);
        AssertRejected(With(r => r["allowedHosts"] = Hosts(0)), path, WorkflowErrorCodes.TooFewItems);

        var prefix = "https://";
        // At the structural limit the entry passes the length check (a 300-char origin cannot be a canonical host, since
        // DNS names are at most 253 chars, so the semantic check then rejects it for a different reason).
        var atLimit = Errors(With(r => r["allowedHosts"] = new JsonArray(prefix + Repeat('h', WorkflowLimits.MaxAllowedHostChars - prefix.Length))));
        Assert.DoesNotContain(atLimit, e => e.Code == WorkflowErrorCodes.TooLong);
        Assert.Contains(atLimit, e => e.Path == path + "[0]" && e.Code == "allowed-host-not-canonical");
        AssertRejected(With(r => r["allowedHosts"] = new JsonArray(prefix + Repeat('h', WorkflowLimits.MaxAllowedHostChars - prefix.Length + 1))), path + "[0]", WorkflowErrorCodes.TooLong);
        AssertRejected(With(r => r["allowedHosts"] = new JsonArray("")), path + "[0]", WorkflowErrorCodes.TooShort);
    }

    [Fact]
    public void Declaration_count_bounds()
    {
        JsonArray Names(int count, Func<int, JsonNode> item) => [.. Enumerable.Range(0, count).Select(item)];
        JsonNode Parameter(int i) => new JsonObject { ["name"] = "p" + i, ["type"] = "string" };
        JsonNode Input(int i) => new JsonObject { ["name"] = "i" + i, ["type"] = "string" };
        JsonNode Secret(int i) => "s" + i;
        JsonNode Mask(int i) => new JsonObject { ["testId"] = "m" + i };

        foreach (var (property, max, item) in new (string, int, Func<int, JsonNode>)[]
        {
            ("parameters", WorkflowLimits.MaxParameters, Parameter),
            ("inputs", WorkflowLimits.MaxInputs, Input),
            ("secrets", WorkflowLimits.MaxSecrets, Secret),
            ("maskSelectors", WorkflowLimits.MaxMaskSelectors, Mask),
        })
        {
            Valid(With(r => r[property] = Names(max, item)));
            AssertRejected(With(r => r[property] = Names(max + 1, item)), "$." + property, WorkflowErrorCodes.TooManyItems);
        }
    }

    [Fact]
    public void Declared_name_length_bounds()
    {
        Valid(With(r => r["secrets"] = new JsonArray("s" + Repeat('x', WorkflowLimits.MaxDeclaredNameChars - 1))));
        AssertRejected(With(r => r["secrets"] = new JsonArray("s" + Repeat('x', WorkflowLimits.MaxDeclaredNameChars))), "$.secrets[0]", WorkflowErrorCodes.TooLong);
    }

    [Fact]
    public void Selector_length_bounds()
    {
        Valid(With(r => r["maskSelectors"] = new JsonArray(new JsonObject { ["text"] = Repeat('t', WorkflowLimits.MaxSelectorChars) })));
        AssertRejected(
            With(r => r["maskSelectors"] = new JsonArray(new JsonObject { ["text"] = Repeat('t', WorkflowLimits.MaxSelectorChars + 1) })),
            "$.maskSelectors[0].text",
            WorkflowErrorCodes.TooLong);
    }

    [Fact]
    public void The_total_step_count_across_sections_is_limited()
    {
        Valid(With(r =>
        {
            r["setup"] = Delays(50, "a");
            r["steps"] = StepsThenDone(WorkflowLimits.MaxSteps - 50 - 1, "b"); // 50 + 149 + 1 terminal = 200
        }));

        AssertRejected(
            With(r =>
            {
                r["setup"] = Delays(50, "a");
                r["steps"] = StepsThenDone(WorkflowLimits.MaxSteps - 50, "b"); // 201
            }),
            "$",
            WorkflowErrorCodes.TooManySteps);
    }

    [Fact]
    public void All_independent_errors_are_reported_together()
    {
        var errors = Errors(With(r =>
        {
            r["name"] = "Bad";
            r["timeoutSeconds"] = 0;
            r["extra"] = true;
            FirstStep(r)["type"] = "script";
        }));

        Assert.Contains(errors, e => e.Path == "$.name" && e.Code == WorkflowErrorCodes.InvalidFormat);
        Assert.Contains(errors, e => e.Path == "$.timeoutSeconds" && e.Code == WorkflowErrorCodes.OutOfRange);
        Assert.Contains(errors, e => e.Path == "$.extra" && e.Code == WorkflowErrorCodes.UnknownProperty);
        Assert.Contains(errors, e => e.Path == "$.steps[0].type" && e.Code == WorkflowErrorCodes.InvalidValue);
    }

    private static JsonObject Authority(JsonObject root) => root["reconciliation"]!["authority"]!.AsObject();

    private static void AssertBound(string json, bool valid, string path)
    {
        if (valid)
        {
            Valid(json);
        }
        else
        {
            AssertRejected(json, path, WorkflowErrorCodes.OutOfRange);
        }
    }
}
