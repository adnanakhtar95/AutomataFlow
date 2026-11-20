using DemoCrm.BackOffice;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DemoCrm.Pages.Orders;

/// <summary>Exact lookup by order reference (operation id): the reconciliation page of the order package.</summary>
public sealed class SearchModel(BackOfficeStore store) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Reference { get; set; }

    public bool Searched { get; private set; }

    public Order? Found { get; private set; }

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Reference))
        {
            return;
        }

        Searched = true;
        Found = store.FindOrderByOperation(Reference);
    }
}
