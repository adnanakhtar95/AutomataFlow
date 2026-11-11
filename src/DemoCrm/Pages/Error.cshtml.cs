using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string Title { get; private set; } = "Something went wrong";

    public string Message { get; private set; } = "An unexpected error occurred. Please try again.";

    public void OnGet(int? code) => Apply(code);

    public void OnPost(int? code) => Apply(code);

    private void Apply(int? code)
    {
        switch (code)
        {
            case 404:
                Title = "Not found";
                Message = "The page or customer you requested does not exist.";
                break;
            case 401:
            case 403:
                Title = "Not allowed";
                Message = "You do not have access to this page.";
                break;
        }
    }
}
