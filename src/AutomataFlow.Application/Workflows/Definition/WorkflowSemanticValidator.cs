using System.Globalization;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Templates;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Semantic validation of a bound definition (docs/PHASE4_PLAN.md §4–§6, §10; ADR-062): allowed hosts and canonical
/// origins, template references and sink rules, and (from step 5) the action catalog rules. Runs only after structural
/// binding succeeded; reports into the same bounded <see cref="DiagnosticBag"/>.
/// </summary>
internal sealed class WorkflowSemanticValidator
{
    private readonly WorkflowDefinition _definition;
    private readonly DiagnosticBag _diagnostics;

    private WorkflowSemanticValidator(WorkflowDefinition definition, DiagnosticBag diagnostics)
    {
        _definition = definition;
        _diagnostics = diagnostics;
    }

    public static void Validate(WorkflowDefinition definition, DiagnosticBag diagnostics)
    {
        var validator = new WorkflowSemanticValidator(definition, diagnostics);
        validator.ValidateAllowedHosts();
        validator.ValidateMaskSelectors();
        validator.Warn();
    }

    private void ValidateAllowedHosts()
    {
        for (var i = 0; i < _definition.AllowedHosts.Length; i++)
        {
            if (AllowedHosts.Check(_definition.AllowedHosts[i], "$.allowedHosts[" + i.ToString(CultureInfo.InvariantCulture) + "]", _definition) is { } error)
            {
                _diagnostics.Add(error.Path, error.Code, error.Message);
            }
        }
    }

    /// <summary>Mask selectors are resolved when the session is created, before any step: declarations only.</summary>
    private void ValidateMaskSelectors()
    {
        var allStepIds = _definition.Setup.Concat(_definition.Reconcile).Concat(_definition.Steps).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var scope = TemplateScope.DeclarationsOnly(_definition, allStepIds);
        for (var i = 0; i < _definition.MaskSelectors.Length; i++)
        {
            CheckSelector(_definition.MaskSelectors[i], "$.maskSelectors[" + i.ToString(CultureInfo.InvariantCulture) + "]", scope);
        }
    }

    /// <summary>Selector values that may hold placeholders (testId, text, label, placeholder, role name) use the selector sink.</summary>
    private void CheckSelector(WorkflowSelector selector, string path, TemplateScope scope)
    {
        if (selector.Kind is WorkflowSelectorKind.TestId or WorkflowSelectorKind.Text or WorkflowSelectorKind.Label or WorkflowSelectorKind.Placeholder)
        {
            CheckTemplate(selector.Value, TemplateSink.SelectorValue, scope, path + "." + SelectorKey(selector.Kind));
        }

        if (selector.AccessibleName is not null)
        {
            CheckTemplate(selector.AccessibleName, TemplateSink.SelectorValue, scope, path + ".name");
        }
    }

    private (Template? Template, TemplateTaint Taint) CheckTemplate(string text, TemplateSink sink, TemplateScope scope, string path)
    {
        var (template, taint, errors) = TemplateChecker.Check(text, sink, scope, path);
        foreach (var error in errors)
        {
            _diagnostics.Add(error.Path, error.Code, error.Message);
        }

        return (template, taint);
    }

    private static string SelectorKey(WorkflowSelectorKind kind) => kind switch
    {
        WorkflowSelectorKind.TestId => "testId",
        WorkflowSelectorKind.Css => "css",
        WorkflowSelectorKind.Role => "role",
        WorkflowSelectorKind.Text => "text",
        WorkflowSelectorKind.Label => "label",
        WorkflowSelectorKind.Placeholder => "placeholder",
        _ => "xpath",
    };

    /// <summary>§5.5: a warning (not an error) when sensitive data exists but no maskSelectors are declared.</summary>
    private void Warn()
    {
        if (_definition.MaskSelectors.IsEmpty && (_definition.Inputs.Any(i => i.Sensitive) || HasSensitiveExtract()))
        {
            _diagnostics.Warn("$.maskSelectors", "mask-selectors-recommended",
                "The workflow handles sensitive values but declares no maskSelectors; text a site displays cannot be detected automatically.");
        }
    }

    private bool HasSensitiveExtract() =>
        AllSteps(_definition.Setup).Concat(AllSteps(_definition.Reconcile)).Concat(AllSteps(_definition.Steps))
            .Any(s => s.Type == ActionType.Extract && s.Config.TryGet("sensitive", out var flag) && flag is Json.JBoolean { Value: true });

    private static IEnumerable<WorkflowStep> AllSteps(IEnumerable<WorkflowStep> steps) => steps;
}
