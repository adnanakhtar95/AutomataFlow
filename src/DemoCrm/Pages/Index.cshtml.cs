using DemoCrm.Customers;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages;

public sealed class IndexModel(ICustomerStore store) : PageModel
{
    public int CustomerCount { get; private set; }

    public void OnGet() => CustomerCount = store.Count;
}
