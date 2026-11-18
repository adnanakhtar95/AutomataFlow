using AutomataFlow.Application.Workflows.Execution;
using AutomataFlow.Worker;

// TEST-ONLY. Two modes:
//  - default: the real Worker composition with a test containment handshake (step 8a, configuration through environment);
//  - socket-holder: a unit process holding a ledgered upstream connection, for the step 7 supervisor tests (ADR-075).
if (args is ["socket-holder", var host, var port, var variant])
{
    return await SocketHolder.RunAsync(host, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), variant);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkerServices(builder.Configuration);
builder.Services.AddSingleton<IContainmentHandshake, TestOnlyContainment>();
await builder.Build().RunAsync();
return 0;

/// <summary>TEST-ONLY handshake: claims a containment unit without any supervisor. Never used in production.</summary>
internal sealed class TestOnlyContainment : IContainmentHandshake
{
    public Task<ContainmentUnit> VerifyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ContainmentUnit(ContainmentKind.LinuxCgroup, "/test-only/" + Environment.ProcessId));
}
