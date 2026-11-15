using AutomataFlow.Automation;
using AutomataFlow.Automation.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutomataFlow.AutomationTests.Workflows;

/// <summary>ADR-058 fail-closed rule 1–2 and §3: AddWorkflowEngine registers no journal, so the executor cannot be resolved without one.</summary>
public sealed class WorkflowEngineRegistrationTests
{
    [Fact]
    public void A_host_without_an_attempt_journal_cannot_resolve_the_executor()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging().AddBrowserEngine(configuration).AddWorkflowEngine(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IWorkflowExecutor>());
        Assert.Contains("IAttemptJournal", error.Message, StringComparison.Ordinal);
        Assert.Equal(13, provider.GetServices<IActionHandler>().Count());
        Assert.NotNull(provider.GetRequiredService<ActionRegistry>());
    }
}
