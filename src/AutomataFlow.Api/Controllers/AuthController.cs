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
[EnableRateLimiting(RateLimits.Authentication)]
public sealed class AuthController(AccountService accounts, TokenIssuer tokens) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.RegisterAsync(request.Email, request.Password, request.OrganizationName, request.OrganizationSlug, cancellationToken), StatusCodes.Status201Created);

    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.LoginAsync(request.Email, request.Password, request.OrganizationId, cancellationToken), StatusCodes.Status200OK);

    [HttpPost("refresh")]
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

    private ObjectResult Map(AccountResult result, int successStatus)
    {
        var browser = BrowserSession.IsRequested(Request);
        Response.Headers.CacheControl = "no-store";
        if (result is { Status: AccountStatus.Ok, Session: { } s })
        {
            var (access, expires) = tokens.Issue(s.UserId, s.OrganizationId);
            string? bodyToken = s.RefreshToken;
            if (browser)
            {
                BrowserSession.Write(Response, s.RefreshToken, s.RefreshExpiresAt);
                bodyToken = null;
            }

            return StatusCode(successStatus, new TokenResponse(access, expires, bodyToken, s.RefreshExpiresAt, s.OrganizationId, s.Role.ToString()));
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
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication failed."),
        };
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
