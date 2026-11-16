using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Worker;

// TEST-ONLY: configuration comes from environment variables (Section__Key) set by the test that starts this process.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkerServices(builder.Configuration);
builder.Services.AddSingleton<IContainmentHandshake, TestOnlyContainment>();
await builder.Build().RunAsync();

/// <summary>TEST-ONLY handshake: claims a containment unit without any supervisor. Never used in production.</summary>
internal sealed class TestOnlyContainment : IContainmentHandshake
{
    public Task<ContainmentUnit> VerifyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ContainmentUnit(ContainmentKind.LinuxCgroup, "/test-only/" + Environment.ProcessId));
}