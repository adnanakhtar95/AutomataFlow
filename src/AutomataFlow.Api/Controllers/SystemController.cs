using System.Reflection;
using AutomataFlow.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AutomataFlow.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    private static readonly string Version =
        typeof(SystemController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>Public, non-sensitive service identification.</summary>
    [HttpGet("info")]
    [ProducesResponseType<SystemInfoDto>(StatusCodes.Status200OK)]
    public ActionResult<SystemInfoDto> GetInfo() => new SystemInfoDto("AutomataFlow.Api", Version);
}
