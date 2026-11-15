using System.Text.Json.Nodes;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition;
using AutomataFlow.Application.Workflows.Templates;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step5;

/// <summary>Phase 4 step 5: the pure, short-circuit condition evaluator over a fake page probe (§8.1).</summary>
public class ConditionEvaluatorTests
{
    private sealed class FakeProbe : IPageProbe
    {
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public string CurrentUrl { get; set; } = "https://crm.example.test/Customers/Confirmation/7?x=1";

        public Task<int> CountAsync(ResolvedSelector selector, CancellationToken cancellationToken)
        {
            Calls.Add("count:" + selector.Value);
            return Task.FromResult(Counts.GetValueOrDefault(selector.Value));
        }

        public Task<bool> IsVisibleAsync(ResolvedSelector selector, CancellationToken cancellationToken)
        {
            Calls.Add("visible:" + selector.Value);
            return Task.FromResult(Visible.Contains(selector.Value));
        }

        public Task<string?> GetTextAsync(ResolvedSelector selector, CancellationToken cancellationToken)
        {
            Calls.Add("text:" + selector.Value);
            return Task.FromResult(Texts.TryGetValue(selector.Value, out var text) ? text : null);
        }

        public Task<string?> GetAttributeAsync(ResolvedSelector selector, string attribute, CancellationToken cancellationToken)
        {
            Calls.Add("attr:" + selector.Value + "@" + attribute);
            return Task.FromResult(Attributes.TryGetValue(selector.Value + "@" + attribute, out var value) ? value : null);
        }
    }

    private sealed class Values : ITemplateValues
    {
        public Guid OperationId => Guid.Parse("11111111-2222-3333-4444-555555555555");

        public Dictionary<string, string?> Record { get; } = new(StringComparer.Ordinal) { ["email"] = "ada@example.test", ["status"] = "Active" };

        public bool TryGetRecordValue(string name, out string? value) => Record.TryGetValue(name, out value);

        public bool TryGetParameter(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        public bool TryGetSecret(string name, out string value)
        {
            value = "s3cr3t";
            return true;
        }

        public bool TryGetStepOutput(string stepId, string output, out string value, out bool isSensitive)
        {
            value = string.Empty;
            isSensitive = false;
            return false;
        }
    }

    private static readonly WorkflowDefinition Definition = Valid(With(r =>
    {
        r["inputs"] = new JsonArray(
            new JsonObject { ["name"] = "email", ["type"] = "email" },
            new JsonObject { ["name"] = "status", ["type"] = "string" },
            new JsonObject { ["name"] = "nickname", ["type"] = "string" });
    })).Definition;

    /// <summary>Parses an expression through the real binder (a condition step) so evaluation sees validated input.</summary>
    private static ConditionExpr Expr(string expression)
    {
        var json = With(r =>
        {
            r["inputs"] = new JsonArray(
                new JsonObject { ["name"] = "email", ["type"] = "email" },
                new JsonObject { ["name"] = "status", ["type"] = "string" },
                new JsonObject { ["name"] = "nickname", ["type"] = "string" });
            r["steps"] = new JsonArray(
                new JsonObject { ["id"] = "check", ["type"] = "condition", ["config"] = new JsonObject { ["if"] = JsonNode.Parse(expression), ["then"] = new JsonArray(Step("t")) } },
                Terminal());
        });

        return ((ConditionConfig)Valid(json).Definition.Steps[0].Action).If;
    }

    private static async Task<ConditionResult> Evaluate(string expression, FakeProbe probe) =>
        await ConditionEvaluator.CheckAsync(Expr(expression), probe, new Values(), TemplateResolver.InputLookup(Definition), CancellationToken.None);

