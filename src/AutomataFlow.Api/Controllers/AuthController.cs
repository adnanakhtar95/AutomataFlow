using System.ComponentModel.DataAnnotations;
using AutomataFlow.Api.Auth;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AutomataFlow.Api.Controllers;

public sealed record RegisterRequest([Required, EmailAddress, MaxLength(254)] string Email, [Required, MaxLength(128)] string Password, [Required, MaxLength(200)] string OrganizationName, [Required, MaxLength(63)] string OrganizationSlug);

public sealed record LoginRequest([Required, MaxLength(254)] string Email, [Required, MaxLength(128)] string Password, Guid? OrganizationId);

/// <param name="RefreshToken">Required for API clients. Browser sessions (ADR-077) send no body value: the token comes from the HttpOnly cookie.</param>
/// <param name="RefreshToken">API clients: the current refresh token, revoked after the switch. Browsers: omitted (the cookie is used).</param>
public sealed record SwitchRequest(Guid OrganizationId, [MaxLength(100)] string? RefreshToken);

public sealed record RefreshRequest([MaxLength(100)] string? RefreshToken);

/// <param name="RefreshToken">Null for browser sessions (ADR-077): the token is set only as an HttpOnly cookie.</param>
public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string? RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid OrganizationId, string Role);

public sealed record MembershipResponse(Guid OrganizationId, string OrganizationName, string OrganizationSlug, string Role);

public sealed record MeResponse(Guid UserId, string Email, Guid OrganizationId, string OrganizationName, string OrganizationSlug, string Role, bool IsPlatformOperator,
    IReadOnlyList<MembershipResponse> Memberships);

/// <summary>
/// Sign-up, sign-in and token rotation (docs/ARCHITECTURE.md Â§8). Maps HTTP to <see cref="AccountService"/> only.
/// Two transports for the refresh token: the JSON body (API clients) or, when the request carries the browser-session
/// header, an HttpOnly cookie only (ADR-077).
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AccountService accounts, TokenIssuer tokens) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting(RateLimits.Authentication)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.RegisterAsync(request.Email, request.Password, request.OrganizationName, request.OrganizationSlug, cancellationToken), StatusCodes.Status201Created);

    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Authentication)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.LoginAsync(request.Email, request.Password, request.OrganizationId, cancellationToken), StatusCodes.Status200OK);

    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimits.Session)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RefreshRequest? request, CancellationToken cancellationToken)
    {
        if (PresentedToken(request) is not { } presented)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "A refresh token is required.");
        }

        return Map(await accounts.RefreshAsync(presented, cancellationToken), StatusCodes.Status200OK);
    }

    [HttpPost("logout")]
    [EnableRateLimiting(RateLimits.Session)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RefreshRequest? request, CancellationToken cancellationToken)
    {
        if (PresentedToken(request) is { } presented)
        {
            await accounts.LogoutAsync(presented, cancellationToken);
        }

        if (BrowserSession.IsRequested(Request))
        {
            BrowserSession.Clear(Response);
        }

        return NoContent();
    }

    /// <summary>The body token for API clients; the cookie only for browser-session requests (custom header = CSRF guard).</summary>
    private string? PresentedToken(RefreshRequest? request)
    {
        if (!string.IsNullOrEmpty(request?.RefreshToken))
        {
            return request.RefreshToken;
        }

        return BrowserSession.IsRequested(Request) ? BrowserSession.Read(Request) : null;
    }

    /// <summary>Signs in to another workspace of the same user (a new session; the previous refresh token is revoked).</summary>
    [HttpPost("switch")]
    [EnableRateLimiting(RateLimits.Session)]
    [Authorize(Policy = Policies.Viewer)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Switch(SwitchRequest request, [FromServices] ICurrentUser current, CancellationToken cancellationToken)
    {
        var result = await accounts.SwitchAsync(current.UserId, request.OrganizationId, cancellationToken);
        if (result.Status == AccountStatus.Ok && PresentedToken(new RefreshRequest(request.RefreshToken)) is { } previous)
        {
            await accounts.LogoutAsync(previous, cancellationToken);
        }

        return Map(result, StatusCodes.Status200OK);
    }

    private ObjectResult Map(AccountResult result, int successStatus)
    {
        var browser = BrowserSession.IsRequested(Request);
        Response.Headers.CacheControl = "no-store";
        if (result is { Status: AccountStatus.Ok, Session: { } s })
        {
            return StatusCode(successStatus, SessionResponses.Create(Request, Response, tokens, s));
        }

        if (browser && result.Status is AccountStatus.InvalidToken or AccountStatus.NotAMember)
        {
            BrowserSession.Clear(Response);
        }

        // Uniform messages: no account enumeration, no echo of input values.
        return result.Status switch
        {
            AccountStatus.Invalid => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request is invalid.", detail: string.Join(",", result.Errors ?? [])),
            AccountStatus.Conflict => Problem(statusCode: StatusCodes.Status409Conflict, title: "The account or organization already exists."),
            AccountStatus.RegistrationDisabled => Problem(statusCode: StatusCodes.Status403Forbidden, title: "Registration is disabled."),
            AccountStatus.LockedOut => Problem(statusCode: StatusCodes.Status423Locked, title: "The account is temporarily locked."),
            AccountStatus.Suspended => Problem(statusCode: StatusCodes.Status403Forbidden, title: "The workspace is suspended."),
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication failed."),
        };
    }
}

/// <summary>Turns an authenticated session into a token response, honouring the browser-session transport (ADR-077).</summary>
public static class SessionResponses
{
    public static TokenResponse Create(HttpRequest request, HttpResponse response, TokenIssuer tokens, AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(session);
        response.Headers.CacheControl = "no-store";
        var (access, expires) = tokens.Issue(session.UserId, session.OrganizationId);
        string? bodyToken = session.RefreshToken;
        if (BrowserSession.IsRequested(request))
        {
            BrowserSession.Write(response, session.RefreshToken, session.RefreshExpiresAt);
            bodyToken = null;
        }

        return new TokenResponse(access, expires, bodyToken, session.RefreshExpiresAt, session.OrganizationId, session.Role.ToString());
    }
}

[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = Policies.Viewer)]
public sealed class MeController(ICurrentUser current, AccountService accounts) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (await accounts.ProfileAsync(current.UserId, current.OrganizationId, cancellationToken) is not { } p)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not a member of this organization.");
        }

        return Ok(new MeResponse(p.UserId, p.Email, p.OrganizationId, p.OrganizationName, p.OrganizationSlug, p.Role.ToString(), p.IsPlatformOperator,
            [.. p.Memberships.Select(m => new MembershipResponse(m.OrganizationId, m.OrganizationName, m.OrganizationSlug, m.Role.ToString()))]));
    }
}
