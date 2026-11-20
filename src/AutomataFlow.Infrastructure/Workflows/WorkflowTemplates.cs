using System.Text.Json;
using AutomataFlow.Application.Abstractions;

namespace AutomataFlow.Infrastructure.Workflows;

public sealed record WorkflowTemplateSummary(string Id, string Title, string Category, string Summary, string Setup);

/// <param name="SampleCsv">A sample record file showing the expected columns.</param>
public sealed record WorkflowTemplateDetail(WorkflowTemplateSummary Template, string DefinitionJson, string SampleCsv);

/// <summary>
/// The workflow packages shipped with the product (repository folder <c>packages/</c>, embedded at build time, Phase 9).
/// A template is copied into a workspace as a new DRAFT workflow; the customer reviews, adjusts parameters and publishes
/// it like any other workflow. Templates are product content: no workspace data, no secrets.
/// </summary>
public static class WorkflowTemplates
{
    private static readonly Lazy<IReadOnlyList<WorkflowTemplateDetail>> All = new(Load);

    public static IReadOnlyList<WorkflowTemplateSummary> List() => [.. All.Value.Select(t => t.Template)];

    public static WorkflowTemplateDetail? Find(string id) => All.Value.FirstOrDefault(t => string.Equals(t.Template.Id, id, StringComparison.Ordinal));

    private static List<WorkflowTemplateDetail> Load()
    {
        var assembly = typeof(WorkflowTemplates).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("packages/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var templates = new List<WorkflowTemplateDetail>();
        foreach (var manifest in names.Where(n => n.EndsWith("/manifest.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var folder = manifest[..^"manifest.json".Length];
            using var document = JsonDocument.Parse(Read(assembly, manifest));
            var root = document.RootElement;
            string Text(string property) => root.GetProperty(property).GetString() ?? string.Empty;
            templates.Add(new WorkflowTemplateDetail(
                new WorkflowTemplateSummary(Text("id"), Text("title"), Text("category"), Text("summary"), Text("setup")),
                Read(assembly, folder + "workflow.json"),
                names.Contains(folder + "sample.csv") ? Read(assembly, folder + "sample.csv") : string.Empty));
        }

        return templates;
    }

    private static string Read(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded package file {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Creates a workspace draft from a shipped template (the workspace and permissions come from the caller, as for any draft).</summary>
public sealed class WorkflowTemplateService(WorkflowService workflows)
{
    public async Task<ServiceResult<VersionDetail>> InstantiateAsync(string templateId, string name, CancellationToken cancellationToken)
    {
        if (WorkflowTemplates.Find(templateId) is not { } template)
        {
            return ServiceResult<VersionDetail>.NotFound();
        }

        return await workflows.CreateAsync(name, template.Template.Summary.Length > 500 ? template.Template.Summary[..500] : template.Template.Summary, template.DefinitionJson, cancellationToken);
    }
}
