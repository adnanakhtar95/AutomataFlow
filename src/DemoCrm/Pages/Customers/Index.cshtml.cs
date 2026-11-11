using DemoCrm.Customers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Customers;

public sealed class IndexModel(ICustomerStore store) : PageModel
{
    public const int PageSize = 10;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public PagedResult<Customer> Result { get; private set; } = new([], 1, PageSize, 0);

    public void OnGet() => Result = store.List(Q, P, PageSize);
}