    [Theory]
    [InlineData("{\"elementExists\":{\"testId\":\"row\"}}", true)]
    [InlineData("{\"elementExists\":{\"testId\":\"none\"}}", false)]
    [InlineData("{\"elementVisible\":{\"testId\":\"banner\"}}", true)]
    [InlineData("{\"elementCountAtLeast\":{\"selector\":{\"testId\":\"row\"},\"count\":3}}", true)]
    [InlineData("{\"elementCountAtLeast\":{\"selector\":{\"testId\":\"row\"},\"count\":4}}", false)]
    [InlineData("{\"textEquals\":{\"selector\":{\"testId\":\"title\"},\"value\":\"Customer saved\"}}", true)]
    [InlineData("{\"textEquals\":{\"selector\":{\"testId\":\"title\"},\"value\":\"customer saved\"}}", false)]
    [InlineData("{\"textEquals\":{\"selector\":{\"testId\":\"title\"},\"value\":\"customer saved\",\"ignoreCase\":true}}", true)]
    [InlineData("{\"textContains\":{\"selector\":{\"testId\":\"title\"},\"value\":\"saved\"}}", true)]
    [InlineData("{\"textEquals\":{\"selector\":{\"testId\":\"missing\"},\"value\":\"\"}}", false)]
    [InlineData("{\"attributeEquals\":{\"selector\":{\"testId\":\"form\"},\"attribute\":\"data-kind\",\"value\":\"validation\"}}", true)]
    [InlineData("{\"attributeEquals\":{\"selector\":{\"testId\":\"form\"},\"attribute\":\"data-other\",\"value\":\"\"}}", false)]
    [InlineData("{\"urlMatches\":\"**/Customers/Confirmation/**\"}", true)]
    [InlineData("{\"urlMatches\":\"**/Customers/Edit/**\"}", false)]
    [InlineData("{\"valueEquals\":{\"left\":\"{{record.status}}\",\"right\":\"Active\"}}", true)]
    [InlineData("{\"valueEquals\":{\"left\":\"{{record.status}}\",\"right\":\"active\"}}", false)]
    [InlineData("{\"valueEquals\":{\"left\":\"{{record.status}}\",\"right\":\"active\",\"ignoreCase\":true}}", true)]
    [InlineData("{\"valueContains\":{\"left\":\"{{record.email}}\",\"right\":\"@example\"}}", true)]
    [InlineData("{\"valueStartsWith\":{\"left\":\"{{record.email}}\",\"right\":\"ada\"}}", true)]
    [InlineData("{\"isEmpty\":\"{{record.nickname}}\"}", true)]
    [InlineData("{\"isEmpty\":\"{{record.email}}\"}", false)]
    [InlineData("{\"elementExists\":{\"testId\":\"user-{{record.status}}\"}}", true)]
    public async Task Predicates_evaluate_against_the_page_and_values(string expression, bool expected)
    {
        var probe = Page();

        var result = await Evaluate(expression, probe);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value);
    }

    private static FakeProbe Page()
    {
        var probe = new FakeProbe();
        probe.Counts["row"] = 3;
        probe.Counts["user-Active"] = 1;
        probe.Visible.Add("banner");
        probe.Texts["title"] = "  Customer saved \n";
        probe.Attributes["form@data-kind"] = "validation";
        return probe;
    }

    [Theory]
    [InlineData("{\"all\":[{\"elementExists\":{\"testId\":\"row\"}},{\"elementVisible\":{\"testId\":\"banner\"}}]}", true)]
    [InlineData("{\"all\":[{\"elementExists\":{\"testId\":\"row\"}},{\"elementVisible\":{\"testId\":\"hidden\"}}]}", false)]
    [InlineData("{\"any\":[{\"elementExists\":{\"testId\":\"none\"}},{\"elementVisible\":{\"testId\":\"banner\"}}]}", true)]
    [InlineData("{\"any\":[{\"elementExists\":{\"testId\":\"none\"}},{\"elementVisible\":{\"testId\":\"hidden\"}}]}", false)]
    [InlineData("{\"not\":{\"elementExists\":{\"testId\":\"none\"}}}", true)]
    [InlineData("{\"not\":{\"elementExists\":{\"testId\":\"row\"}}}", false)]
    public async Task Combinators_follow_their_truth_tables(string expression, bool expected)
    {
        Assert.Equal(expected, (await Evaluate(expression, Page())).Value);
    }

    [Fact]
    public async Task Evaluation_short_circuits_left_to_right()
    {
        var anyProbe = Page();
        await Evaluate("{\"any\":[{\"elementExists\":{\"testId\":\"row\"}},{\"elementVisible\":{\"testId\":\"banner\"}}]}", anyProbe);
        Assert.Equal(["count:row"], anyProbe.Calls);

        var allProbe = Page();
        await Evaluate("{\"all\":[{\"elementExists\":{\"testId\":\"none\"}},{\"elementVisible\":{\"testId\":\"banner\"}}]}", allProbe);
        Assert.Equal(["count:none"], allProbe.Calls);
    }

    [Fact]
    public async Task An_unresolvable_selector_value_is_an_evaluation_error_not_false()
    {
        var result = await Evaluate("{\"elementExists\":{\"testId\":\"{{record.nickname}}\"}}", Page());

        Assert.False(result.Succeeded);
        Assert.Equal(EngineErrorCodes.VariableUnresolved, result.ErrorCode);
    }
}
