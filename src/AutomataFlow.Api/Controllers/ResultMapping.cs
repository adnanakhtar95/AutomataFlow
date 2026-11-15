using AutomataFlow.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

/// <summary>Maps use-case results to HTTP (ProblemDetails for failures; errors carry paths and codes, never values).</summary>
public static class ResultMapping
{
    public static IActionResult ToHttp<T>(this ControllerBase controller, ServiceResult<T> result, Func<T, IActionResult> ok)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ok);
        if (result.Status == ServiceStatus.Ok)
        {
            return ok(result.Value!);
        }

        var status = result.Status switch
        {
            ServiceStatus.NotFound => StatusCodes.Status404NotFound,
            ServiceStatus.Conflict => StatusCodes.Status409Conflict,
            ServiceStatus.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails { Status = status, Title = result.Status == ServiceStatus.NotFound ? "Not found." : "The request could not be completed." };
        if (result.Errors.Count > 0)
        {
            problem.Extensions["errors"] = result.Errors;
        }

        return new ObjectResult(problem) { StatusCode = status };
    }
}
