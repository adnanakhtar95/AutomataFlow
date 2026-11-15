using System.Threading.RateLimiting;
using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AutomataFlow.Api.Auth;

public static class RateLimits
{
    public const string Authentication = "auth";
}

public static class AuthSetup
{
    /// <summary>JWT bearer, membership-verified caller, role policies and rate-limited auth endpoints.</summary>
    public static IServiceCollection AddPlatformAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformIdentity(configuration);
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(JwtOptions.IsValid, "Auth:Jwt:SigningKey must be at least 256 bits (32 bytes); issuer, audience and AccessTokenMinutes (1-60) are required.")
            .ValidateOnStart();
        services.AddSingleton<TokenIssuer>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((bearer, jwt) =>
        {
            bearer.MapInboundClaims = false;
            bearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Value.Issuer,
                ValidAudience = jwt.Value.Audience,
                IssuerSigningKey = jwt.Value.Key(),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

        services.AddScoped<IAuthorizationHandler, MinimumRoleHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformOperatorHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.RequireAuthenticatedUser().AddRequirements(new MinimumRoleRequirement(OrganizationRole.Viewer)))
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().AddRequirements(new MinimumRoleRequirement(OrganizationRole.Operator)))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().AddRequirements(new MinimumRoleRequirement(OrganizationRole.Admin)))
            .AddPolicy(Policies.Owner, p => p.RequireAuthenticatedUser().AddRequirements(new MinimumRoleRequirement(OrganizationRole.Owner)))
            .AddPolicy(Policies.PlatformOperator, p => p.RequireAuthenticatedUser().AddRequirements(new PlatformOperatorRequirement()));

        var permitPerMinute = configuration.GetValue("Auth:PermitsPerMinute", 20);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimits.Authentication, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
