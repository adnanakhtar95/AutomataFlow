using DemoCrm.Configuration;
using DemoCrm.Customers;
using DemoCrm.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace DemoCrm.Pages.Admin;

public sealed class IndexModel(
    ICustomerStore store,
    FailureSimulator simulator,
    IOptions<DemoCrmOptions> options,
    ILogger<IndexModel> logger) : PageModel
{
    [TempData]
    public string? ResetMessage { get; set; }

    public int CustomerCount { get; private set; }

    public int SeededCount { get; private set; }

    public bool SimulationEnabled { get; private set; }

    public void OnGet() => Load();

    public IActionResult OnPostReset()
    {
        store.Reset();
        simulator.Reset();
        logger.LogInformation("Demo data reset to seed");
        ResetMessage = "Demo data has been reset to the original seed.";
        return RedirectToPage();
    }

    private void Load()
    {
        CustomerCount = store.Count;
        SeededCount = store.SeededCount;
        SimulationEnabled = options.Value.Simulation.Enabled;
    }
}
