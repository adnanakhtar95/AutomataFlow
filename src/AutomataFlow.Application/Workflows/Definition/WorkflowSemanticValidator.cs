using System.Globalization;
using AutomataFlow.Application.Workflows.Binding;
using AutomataFlow.Application.Workflows.Templates;

namespace AutomataFlow.Application.Workflows.Definition;

/// <summary>
/// Semantic validation of a bound definition (docs/PHASE4_PLAN.md §2, §4–§6, §9, §10; ADR-062, ADR-063): allowed hosts
/// and canonical origins, template references and sink rules, the action catalog's section and side-effect rules,
/// explicit finalization and reconciliation. Runs only after structural binding succeeded; reports into the same
/// bounded <see cref="DiagnosticBag"/>.
/// </summary>
internal sealed partial class WorkflowSemanticValidator
{
    private readonly WorkflowDefinition _definition;
    private readonly DiagnosticBag _diagnostics;

    private WorkflowSemanticValidator(WorkflowDefinition definition, DiagnosticBag diagnostics)
    {
        _definition = definition;
        _diagnostics = diagnostics;
    }

    public static WorkflowSafetyProfile Validate(WorkflowDefinition definition, DiagnosticBag diagnostics)
    {
        var validator = new WorkflowSemanticValidator(definition, diagnostics);
        validator.ValidateAllowedHosts();
        var profile = validator.ValidateSteps();
        validator.ValidateMaskSelectors();
        validator.Warn();
        return profile;
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
        var scope = TemplateScope.DeclarationsOnly(_definition, _scope.AllStepIds);
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
            CheckSelectorText(selector.Value, scope, path + "." + SelectorKey(selector.Kind));
        }

        if (selector.AccessibleName is not null)
        {
            CheckSelectorText(selector.AccessibleName, scope, path + ".name");
        }
    }

    private void CheckSelectorText(string text, TemplateScope scope, string path)
    {
        foreach (var error in TemplateChecker.Check(text, TemplateSink.SelectorValue, scope, path).Errors)
        {
            _diagnostics.Add(error.Path, error.Code, error.Message);
        }
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
        if (_definition.MaskSelectors.IsEmpty && (_definition.Inputs.Any(i => i.Sensitive) || _hasSensitiveExtract))
        {
            _diagnostics.Warn("$.maskSelectors", "mask-selectors-recommended",
                "The workflow handles sensitive values but declares no maskSelectors; text a site displays cannot be detected automatically.");
        }
    }
}
