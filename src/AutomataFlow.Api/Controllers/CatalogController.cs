using AutomataFlow.Api.Auth;
using AutomataFlow.Application.Workflows.Actions;
using AutomataFlow.Application.Workflows.Definition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <param name="Type">The JSON spelling used in definitions (e.g. <c>navigate</c>).</param>
/// <param name="Sections">Where the action may appear: setup, reconcile, steps.</param>
public sealed record ActionInfo(string Type, IReadOnlyList<string> Sections, IReadOnlyList<string> Outputs, bool SideEffectAllowed, bool ContinueAllowed, int DefaultTimeoutMs);

/// <summary>The closed value sets of workflow schemaVersion 1 and the implemented actions, for the dashboard's editor.</summary>
public sealed record WorkflowCatalog(int SchemaVersion, IReadOnlyList<ActionInfo> Actions, IReadOnlyList<string> InputTypes, IReadOnlyList<string> ParameterTypes,
    IReadOnlyList<string> EvidenceLevels, IReadOnlyList<string> MatchKeys, IReadOnlyList<string> ErrorModes, IReadOnlyList<string> IdempotencyGuarantees);

/// <summary>
/// Static workflow metadata from Application (docs/ARCHITECTURE.md §8: the action catalogue is exposed as Application
/// metadata, never by referencing Automation). The editor offers exactly these actions.
/// </summary>
[ApiController]
[Route("api/v1/workflow-catalog")]
[Authorize(Policy = Policies.Viewer)]
public sealed class CatalogController : ControllerBase
{
    private static readonly WorkflowCatalog Catalog = Build();

    [HttpGet]
    public WorkflowCatalog Get() => Catalog;

    private static WorkflowCatalog Build()
    {
        var actions = WorkflowJsonNames.ActionTypes.Select(p => ActionCatalog.Get(p.Value)).Select(d => new ActionInfo(
            d.JsonName,
            [.. new[] { (WorkflowSections.Setup, "setup"), (WorkflowSections.Reconcile, "reconcile"), (WorkflowSections.Steps, "steps") }
                .Where(s => d.AllowedSections.HasFlag(s.Item1)).Select(s => s.Item2)],
            [.. d.Outputs, ActionCatalog.FailedOutput],
            d.SideEffectAllowed,
            d.ContinueAllowed,
            (int)d.DefaultTimeout.TotalMilliseconds));
        return new WorkflowCatalog(1, [.. actions], Keys(WorkflowJsonNames.InputTypes), Keys(WorkflowJsonNames.ParameterTypes), Keys(WorkflowJsonNames.EvidenceLevels),
            Keys(WorkflowJsonNames.MatchKeys), Keys(WorkflowJsonNames.ErrorModes), Keys(WorkflowJsonNames.Guarantees));
    }

    private static string[] Keys<T>(IEnumerable<KeyValuePair<string, T>> pairs) => [.. pairs.Select(p => p.Key)];
}
