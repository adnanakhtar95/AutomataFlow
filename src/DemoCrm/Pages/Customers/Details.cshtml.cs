using DemoCrm.Customers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Customers;

public sealed class DetailsModel(ICustomerStore store) : PageModel
{
    public Customer Customer { get; private set; } = null!;

    public IActionResult OnGet(int id)
    {
        var customer = store.Find(id);
        if (customer is null)
        {
            return NotFound();
        }

        Customer = customer;
        return Page();
    }
}
