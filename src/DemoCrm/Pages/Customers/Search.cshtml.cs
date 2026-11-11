using DemoCrm.Customers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Customers;

public sealed class SearchModel(ICustomerStore store) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public CustomerSearchField Field { get; set; } = CustomerSearchField.Any;

    [BindProperty(SupportsGet = true)]
    public string? Term { get; set; }

    public bool Searched { get; private set; }

    public IReadOnlyList<Customer> Results { get; private set; } = [];

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Term))
        {
            return;
        }

        Searched = true;
        Results = store.Search(Field, Term);
    }
}
