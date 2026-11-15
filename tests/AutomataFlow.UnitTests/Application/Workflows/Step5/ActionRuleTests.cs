using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using AutomataFlow.Application.Workflows.Urls;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step5;

/// <summary>Phase 4 step 5: per-action configs, placement, side effects, finalization, dominance and reconciliation (§13.1).</summary>
public class ActionRuleTests
{
    private const string Base = """
        {"schemaVersion":1,"name":"rule-flow","allowedHosts":["https://crm.example.test","https://*.cdn.example.test"],
         "parameters":[{"name":"crmBaseUrl","type":"url"},{"name":"region","type":"string"}],
         "inputs":[{"name":"email","type":"email","required":true},{"name":"nickname","type":"string"},{"name":"taxId","type":"string","sensitive":true},{"name":"contract","type":"file"}],
         "secrets":["crmPassword"],"maskSelectors":[{"testId":"tax"}],
         "steps":[]}
        """;

    private static JsonObject S(string id, string type, JsonObject config, Action<JsonObject>? extra = null)
    {
        var step = new JsonObject { ["id"] = id, ["type"] = type, ["config"] = config };
        extra?.Invoke(step);
        return step;
    }

    private static JsonObject Sel(string testId) => new() { ["testId"] = testId };

    private static JsonObject Cfg(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The given steps followed by a terminal captureResult, in <c>steps</c>.</summary>
    private static string InSteps(params JsonObject[] steps) => With(Base, r => r["steps"] = new JsonArray([.. steps, Terminal()]));

    private static string InSetup(params JsonObject[] steps) => With(Base, r =>
    {
        r["setup"] = new JsonArray([.. steps]);
        r["steps"] = new JsonArray(Terminal());
    });

    // ----- Per-action configuration (strict, typed, bounded) -----

    public static TheoryData<string, string, string, string> InvalidConfigs() => new()
    {
        // type, config, path suffix, code
        { "navigate", "{}", ".url", WorkflowErrorCodes.Required },
        { "navigate", "{\"url\":\"https://crm.example.test/a\",\"acceptStatus\":[399]}", ".acceptStatus[0]", WorkflowErrorCodes.OutOfRange },
        { "navigate", "{\"url\":\"https://crm.example.test/a\",\"acceptStatus\":[600]}", ".acceptStatus[0]", WorkflowErrorCodes.OutOfRange },
        { "navigate", "{\"url\":\"https://crm.example.test/a\",\"acceptStatus\":[404,404]}", ".acceptStatus[1]", WorkflowErrorCodes.Duplicate },
        { "navigate", "{\"url\":\"https://crm.example.test/a\",\"acceptStatus\":[400,401,402,403,404,405,406,407,408,409,410]}", ".acceptStatus", WorkflowErrorCodes.TooManyItems },
        { "navigate", "{\"url\":\"https://crm.example.test/a#x\"}", ".url", UrlErrorCodes.Fragment },
        { "navigate", "{\"url\":\"https://crm.example.test/a\",\"method\":\"POST\"}", ".method", WorkflowErrorCodes.UnknownProperty },
        { "click", "{}", ".selector", WorkflowErrorCodes.Required },
        { "click", "{\"selector\":{\"testId\":\"a\"},\"waitForNavigation\":\"yes\"}", ".waitForNavigation", WorkflowErrorCodes.InvalidType },
        { "fill", "{\"selector\":{\"testId\":\"a\"}}", ".value", WorkflowErrorCodes.Required },
        { "fill", "{\"selector\":{\"testId\":\"a\"},\"value\":\"{{record.email\"}", ".value", TemplateErrorCodes.Unterminated },
        { "select", "{\"selector\":{\"testId\":\"a\"}}", "", "select-choice" },
        { "select", "{\"selector\":{\"testId\":\"a\"},\"value\":\"x\",\"index\":1}", "", "select-choice" },
        { "select", "{\"selector\":{\"testId\":\"a\"},\"index\":1001}", ".index", WorkflowErrorCodes.OutOfRange },
        { "wait", "{}", "", "wait-kind" },
        { "wait", "{\"for\":\"load\",\"url\":\"**/x\"}", "", "wait-kind" },
        { "wait", "{\"for\":\"load\",\"selector\":{\"testId\":\"a\"}}", ".selector", WorkflowErrorCodes.NotAllowed },
        { "wait", "{\"for\":\"visible\"}", ".selector", WorkflowErrorCodes.Required },
        { "wait", "{\"for\":\"enabled\",\"selector\":{\"testId\":\"a\"}}", ".for", WorkflowErrorCodes.InvalidValue },
        { "wait", "{\"url\":\"**/a?b\"}", ".url", "invalid-glob" },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"attribute\"}", ".attribute", WorkflowErrorCodes.Required },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"text\",\"attribute\":\"id\"}", ".attribute", WorkflowErrorCodes.NotAllowed },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"attribute\",\"attribute\":\"on click\"}", ".attribute", WorkflowErrorCodes.InvalidFormat },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"text\",\"maxLength\":0}", ".maxLength", WorkflowErrorCodes.OutOfRange },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"text\",\"maxLength\":10001}", ".maxLength", WorkflowErrorCodes.OutOfRange },
        { "extract", "{\"selector\":{\"testId\":\"a\"},\"source\":\"html\"}", ".source", WorkflowErrorCodes.InvalidValue },
        { "screenshot", "{}", ".label", WorkflowErrorCodes.Required },
        { "screenshot", "{\"label\":\"" + new string('l', 101) + "\"}", ".label", WorkflowErrorCodes.TooLong },
        { "download", "{}", ".trigger", WorkflowErrorCodes.Required },
        { "delay", "{\"milliseconds\":0}", ".milliseconds", WorkflowErrorCodes.OutOfRange },
        { "delay", "{\"milliseconds\":30001}", ".milliseconds", WorkflowErrorCodes.OutOfRange },
        { "captureResult", "{}", "", "capture-empty" },
        { "captureResult", "{\"fields\":{\"1bad\":\"x\"},\"outcome\":{\"status\":\"succeeded\"}}", ".fields['1bad']", WorkflowErrorCodes.InvalidFormat },
        { "captureResult", "{\"fields\":{\"a\":1},\"outcome\":{\"status\":\"succeeded\"}}", ".fields.a", WorkflowErrorCodes.InvalidType },
        { "captureResult", "{\"outcome\":{\"status\":\"succeeded\",\"code\":\"Ok\"}}", ".outcome.code", WorkflowErrorCodes.NotAllowed },
        { "captureResult", "{\"outcome\":{\"status\":\"failed\"}}", ".outcome.code", WorkflowErrorCodes.Required },
        { "captureResult", "{\"outcome\":{\"status\":\"failed\",\"code\":\"lowercase\"}}", ".outcome.code", WorkflowErrorCodes.InvalidFormat },
        { "captureResult", "{\"outcome\":{\"status\":\"failed\",\"code\":\"Dup\",\"transient\":true,\"transientWhen\":{\"isEmpty\":\"x\"}}}", ".outcome.transientWhen", WorkflowErrorCodes.NotAllowed },
        { "captureResult", "{\"outcome\":{\"status\":\"maybe\"}}", ".outcome.status", WorkflowErrorCodes.InvalidValue },
        { "condition", "{\"if\":{\"isEmpty\":\"x\"},\"then\":[]}", ".then", WorkflowErrorCodes.TooFewItems },
        { "condition", "{\"if\":{\"isEmpty\":\"x\"}}", ".then", WorkflowErrorCodes.Required },
        { "condition", "{\"then\":[{\"id\":\"t\",\"type\":\"delay\",\"config\":{\"milliseconds\":1}}]}", ".if", WorkflowErrorCodes.Required },
        { "condition", "{\"if\":{\"isEmpty\":\"x\"},\"then\":[{\"id\":\"t\",\"type\":\"delay\",\"config\":{\"milliseconds\":1}}],\"else\":{}}", ".else", WorkflowErrorCodes.InvalidType },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void Invalid_action_configs_are_rejected_with_their_path(string type, string config, string pathSuffix, string code)
    {
        var step = S("x", type, Cfg(config), s =>
        {
            if (type is "click" or "upload")
            {
                s["sideEffect"] = false;
            }
        });

        AssertRejected(InSteps(step), "$.steps[0].config" + pathSuffix, code);
    }

    public static TheoryData<string> InvalidUploads() => new()
    {
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\"}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[]}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".PDF\"]}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\"pdf\"]}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".exe\"]}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".pdf\",\".pdf\"]}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".pdf\"],\"maxBytes\":0}",
        "{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.nickname}}\",\"allowedExtensions\":[\".pdf\"]}",
    };

    [Theory]
    [MemberData(nameof(InvalidUploads))]
    public void Upload_configs_are_strict(string config)
    {
        Assert.NotEmpty(Errors(InSteps(S("up", "upload", Cfg(config), s => s["sideEffect"] = false))));
    }

    [Fact]
    public void Upload_extensions_are_bounded_and_executables_are_never_allowed()
    {
        var tooMany = new JsonArray([.. Enumerable.Range(0, WorkflowLimits.MaxUploadExtensions + 1).Select(i => (JsonNode)$".e{i}")]);
        var config = Cfg("{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\"}");
        config["allowedExtensions"] = tooMany;
        AssertRejected(InSteps(S("up", "upload", config, s => s["sideEffect"] = false)), "$.steps[0].config.allowedExtensions", WorkflowErrorCodes.TooManyItems);

        var exe = Cfg("{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".ps1\"]}");
        AssertRejected(InSteps(S("up", "upload", exe, s => s["sideEffect"] = false)), "$.steps[0].config.allowedExtensions[0]", "upload-extension-forbidden");
    }

    // ----- Placement -----

    [Theory]
    [InlineData("upload", "setup")]
    [InlineData("download", "reconcile")]
    [InlineData("captureResult", "reconcile")]
    [InlineData("reconcileMatch", "steps")]
    [InlineData("reconcileMatch", "setup")]
    public void Actions_are_restricted_to_their_sections(string type, string section)
    {
        var step = type switch
        {
            "upload" => S("x", type, Cfg("{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".pdf\"]}")),
            "reconcileMatch" => S("x", type, Cfg(ReconcileMatchConfig("{{operation.id}}"))),
            _ => Step("x", type),
        };

        var json = section switch
        {
            "setup" => InSetup(step),
            "steps" => InSteps(step),
            _ => With(Full, r => r["reconcile"] = new JsonArray(step, S("find", "reconcileMatch", Cfg(ReconcileMatchConfig("{{operation.id}}"))))),
        };

        Assert.Contains(Errors(json), e => e.Code == "step-section");
    }

    private static string ReconcileMatchConfig(string equals) =>
        "{\"rows\":{\"css\":\"tr\"},\"key\":{\"attribute\":\"data-id\"},\"equals\":\"" + equals + "\",\"emptyIndicator\":{\"testId\":\"none\"}}";

    [Fact]
    public void ReconcileMatch_must_be_the_single_last_top_level_reconcile_step()
    {
        JsonObject Match() => S("find", "reconcileMatch", Cfg(ReconcileMatchConfig("{{operation.id}}")));

        AssertRejected(With(Full, r => r["reconcile"] = new JsonArray()), "$.reconcile", "reconcile-match-required");
        AssertRejected(With(Full, r => r["reconcile"] = new JsonArray(Step("nav", "navigate"))), "$.reconcile", "reconcile-match-required");
        AssertRejected(With(Full, r => r["reconcile"] = new JsonArray(S("early", "reconcileMatch", Cfg(ReconcileMatchConfig("{{operation.id}}"))), Match())),
            "$.reconcile[0].type", "reconcile-match-position");

        var branched = S("check", "condition", new JsonObject
        {
            ["if"] = Cfg("{\"isEmpty\":\"x\"}"),
            ["then"] = new JsonArray(S("inner", "reconcileMatch", Cfg(ReconcileMatchConfig("{{operation.id}}")))),
        });
        AssertRejected(With(Full, r => r["reconcile"] = new JsonArray(branched, Match())), "$.reconcile[0].config.then[0].type", "reconcile-match-position");
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("click")]
    [InlineData("download")]
    [InlineData("delay")]
    public void Continue_is_rejected_on_actions_whose_work_can_outlive_a_timeout(string type)
    {
        var step = Step("x", type);
        step["onError"] = "continue";
        if (type == "click")
        {
            step["sideEffect"] = false;
        }

        AssertRejected(InSteps(step), "$.steps[0].onError", "on-error-continue-not-allowed");
    }

    [Fact]
    public void Continue_is_rejected_on_terminal_and_reconcile_steps()
    {
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(Terminal().Also(s => s["onError"] = "continue"))), "$.steps[0].onError", "on-error-continue-not-allowed");
        AssertRejected(With(Full, r => r["reconcile"]![0]!["onError"] = "continue"), "$.reconcile[0].onError", "on-error-continue-not-allowed");
        var upload = S("up", "upload", Cfg("{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".pdf\"]}"), s =>
        {
            s["sideEffect"] = false;
            s["onError"] = "continue";
        });
        AssertRejected(InSteps(upload), "$.steps[0].onError", "on-error-continue-not-allowed");
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("select")]
    [InlineData("wait")]
    [InlineData("extract")]
    [InlineData("screenshot")]
    public void Continue_is_allowed_on_reading_and_typing_actions(string type)
    {
        Valid(InSteps(Step("x", type).Also(s => s["onError"] = "continue")));
    }

    // ----- Side effects (§10.1, R4) -----

    [Fact]
    public void SideEffect_must_be_declared_on_every_click_and_upload_in_steps_including_branches()
    {
        AssertRejected(InSteps(Step("go", "click")), "$.steps[0].sideEffect", "side-effect-required");

        var branched = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(Step("go", "click")) });
        AssertRejected(InSteps(branched), "$.steps[0].config.then[0].sideEffect", "side-effect-required");

        Valid(InSteps(Step("go", "click").Also(s => s["sideEffect"] = false)));
        Valid(InSetup(Step("go", "click"))); // absent is fine outside steps
    }

    [Fact]
    public void Side_effects_are_forbidden_outside_steps_and_on_other_actions()
    {
        AssertRejected(InSetup(Step("go", "click").Also(s => s["sideEffect"] = true)), "$.setup[0].sideEffect", "side-effect-not-allowed");
        Valid(InSetup(Step("go", "click").Also(s => s["sideEffect"] = false)));
        AssertRejected(InSteps(Step("f", "fill").Also(s => s["sideEffect"] = false)), "$.steps[0].sideEffect", "side-effect-forbidden");
        AssertRejected(InSteps(Step("n", "navigate").Also(s => s["sideEffect"] = true)), "$.steps[0].sideEffect", "side-effect-forbidden");
    }

    [Fact]
    public void Side_effect_steps_need_at_least_five_seconds()
    {
        AssertRejected(InSteps(Step("go", "click").Also(s =>
        {
            s["sideEffect"] = true;
            s["timeoutMs"] = WorkflowLimits.MinSideEffectStepTimeoutMs - 1;
        })), "$.steps[0].timeoutMs", "side-effect-timeout");

        Valid(InSteps(Step("go", "click").Also(s =>
        {
            s["sideEffect"] = true;
            s["timeoutMs"] = WorkflowLimits.MinSideEffectStepTimeoutMs;
        })));
        Valid(InSteps(Step("go", "click").Also(s => s["sideEffect"] = true))); // default 15 s
    }

    [Fact]
    public void A_navigating_click_declared_without_side_effect_produces_a_warning()
    {
        var workflow = Valid(InSteps(S("go", "click", Cfg("{\"selector\":{\"testId\":\"save\"},\"waitForNavigation\":true}"), s => s["sideEffect"] = false)));

        Assert.Contains(workflow.Warnings, w => w.Code == "navigating-click-without-side-effect" && w.Path == "$.steps[0].sideEffect");
    }

    // ----- Idempotency attestation -----

    private static readonly JsonObject Attestation = new() { ["key"] = "{{operation.id}}", ["guarantee"] = "targetEnforced" };

    private static JsonObject Submit(string id = "submit", bool attest = true) => Step(id, "click").Also(s =>
    {
        s["sideEffect"] = true;
        if (attest)
        {
            s["idempotency"] = Attestation.DeepClone();
        }
    });

    private static JsonObject FillOperationId(string id = "op", string value = "{{operation.id}}") => S(id, "fill", new JsonObject { ["selector"] = Sel("operation-id"), ["value"] = value });

    [Fact]
    public void Idempotency_requires_a_dominating_fill_of_exactly_the_operation_id()
    {
        Valid(InSteps(FillOperationId(), Submit()));
        Valid(InSteps(S("op", "select", new JsonObject { ["selector"] = Sel("op"), ["value"] = "{{operation.id}}" }), Submit()));
        AssertRejected(InSteps(Submit()), "$.steps[0].idempotency", "idempotency-key-not-sent");
        AssertRejected(InSteps(FillOperationId(value: "op-{{operation.id}}"), Submit()), "$.steps[1].idempotency", "idempotency-key-not-sent");
        AssertRejected(InSteps(Submit(), FillOperationId()), "$.steps[0].idempotency", "idempotency-key-not-sent");
        AssertRejected(InSteps(FillOperationId().Also(s => s["onError"] = "continue"), Submit()), "$.steps[1].idempotency", "idempotency-key-not-sent");
    }

    [Fact]
    public void A_fill_inside_a_branch_does_not_dominate_steps_after_the_condition()
    {
        var branch = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(FillOperationId()) });

        AssertRejected(InSteps(branch, Submit()), "$.steps[1].idempotency", "idempotency-key-not-sent");

        var inside = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(FillOperationId(), Submit()) });
        Valid(InSteps(inside));
    }

    [Fact]
    public void Idempotency_is_only_allowed_on_side_effect_steps()
    {
        AssertRejected(InSteps(FillOperationId(), Step("go", "click").Also(s =>
        {
            s["sideEffect"] = false;
            s["idempotency"] = Attestation.DeepClone();
        })), "$.steps[1].idempotency", "idempotency-not-allowed");
    }

    [Fact]
    public void TargetEnforced_requires_every_side_effect_step_to_attest()
    {
        var partial = Valid(InSteps(FillOperationId(), Submit("first"), Submit("second", attest: false))).Safety;
        Assert.False(partial.TargetEnforced);
        Assert.True(partial.OperationIdSent);

        var all = Valid(InSteps(FillOperationId(), Submit("first"), Submit("second"))).Safety;
        Assert.True(all.TargetEnforced);
    }

    [Fact]
    public void OperationIdSent_requires_every_side_effect_step_to_be_dominated()
    {
        var safety = Valid(InSteps(Submit("first", attest: false), FillOperationId(), Submit("second", attest: false))).Safety;

        Assert.True(safety.HasSideEffects);
        Assert.False(safety.OperationIdSent);
    }

    // ----- Reconciliation keys (§10.3) -----

    [Fact]
    public void An_operation_id_key_must_be_exactly_the_operation_id_and_be_sent()
    {
        AssertRejected(With(Full, r => r["reconcile"]![0]!["config"]!["equals"] = "{{record.email}}"), "$.reconcile[0].config.equals", "reconcile-key-mismatch");
        AssertRejected(With(Full, r => ((JsonArray)r["setup"]!).RemoveAt(1)), "$.reconcile[0].config.equals", "reconcile-key-not-sent");
    }

    [Theory]
    [InlineData("{{record.company}}", "reconcile-business-key")]
    [InlineData("{{record.taxId}}", "reconcile-business-key")]
    [InlineData("literal@example.test", "reconcile-business-key")]
    [InlineData("{{record.email}}-x", "reconcile-business-key")]
    [InlineData("{{operation.id}}", "reconcile-business-key")]
    public void A_business_key_must_be_exactly_one_required_non_sensitive_input(string equals, string code)
    {
        var json = With(Full, r =>
        {
            r["reconciliation"]!["matchKey"] = "businessKey";
            r["reconcile"]![0]!["config"]!["equals"] = equals;
        });

        AssertRejected(json, "$.reconcile[0].config.equals", code);
    }

    [Fact]
    public void A_valid_business_key_is_accepted_and_sensitive_keys_also_violate_the_sink()
    {
        Valid(With(Full, r =>
        {
            r["reconciliation"]!["matchKey"] = "businessKey";
            r["reconcile"]![0]!["config"]!["equals"] = "{{record.email}}";
        }));

        AssertRejected(With(Full, r => r["reconcile"]![0]!["config"]!["equals"] = "{{record.taxId}}"), "$.reconcile[0].config.equals", TemplateErrorCodes.SensitiveNotAllowed);
    }

    // ----- Explicit finalization and terminal outcomes (§16 D3) -----

    [Fact]
    public void Every_path_through_steps_must_end_in_a_terminal_capture()
    {
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(Step("only"))), "$.steps[0]", "missing-finalization");
        AssertRejected(With(Base, r => r["steps"] = new JsonArray()), "$.steps", "missing-finalization");

        var halfTerminated = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(Terminal("ok")) });
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(halfTerminated)), "$.steps[0].config.else", "missing-finalization");

        var missingInThen = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(Step("t")), ["else"] = new JsonArray(Terminal("ko", "failed").Also(t => t["config"]!["outcome"]!["code"] = "Rejected")) });
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(missingInThen)), "$.steps[0].config.then[0]", "missing-finalization");

        var both = S("check", "condition", new JsonObject
        {
            ["if"] = Cfg("{\"isEmpty\":\"x\"}"),
            ["then"] = new JsonArray(Terminal("ok")),
            ["else"] = new JsonArray(Terminal("ko", "failed").Also(t => t["config"]!["outcome"]!["code"] = "Rejected")),
        });
        Valid(With(Base, r => r["steps"] = new JsonArray(both)));
        Valid(InSteps(halfTerminated.DeepClone().AsObject()));
    }

    [Fact]
    public void A_terminal_step_must_be_last_in_its_sequence()
    {
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(Terminal("first"), Terminal("second"))), "$.steps[1]", "unreachable-step");

        var both = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"x\"}"), ["then"] = new JsonArray(Terminal("ok")), ["else"] = new JsonArray(Terminal("ko")) });
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(both, Terminal("after"))), "$.steps[1]", "unreachable-step");

        AssertRejected(InSetup(Terminal("early", "failed").Also(t => t["config"]!["outcome"]!["code"] = "LoginFailed"), Step("later")), "$.setup[1]", "unreachable-step");
    }

    [Fact]
    public void Setup_may_only_end_an_attempt_as_failed()
    {
        AssertRejected(InSetup(Terminal("early")), "$.setup[0].config.outcome.status", "setup-outcome");
        Valid(InSetup(Terminal("early", "failed").Also(t => t["config"]!["outcome"]!["code"] = "LoginFailed")));
        Valid(InSetup(S("note", "captureResult", Cfg("{\"fields\":{\"loginPage\":\"seen\"}}"))));
    }

    [Fact]
    public void Result_fields_are_written_at_most_once_per_path()
    {
        AssertRejected(InSteps(S("a", "captureResult", Cfg("{\"fields\":{\"f\":\"x\"}}")), S("b", "captureResult", Cfg("{\"fields\":{\"f\":\"y\"}}"))),
            "$.steps[1].config.fields.f", "result-overwrite");

        var perBranch = S("check", "condition", new JsonObject
        {
            ["if"] = Cfg("{\"isEmpty\":\"x\"}"),
            ["then"] = new JsonArray(S("t", "captureResult", Cfg("{\"fields\":{\"f\":\"x\"}}"))),
            ["else"] = new JsonArray(S("e", "captureResult", Cfg("{\"fields\":{\"f\":\"y\"}}"))),
        });
        Valid(InSteps(perBranch));
        AssertRejected(InSteps(perBranch.DeepClone().AsObject(), S("after", "captureResult", Cfg("{\"fields\":{\"f\":\"z\"}}"))), "$.steps[1].config.fields.f", "result-overwrite");

        AssertRejected(With(Base, r => r["steps"] = new JsonArray(S("a", "captureResult", Cfg("{\"fields\":{\"f\":\"x\"}}")),
            S("done", "captureResult", Cfg("{\"fields\":{\"f\":\"y\"},\"outcome\":{\"status\":\"succeeded\"}}")))), "$.steps[1].config.fields.f", "result-overwrite");
    }

    [Fact]
    public void Setup_fields_count_on_the_steps_and_reconcile_paths()
    {
        var json = With(Full, r => ((JsonArray)r["setup"]!).Add(S("note", "captureResult", Cfg("{\"fields\":{\"customerId\":\"pre\"}}"))));

        AssertRejected(json, "$.steps[2].config.fields.customerId", "result-overwrite");
    }

    // ----- Dominance and sinks across sections and branches (§4, §5.1) -----

    private static JsonObject Extract(string id, bool sensitive = false) =>
        S(id, "extract", Cfg("{\"selector\":{\"testId\":\"v\"},\"source\":\"text\",\"sensitive\":" + (sensitive ? "true" : "false") + "}"));

    private static JsonObject FillWith(string id, string value) => S(id, "fill", new JsonObject { ["selector"] = Sel("f"), ["value"] = value });

    [Fact]
    public void Step_references_must_dominate_their_use()
    {
        Valid(InSteps(Extract("read"), FillWith("use", "{{steps.read.value}}")));
        AssertRejected(InSteps(FillWith("use", "{{steps.read.value}}"), Extract("read")), "$.steps[0].config.value", TemplateErrorCodes.StepNotDominating);

        var branch = S("check", "condition", new JsonObject
        {
            ["if"] = Cfg("{\"isEmpty\":\"x\"}"),
            ["then"] = new JsonArray(Extract("inner"), FillWith("same-branch", "{{steps.inner.value}}")),
            ["else"] = new JsonArray(FillWith("sibling", "{{steps.inner.value}}")),
        });
        AssertRejected(InSteps(branch), "$.steps[0].config.else[0].config.value", TemplateErrorCodes.StepNotDominating);
        AssertRejected(InSteps(branch.DeepClone().AsObject().Also(b => ((JsonArray)b["config"]!["else"]!).Clear()), FillWith("after", "{{steps.inner.value}}")),
            "$.steps[1].config.value", TemplateErrorCodes.StepNotDominating);
        Valid(InSteps(branch.DeepClone().AsObject().Also(b => ((JsonArray)b["config"]!["else"]!).Clear()), FillWith("after", "{{steps.check.result}}")));
    }

    [Fact]
    public void Setup_outputs_are_visible_to_steps_and_reconcile_but_steps_outputs_are_not_visible_to_reconcile()
    {
        Valid(With(Full, r =>
        {
            ((JsonArray)r["setup"]!).Add(Extract("token"));
            r["steps"]![1]!["config"] = Cfg("{\"selector\":{\"testId\":\"{{steps.token.value}}\"},\"source\":\"text\"}");
        }));

        AssertRejected(With(Full, r => r["reconcile"]![0]!["config"]!["rows"] = new JsonObject { ["testId"] = "{{steps.read-id.value}}" }),
            "$.reconcile[0].config.rows.testId", TemplateErrorCodes.StepNotDominating);
    }

    [Fact]
    public void Undeclared_outputs_are_rejected_and_every_step_exposes_failed()
    {
        AssertRejected(InSteps(Step("nav", "navigate"), FillWith("use", "{{steps.nav.value}}")), "$.steps[1].config.value", TemplateErrorCodes.UndeclaredOutput);
        Valid(InSteps(Step("nav", "navigate"), FillWith("use", "{{steps.nav.status}}-{{steps.nav.failed}}")));
    }

    [Fact]
    public void Sink_rules_apply_to_every_action_field()
    {
        Valid(InSteps(FillWith("pw", "{{secret.crmPassword}}")));
        AssertRejected(InSteps(S("s", "select", new JsonObject { ["selector"] = Sel("s"), ["value"] = "{{secret.crmPassword}}" })), "$.steps[0].config.value", TemplateErrorCodes.SecretNotAllowed);
        Valid(InSteps(S("s", "select", new JsonObject { ["selector"] = Sel("s"), ["value"] = "{{record.taxId}}" })));
        AssertRejected(InSteps(Extract("secret-read", sensitive: true), S("c", "captureResult", Cfg("{\"fields\":{\"leak\":\"{{steps.secret-read.value}}\"}}"))),
            "$.steps[1].config.fields.leak", TemplateErrorCodes.SensitiveNotAllowed);
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://crm.example.test/c/{{record.taxId}}\"}"))), "$.steps[0].config.url", TemplateErrorCodes.SensitiveNotAllowed);
        AssertRejected(InSteps(S("shot", "screenshot", Cfg("{\"label\":\"{{record.taxId}}\"}"))), "$.steps[0].config.label", TemplateErrorCodes.SensitiveNotAllowed);
        AssertRejected(InSteps(S("w", "wait", Cfg("{\"for\":\"visible\",\"selector\":{\"text\":\"{{secret.crmPassword}}\"}}"))), "$.steps[0].config.selector.text", TemplateErrorCodes.SecretNotAllowed);
        var condition = S("check", "condition", new JsonObject
        {
            ["if"] = Cfg("{\"valueEquals\":{\"left\":\"{{record.taxId}}\",\"right\":\"{{secret.crmPassword}}\"}}"),
            ["then"] = new JsonArray(Step("t")),
        });
        AssertRejected(InSteps(condition), "$.steps[0].config.if", TemplateErrorCodes.SecretNotAllowed);
    }

    [Fact]
    public void Navigate_origins_must_be_allowed_hosts()
    {
        Valid(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://crm.example.test/a\"}"))));
        Valid(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://eu.cdn.example.test/a\"}"))));
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://other.example.test/a\"}"))), "$.steps[0].config.url", UrlErrorCodes.OriginNotAllowed);
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"{{params.crmBaseUrl}}/a\"}"))), "$.steps[0].config.url", UrlErrorCodes.OriginNotAllowed);
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"{{params.region}}/a\"}"))), "$.steps[0].config.url", TemplateErrorCodes.ParameterMisuse);
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"{{params.nope}}/a\"}"))), "$.steps[0].config.url", TemplateErrorCodes.UndeclaredParameter);
        AssertRejected(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://{{record.nickname}}.example.test/a\"}"))), "$.steps[0].config.url", UrlErrorCodes.OriginPlaceholder);
        Valid(InSteps(S("n", "navigate", Cfg("{\"url\":\"https://crm.example.test/c/{{record.nickname}}?r={{params.region}}\"}"))));
    }

    // ----- Conditions (§8.1) -----

    private static string ConditionWith(string expression) =>
        InSteps(S("check", "condition", new JsonObject { ["if"] = JsonNode.Parse(expression), ["then"] = new JsonArray(Step("t")) }));

    [Theory]
    [InlineData("{\"all\":[]}", "$.steps[0].config.if.all", "condition-children")]
    [InlineData("{\"any\":[{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"},{\"isEmpty\":\"a\"}]}", "$.steps[0].config.if.any", "condition-children")]
    [InlineData("{\"not\":[{\"isEmpty\":\"a\"}]}", "$.steps[0].config.if.not", "condition-children")]
    [InlineData("{\"isEmpty\":\"a\",\"urlMatches\":\"**/x\"}", "$.steps[0].config.if", "condition-shape")]
    [InlineData("{}", "$.steps[0].config.if", "condition-shape")]
    [InlineData("{\"matches\":\"a.*\"}", "$.steps[0].config.if.matches", WorkflowErrorCodes.ForbiddenConstruct)]
    [InlineData("{\"regex\":\"a.*\"}", "$.steps[0].config.if.regex", WorkflowErrorCodes.ForbiddenConstruct)]
    [InlineData("{\"script\":\"true\"}", "$.steps[0].config.if.script", WorkflowErrorCodes.UnknownProperty)]
    [InlineData("{\"urlMatches\":\"**/a[0]\"}", "$.steps[0].config.if.urlMatches", "invalid-glob")]
    [InlineData("{\"elementCountAtLeast\":{\"selector\":{\"testId\":\"r\"},\"count\":0}}", "$.steps[0].config.if.elementCountAtLeast.count", WorkflowErrorCodes.OutOfRange)]
    [InlineData("{\"attributeEquals\":{\"selector\":{\"testId\":\"r\"},\"value\":\"x\"}}", "$.steps[0].config.if.attributeEquals.attribute", WorkflowErrorCodes.Required)]
    [InlineData("{\"valueEquals\":{\"left\":\"a\"}}", "$.steps[0].config.if.valueEquals.right", WorkflowErrorCodes.Required)]
    public void Malformed_conditions_are_rejected(string expression, string path, string code)
    {
        AssertRejected(ConditionWith(expression), path, code);
    }

    [Fact]
    public void Condition_depth_and_predicate_limits()
    {
        static string Nest(int depth) => depth == 1 ? "{\"isEmpty\":\"a\"}" : "{\"not\":" + Nest(depth - 1) + "}";
        Valid(ConditionWith(Nest(WorkflowLimits.MaxConditionDepth)));
        Assert.Contains(Errors(ConditionWith(Nest(WorkflowLimits.MaxConditionDepth + 1))), e => e.Code == "condition-too-deep");

        static string Many(int count) => "{\"all\":[" + string.Join(",", Enumerable.Range(0, (count + 9) / 10).Select(g =>
            "{\"any\":[" + string.Join(",", Enumerable.Repeat("{\"isEmpty\":\"a\"}", Math.Min(10, count - (g * 10)))) + "]}")) + "]}";
        Valid(ConditionWith(Many(WorkflowLimits.MaxConditionPredicates)));
        AssertRejected(ConditionWith(Many(WorkflowLimits.MaxConditionPredicates + 1)), "$.steps[0].config.if", "condition-too-many-predicates");
    }

    [Fact]
    public void TransientWhen_is_a_validated_condition_that_counts_against_the_limits()
    {
        var terminal = S("done", "captureResult", Cfg("{\"outcome\":{\"status\":\"failed\",\"code\":\"Busy\",\"transientWhen\":{\"elementVisible\":{\"testId\":\"retry-later\"}}}}"));
        Valid(With(Base, r => r["steps"] = new JsonArray(terminal)));

        var bad = S("done", "captureResult", Cfg("{\"outcome\":{\"status\":\"failed\",\"code\":\"Busy\",\"transientWhen\":{\"regex\":\"x\"}}}"));
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(bad)), "$.steps[0].config.outcome.transientWhen.regex", WorkflowErrorCodes.ForbiddenConstruct);
    }

    [Fact]
    public void Branch_nesting_is_limited_to_three_levels()
    {
        static JsonObject Nested(int levels, string prefix) => levels == 0
            ? Step(prefix + "leaf")
            : S(prefix + levels, "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"a\"}"), ["then"] = new JsonArray(Nested(levels - 1, prefix)) });

        Valid(InSteps(Nested(WorkflowLimits.MaxStepNestingDepth, "a")));
        Assert.Contains(Errors(InSteps(Nested(WorkflowLimits.MaxStepNestingDepth + 1, "b"))), e => e.Code == "step-nesting-too-deep");
    }

    [Fact]
    public void Steps_inside_branches_count_toward_the_limit_and_share_one_id_namespace()
    {
        var branch = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"a\"}"), ["then"] = Delays(WorkflowLimits.MaxSteps - 2, "t") });
        Valid(With(Base, r => r["steps"] = new JsonArray(branch, Terminal()))); // 1 + 198 + 1 = 200

        var over = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"a\"}"), ["then"] = Delays(WorkflowLimits.MaxSteps - 1, "t") });
        AssertRejected(With(Base, r => r["steps"] = new JsonArray(over, Terminal())), "$", WorkflowErrorCodes.TooManySteps);

        var duplicate = S("check", "condition", new JsonObject { ["if"] = Cfg("{\"isEmpty\":\"a\"}"), ["then"] = new JsonArray(Step("done")) });
        AssertRejected(InSteps(duplicate), "$.steps[1].id", WorkflowErrorCodes.Duplicate);
    }

    [Fact]
    public void A_delay_must_be_shorter_than_its_step_timeout()
    {
        AssertRejected(InSteps(S("d", "delay", Cfg("{\"milliseconds\":5000}"), s => s["timeoutMs"] = 5000)), "$.steps[0].config.milliseconds", "delay-exceeds-timeout");
        Valid(InSteps(S("d", "delay", Cfg("{\"milliseconds\":30000}"))));
    }

    // ----- Binding-time upload limits -----

    [Fact]
    public void Upload_extensions_must_be_permitted_by_the_server_and_maxBytes_may_not_exceed_it()
    {
        var workflow = Valid(InSteps(S("up", "upload", Cfg("{\"selector\":{\"testId\":\"f\"},\"file\":\"{{record.contract}}\",\"allowedExtensions\":[\".pdf\",\".zip\"],\"maxBytes\":2000000}"), s => s["sideEffect"] = false)));
        var parameters = new Dictionary<string, string> { ["crmBaseUrl"] = "https://crm.example.test", ["region"] = "eu" };

        var (_, errors) = WorkflowBinder.Bind(workflow.Definition, parameters, new BindingOptions { MaxUploadBytes = 1_000_000 });

        Assert.Contains(errors, e => e.Code == "upload-extension-not-permitted" && e.Path == "$.steps[0].config.allowedExtensions");
        Assert.Contains(errors, e => e.Code == "upload-max-bytes-exceeds-server" && e.Path == "$.steps[0].config.maxBytes");
        Assert.Empty(WorkflowBinder.Bind(workflow.Definition, parameters, new BindingOptions { UploadAllowedExtensions = [".pdf", ".zip"], MaxUploadBytes = 2_000_000 }).Errors);
    }
}

internal static class JsonTestExtensions
{
    /// <summary>Mutates and returns the same node (fluent test setup).</summary>
    public static T Also<T>(this T node, Action<T> mutate)
        where T : JsonNode
    {
        mutate(node);
        return node;
    }
}
