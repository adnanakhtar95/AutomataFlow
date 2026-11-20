using DemoCrm.BackOffice;
using DemoCrm.Configuration;
using DemoCrm.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace DemoCrm.Pages.Invoices;

/// <summary>
/// Invoice approval queue (Phase 9 fixture extension). Approving is a one-time state transition; the approving request's
/// operation reference is recorded so that reconciliation can find it. Two seeded invoices simulate failures:
/// INV-1006 fails once BEFORE committing (temporary error), INV-1007 commits and then reports a temporary error once.
/// </summary>
public sealed class IndexModel(BackOfficeStore store, FailureSimulator simulator, ApprovalCounter counter, IOptions<DemoCrmOptions> options, TimeProvider time) : PageModel
{
    public const string FailBeforeCommitInvoice = "INV-1006";
    public const string CommitThenFailInvoice = "INV-1007";

    [BindProperty(SupportsGet = true)]
    public string? Number { get; set; }

    [BindProperty]
    public string? OperationId { get; set; }

    public Invoice? Invoice { get; private set; }

    public string? ErrorKind { get; private set; }

    public string? Notice { get; private set; }

    public void OnGet() => Invoice = string.IsNullOrWhiteSpace(Number) ? null : store.FindInvoice(Number);

    public IActionResult OnPostApprove()
    {
        counter.Increment();
        if (string.IsNullOrWhiteSpace(Number))
        {
            return NotFound();
        }

        var number = Number.Trim().ToUpperInvariant();
        var simulate = options.Value.Simulation.Enabled;
        var attempt = simulate && number is FailBeforeCommitInvoice or CommitThenFailInvoice ? simulator.RecordAttempt("invoice:" + number) : 0;
        if (number == FailBeforeCommitInvoice && attempt == 1)
        {
            return Temporary();
        }

        var outcome = store.Approve(number, OperationId, time.GetUtcNow());
        if (number == CommitThenFailInvoice && attempt == 1 && outcome.Status == ApprovalStatus.Approved)
        {
            return Temporary();
        }

        return outcome.Status == ApprovalStatus.NotFound ? NotFound() : RedirectToPage(new { number, result = outcome.Status == ApprovalStatus.Approved ? "approved" : "already-approved" });
    }

    /// <summary>The error page shows only the error (like a server that lost its response), never the invoice state.</summary>
    private PageResult Temporary()
    {
        ErrorKind = "temporary";
        Notice = "Temporary service error. Please try again.";
        Response.Headers.RetryAfter = "1";
        var result = Page();
        result.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return result;
    }
}
