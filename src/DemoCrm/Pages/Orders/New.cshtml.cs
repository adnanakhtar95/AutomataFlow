using DemoCrm.BackOffice;
using DemoCrm.Configuration;
using DemoCrm.Customers;
using DemoCrm.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace DemoCrm.Pages.Orders;

public sealed class OrderInput
{
    public string CustomerEmail { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public string Quantity { get; set; } = "1";

    public string Shipping { get; set; } = "Standard";

    /// <summary>The customer's purchase order reference. A plus-tag selects a simulated failure (PO-7+commit-fail).</summary>
    public string PurchaseOrder { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;
}

/// <summary>Order entry (Phase 9 fixture extension): idempotent by operation reference, stock-checked, failure scenarios by PO plus-tag.</summary>
public sealed class NewModel(BackOfficeStore store, ICustomerStore customers, FailureSimulator simulator, OrderCounter counter, IOptions<DemoCrmOptions> options, TimeProvider time) : PageModel
{
    [BindProperty]
    public OrderInput Input { get; set; } = new();

    /// <summary>validation, unknown-customer, out-of-stock, operation-conflict or temporary.</summary>
    public string? ErrorKind { get; private set; }

    public string? ErrorMessage { get; private set; }

    public void OnGet() => Input.OperationId = Guid.NewGuid().ToString("D");

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        counter.Increment();
        ModelState.Clear();
        if (string.IsNullOrWhiteSpace(Input.CustomerEmail) || string.IsNullOrWhiteSpace(Input.Sku) || string.IsNullOrWhiteSpace(Input.PurchaseOrder)
            || !int.TryParse(Input.Quantity, out var quantity) || (Input.OperationId.Length > 0 && !Guid.TryParse(Input.OperationId, out _)))
        {
            return Fail(StatusCodes.Status422UnprocessableEntity, "validation", "Customer, product, quantity and purchase order are required.");
        }

        var scenario = options.Value.Simulation.Enabled ? FailureScenario.Parse(Input.PurchaseOrder) : FailureScenario.None;
        var attempt = scenario.Kind == FailureScenarioKind.None ? 0 : simulator.RecordAttempt("order:" + Input.PurchaseOrder);
        if (scenario.Kind == FailureScenarioKind.FailBeforeCommit && attempt <= scenario.FailCount)
        {
            return Temporary();
        }

        if (scenario.Kind == FailureScenarioKind.Slow)
        {
            await Task.Delay(options.Value.Simulation.SlowDelayMs, cancellationToken);
        }

        var customerExists = customers.Search(CustomerSearchField.Email, Input.CustomerEmail.Trim()).Count > 0;
        var outcome = store.PlaceOrder(new NewOrder(Input.CustomerEmail.Trim(), Input.Sku.Trim(), quantity, Input.Shipping, Input.PurchaseOrder),
            Input.OperationId, customerExists, time.GetUtcNow());

        if (scenario.Kind == FailureScenarioKind.CommitThenFail && attempt == 1 && outcome.Status == OrderStatus.Created)
        {
            return Temporary();
        }

        return outcome.Status switch
        {
            OrderStatus.Created => RedirectToPage("Confirmation", new { id = outcome.Order!.Id, result = "created" }),
            OrderStatus.Replayed => RedirectToPage("Confirmation", new { id = outcome.Order!.Id, result = "replayed" }),
            OrderStatus.UnknownCustomer => Fail(StatusCodes.Status422UnprocessableEntity, "unknown-customer", "No customer is registered with this e-mail address."),
            OrderStatus.UnknownProduct => Fail(StatusCodes.Status422UnprocessableEntity, "validation", "Unknown product or shipping method."),
            OrderStatus.InvalidQuantity => Fail(StatusCodes.Status422UnprocessableEntity, "validation", "Quantity must be between 1 and 100."),
            OrderStatus.OutOfStock => Fail(StatusCodes.Status409Conflict, "out-of-stock", "Not enough stock for this product."),
            _ => Fail(StatusCodes.Status409Conflict, "operation-conflict", "Order reference conflict: it was already used with different order data."),
        };
    }

    private PageResult Temporary()
    {
        Response.Headers.RetryAfter = "1";
        return Fail(StatusCodes.Status503ServiceUnavailable, "temporary", "Temporary service error. Please try again.");
    }

    private PageResult Fail(int status, string kind, string message)
    {
        ErrorKind = kind;
        ErrorMessage = message;
        var result = Page();
        result.StatusCode = status;
        return result;
    }
}
