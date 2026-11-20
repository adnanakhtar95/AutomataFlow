using DemoCrm.BackOffice;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Orders;

public sealed class ConfirmationModel(BackOfficeStore store) : PageModel
{
    public Order Order { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? Result { get; set; }

    public IActionResult OnGet(int id)
    {
        if (store.FindOrder(id) is not { } order)
        {
            return NotFound();
        }

        Order = order;
        return Page();
    }
}
