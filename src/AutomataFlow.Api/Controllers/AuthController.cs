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

public sealed record RefreshRequest([Required, MaxLength(100)] string RefreshToken);

public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid OrganizationId, string Role);

public sealed record MeResponse(Guid UserId, Guid OrganizationId, string Role);

/// <summary>Sign-up, sign-in and token rotation (docs/ARCHITECTURE.md §8). Maps HTTP to <see cref="AccountService"/> only.</summary>
[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimits.Authentication)]
public sealed class AuthController(AccountService accounts, TokenIssuer tokens) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.RegisterAsync(request.Email, request.Password, request.OrganizationName, request.OrganizationSlug, cancellationToken), StatusCodes.Status201Created);

    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.LoginAsync(request.Email, request.Password, request.OrganizationId, cancellationToken), StatusCodes.Status200OK);

    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        Map(await accounts.RefreshAsync(request.RefreshToken, cancellationToken), StatusCodes.Status200OK);

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        await accounts.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    private ObjectResult Map(AccountResult result, int successStatus)
    {
        if (result is { Status: AccountStatus.Ok, Session: { } s })
        {
            var (access, expires) = tokens.Issue(s.UserId, s.OrganizationId);
            return StatusCode(successStatus, new TokenResponse(access, expires, s.RefreshToken, s.RefreshExpiresAt, s.OrganizationId, s.Role.ToString()));
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
public sealed class MeController(ICurrentUser current) : ControllerBase
{
    [HttpGet]
    public MeResponse Get() => new(current.UserId, current.OrganizationId, current.Role.ToString());
}
