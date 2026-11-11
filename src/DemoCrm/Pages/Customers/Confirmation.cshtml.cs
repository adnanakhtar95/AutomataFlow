using DemoCrm.Customers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Customers;

public sealed class ConfirmationModel(ICustomerStore store) : PageModel
{
    public Customer Customer { get; private set; } = null!;

    /// <summary><c>created</c> or <c>replayed</c>.</summary>
    public string Result { get; private set; } = "created";

    public string Message { get; private set; } = string.Empty;

    public IActionResult OnGet(int id, string? result)
    {
        var customer = store.Find(id);
        if (customer is null)
        {
            return NotFound();
        }

        Customer = customer;
        Result = string.Equals(result, "replayed", StringComparison.OrdinalIgnoreCase) ? "replayed" : "created";
        Message = Result == "replayed"
            ? $"Registration already processed. Customer {customer.DisplayId} was created earlier by the same operation."
            : $"Customer {customer.DisplayId} registered successfully.";
        return Page();
    }
}
