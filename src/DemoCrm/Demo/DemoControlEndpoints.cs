using System.Security.Cryptography;
using System.Text;
using DemoCrm.Configuration;
using DemoCrm.Customers;
using DemoCrm.Simulation;
using Microsoft.Extensions.Options;

namespace DemoCrm.Demo;

/// <summary>
/// Machine-facing control endpoints for tests and automation: reset the demo data and read its state.
/// Disabled (404) unless <c>DemoCrm:ControlApi:Token</c> is configured; callers must send it in
/// <c>X-Demo-Control-Token</c>.
/// </summary>
public static class DemoControlEndpoints
{
    public const string TokenHeader = "X-Demo-Control-Token";

    public static IEndpointRouteBuilder MapDemoControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/demo").AllowAnonymous().DisableAntiforgery();

        group.MapPost("/reset", (HttpContext context, IOptions<DemoCrmOptions> options, ICustomerStore store, FailureSimulator simulator, RegistrationCounter counter,
            BackOffice.BackOfficeStore backOffice, BackOffice.OrderCounter orders, BackOffice.ApprovalCounter approvals) =>
        {
            if (Authorize(context, options.Value) is { } denied)
            {
                return denied;
            }

            store.Reset();
            simulator.Reset();
            counter.Reset();
            backOffice.Reset();
            orders.Reset();
            approvals.Reset();
            return Results.NoContent();
        });

        group.MapGet("/state", (HttpContext context, IOptions<DemoCrmOptions> options, ICustomerStore store, FailureSimulator simulator, RegistrationCounter counter,
            BackOffice.BackOfficeStore backOffice, BackOffice.OrderCounter orders, BackOffice.ApprovalCounter approvals) =>
        {
            if (Authorize(context, options.Value) is { } denied)
            {
                return denied;
            }

            return Results.Ok(new DemoStateDto(store.Count, store.SeededCount, simulator.TrackedAddresses, counter.Count, backOffice.OrderCount, orders.Count, backOffice.ApprovalCount, approvals.Count));
        });

        return endpoints;
    }

    private static IResult? Authorize(HttpContext context, DemoCrmOptions options)
    {
        var expected = options.ControlApi.Token;
        if (string.IsNullOrEmpty(expected))
        {
            return Results.NotFound();
        }

        var supplied = context.Request.Headers[TokenHeader].ToString();
        var equal = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        return equal ? null : Results.Unauthorized();
    }
}

/// <summary>Demo state counts. Submissions count every POST (before validation), so tests detect any resubmission.</summary>
public sealed record DemoStateDto(int CustomerCount, int SeededCount, int SimulatedAddresses, int RegistrationSubmissions, int OrderCount = 0, int OrderSubmissions = 0, int ApprovalCount = 0, int ApprovalSubmissions = 0);
