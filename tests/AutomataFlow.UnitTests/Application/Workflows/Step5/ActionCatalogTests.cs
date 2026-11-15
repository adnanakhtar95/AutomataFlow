using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition;
using static AutomataFlow.UnitTests.Application.Workflows.WorkflowJson;

namespace AutomataFlow.UnitTests.Application.Workflows.Step5;

/// <summary>Phase 4 step 5: the action catalog (§2, §3; ADR-053, ADR-063).</summary>
public class ActionCatalogTests
{
    [Fact]
    public void There_is_exactly_one_descriptor_per_action_type()
    {
        Assert.Equal(Enum.GetValues<ActionType>().Order(), ActionCatalog.All.Keys.Order());
        Assert.Equal(13, ActionCatalog.All.Count);
        Assert.All(ActionCatalog.All, pair => Assert.Equal(pair.Key, pair.Value.Type));
        Assert.Equal(ActionCatalog.All.Count, ActionCatalog.All.Values.Select(d => d.ConfigType).Distinct().Count());
    }

    [Fact]
    public void Descriptors_use_the_schema_json_names_and_sealed_config_types()
    {
        foreach (var (name, type) in WorkflowJsonNames.ActionTypes)
        {
            var descriptor = ActionCatalog.Get(type);
            Assert.Equal(name, descriptor.JsonName);
            Assert.True(descriptor.ConfigType.IsSealed && descriptor.ConfigType.IsSubclassOf(typeof(ActionConfig)), descriptor.ConfigType.Name);
        }
    }

    [Fact]
    public void Only_click_and_upload_may_have_side_effects()
    {
        Assert.Equal([ActionType.Click, ActionType.Upload], ActionCatalog.All.Values.Where(d => d.SideEffectAllowed).Select(d => d.Type).Order());
    }

    /// <summary>R10/ADR-053: continue only on actions that read, type into or wait on the current page.</summary>
    [Fact]
    public void Continue_is_allowed_only_on_the_six_non_navigating_actions()
    {
        Assert.Equal(
            [ActionType.Fill, ActionType.Select, ActionType.Wait, ActionType.Extract, ActionType.Screenshot, ActionType.Condition],
            ActionCatalog.All.Values.Where(d => d.ContinueAllowed).Select(d => d.Type).Order());
    }

    [Fact]
    public void Section_placement_matches_the_specification()
    {
        Assert.Equal(WorkflowSections.Steps, ActionCatalog.Get(ActionType.Upload).AllowedSections);
        Assert.Equal(WorkflowSections.Setup | WorkflowSections.Steps, ActionCatalog.Get(ActionType.Download).AllowedSections);
        Assert.Equal(WorkflowSections.Setup | WorkflowSections.Steps, ActionCatalog.Get(ActionType.CaptureResult).AllowedSections);
        Assert.Equal(WorkflowSections.Reconcile, ActionCatalog.Get(ActionType.ReconcileMatch).AllowedSections);
        var everywhere = new[] { ActionType.Navigate, ActionType.Click, ActionType.Fill, ActionType.Select, ActionType.Wait, ActionType.Extract, ActionType.Screenshot, ActionType.Condition, ActionType.Delay };
        Assert.All(everywhere, t => Assert.Equal(WorkflowSections.All, ActionCatalog.Get(t).AllowedSections));
    }

    [Fact]
    public void Outputs_match_the_specification()
    {
        Assert.Equal(["status", "url"], ActionCatalog.Get(ActionType.Navigate).Outputs.AsEnumerable());
        Assert.Equal(["value"], ActionCatalog.Get(ActionType.Extract).Outputs.AsEnumerable());
        Assert.Equal(["artifactId"], ActionCatalog.Get(ActionType.Screenshot).Outputs.AsEnumerable());
        Assert.Equal(["fileName"], ActionCatalog.Get(ActionType.Upload).Outputs.AsEnumerable());
        Assert.Equal(["artifactId", "fileName", "sha256"], ActionCatalog.Get(ActionType.Download).Outputs.AsEnumerable());
        Assert.Equal(["result"], ActionCatalog.Get(ActionType.Condition).Outputs.AsEnumerable());
        Assert.All(new[] { ActionType.Click, ActionType.Fill, ActionType.Select, ActionType.Wait, ActionType.Delay, ActionType.CaptureResult, ActionType.ReconcileMatch },
            t => Assert.Empty(ActionCatalog.Get(t).Outputs));
    }

