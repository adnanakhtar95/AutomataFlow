using System.Security.Claims;
using DemoCrm.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages;

public sealed class LoginModel(DemoUserAuthenticator authenticator, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true ? LocalRedirect("/") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = authenticator.Authenticate(Email, Password);
        if (user is null)
        {
            // Never log the submitted credentials.
            logger.LogWarning("Failed demo login attempt");
            ErrorMessage = "Invalid email or password.";
            Password = string.Empty;
            var failed = (PageResult)Page();
            failed.StatusCode = StatusCodes.Status401Unauthorized;
            return failed;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.DisplayName), new Claim(ClaimTypes.Email, user.Email)],
            "DemoCrmCookie");
        await HttpContext.SignInAsync("DemoCrmCookie", new ClaimsPrincipal(identity));
        logger.LogInformation("Demo user signed in");

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