    [Fact]
    public void Default_timeouts_are_frozen_and_within_bounds()
    {
        Assert.All(ActionCatalog.All.Values, d => Assert.InRange(d.DefaultTimeout.TotalMilliseconds, WorkflowLimits.MinStepTimeoutMs, WorkflowLimits.MaxStepTimeoutMs));
        Assert.All(ActionCatalog.All.Values.Where(d => d.SideEffectAllowed), d => Assert.True(d.DefaultTimeout.TotalMilliseconds >= WorkflowLimits.MinSideEffectStepTimeoutMs));
        Assert.True(ActionCatalog.Get(ActionType.Delay).DefaultTimeout.TotalMilliseconds > WorkflowLimits.MaxDelayMilliseconds);

        Assert.Equal(
            [30_000, 15_000, 10_000, 10_000, 30_000, 10_000, 15_000, 30_000, 60_000, 10_000, 31_000, 5_000, 30_000],
            Enum.GetValues<ActionType>().Select(t => (int)ActionCatalog.Get(t).DefaultTimeout.TotalMilliseconds));
    }

    [Fact]
    public void The_kitchen_sink_uses_every_action_and_computes_its_safety_profile()
    {
        var workflow = Valid(KitchenSink);

        Assert.Equal(Enum.GetValues<ActionType>().ToHashSet(), workflow.Definition.AllSteps().Select(s => s.Type).ToHashSet());
        Assert.True(workflow.Safety.HasSideEffects);
        Assert.False(workflow.Safety.TargetEnforced);
        Assert.True(workflow.Safety.OperationIdSent);
        Assert.True(workflow.Safety.RequiresDownloads);
        Assert.DoesNotContain(workflow.Warnings, w => w.Code == "navigating-click-without-side-effect"); // the navigating click in steps declares true
    }

    [Fact]
    public void The_full_sample_attests_target_enforced_idempotency()
    {
        var safety = Valid(Full).Safety;

        Assert.True(safety.HasSideEffects);
        Assert.True(safety.TargetEnforced);
        Assert.True(safety.OperationIdSent);
        Assert.False(safety.RequiresDownloads);
    }

    [Fact]
    public void A_workflow_without_side_effects_attests_nothing()
    {
        var safety = Valid(Minimal).Safety;

        Assert.False(safety.HasSideEffects);
        Assert.False(safety.TargetEnforced);
        Assert.False(safety.OperationIdSent);
    }

    [Fact]
    public void Typed_configs_carry_the_parsed_values()
    {
        var steps = Valid(KitchenSink).Definition.AllSteps().ToDictionary(s => s.Id);

        var open = Assert.IsType<NavigateConfig>(steps["open"].Action);
        Assert.Equal("crmBaseUrl", open.Url.OriginParameter);
        Assert.Equal([404], open.AcceptStatus.AsEnumerable());
        Assert.True(Assert.IsType<ClickConfig>(steps["save"].Action).WaitForNavigation);
        Assert.Equal(SelectBy.Label, Assert.IsType<SelectConfig>(steps["plan"].Action).By);
        Assert.Equal(WaitFor.Load, Assert.IsType<WaitConfig>(steps["loaded"].Action).Kind);
        Assert.Equal(WaitFor.Url, Assert.IsType<WaitConfig>(steps["confirmed"].Action).Kind);
        var extract = Assert.IsType<ExtractConfig>(steps["read-id"].Action);
        Assert.Equal((ExtractSource.Attribute, "data-id", 50, false), (extract.Source, extract.Attribute, extract.MaxLength, extract.Sensitive));
        Assert.True(Assert.IsType<ScreenshotConfig>(steps["proof"].Action).FullPage);
        var upload = Assert.IsType<UploadConfig>(steps["attach"].Action);
        Assert.Equal([".pdf"], upload.AllowedExtensions.AsEnumerable());
        Assert.Null(upload.MaxBytes);
        Assert.Equal(250, Assert.IsType<DelayConfig>(steps["pause"].Action).Milliseconds);
        var condition = Assert.IsType<ConditionConfig>(steps["check"].Action);
        Assert.IsType<CombinedCondition>(condition.If);
        Assert.Equal("ok", Assert.Single(condition.Then).Id);
        var dup = Assert.IsType<CaptureResultConfig>(steps["dup"].Action).Outcome!;
        Assert.Equal((OutcomeStatus.Failed, "DuplicateCustomer", false), (dup.Status, dup.Code, dup.Transient));
        var match = Assert.IsType<ReconcileMatchConfig>(steps["match"].Action);
        Assert.Equal((true, SchemaV1Defaults.ReconcileMaxRows, "data-id"), (match.IgnoreCase, match.MaxRows, match.Fields[0].Value));
        Assert.Equal(WorkflowSelectorKind.Css, match.Key.Text!.Kind);
    }
}
