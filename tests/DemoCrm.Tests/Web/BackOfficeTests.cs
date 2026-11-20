using System.Net;
using DemoCrm.BackOffice;
using DemoCrm.Tests.Support;

namespace DemoCrm.Tests.Web;

/// <summary>Phase 9 fixture extension: the order desk and the invoice approval queue.</summary>
public sealed class BackOfficeTests : IDisposable
{
    private const string SeededCustomer = "alice.morgan@example.test";
    private readonly DemoCrmApp _app = new();

    public void Dispose() => _app.Dispose();

    private static Dictionary<string, string> Order(string sku = "WIDGET-S", string quantity = "2", string po = "PO-1", string? operationId = null, string email = SeededCustomer) => new()
    {
        ["Input.CustomerEmail"] = email,
        ["Input.Sku"] = sku,
        ["Input.Quantity"] = quantity,
        ["Input.Shipping"] = "Standard",
        ["Input.PurchaseOrder"] = po,
        ["Input.OperationId"] = operationId ?? Guid.NewGuid().ToString("D"),
    };

    [Fact]
    public async Task An_order_is_placed_once_per_reference_and_found_by_it()
    {
        await _app.LoginSuccessfullyAsync();
        var fields = Order();
        var first = await _app.PostFormAsync("/Orders/New", fields);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var confirmation = await _app.GetHtmlAsync(first.Headers.Location!.OriginalString);
        Assert.Equal("created", DemoCrmApp.Attribute(confirmation, "order-confirmation", "data-result"));
        Assert.Equal("30.00", DemoCrmApp.Attribute(confirmation, "order-total", "data-amount")); // 2 x 12.50 + 5.00 shipping
        var number = DemoCrmApp.Text(confirmation, "order-number");

        // Same reference again: replayed, nothing new, stock unchanged.
        var replay = await _app.PostFormAsync("/Orders/New", fields);
        Assert.Equal("replayed", DemoCrmApp.Attribute(await _app.GetHtmlAsync(replay.Headers.Location!.OriginalString), "order-confirmation", "data-result"));
        var state = await _app.GetStateAsync();
        Assert.Equal((1, 2), (state.OrderCount, state.OrderSubmissions));

        var search = await _app.GetHtmlAsync("/Orders/Search?Reference=" + fields["Input.OperationId"]);
        Assert.Equal(number, DemoCrmApp.Attribute(search, "order-search-row", "data-order-number"));
        Assert.True(DemoCrmApp.Has(await _app.GetHtmlAsync("/Orders/Search?Reference=" + Guid.NewGuid()), "order-search-empty"));
    }

    [Theory]
    [InlineData("nobody@example.test", "WIDGET-S", "1", "unknown-customer")]
    [InlineData(SeededCustomer, "GADGET-X", "4", "out-of-stock")]
    [InlineData(SeededCustomer, "WIDGET-S", "0", "validation")]
    public async Task Invalid_orders_are_refused_with_a_machine_readable_kind(string email, string sku, string quantity, string kind)
    {
        await _app.LoginSuccessfullyAsync();
        var response = await _app.PostFormAsync("/Orders/New", Order(sku, quantity, email: email));
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(kind, DemoCrmApp.Attribute(await response.Content.ReadAsStringAsync(), "order-error", "data-error-kind"));
        Assert.Equal(0, (await _app.GetStateAsync()).OrderCount);
    }

    [Fact]
    public async Task A_commit_then_fail_order_is_stored_although_the_client_saw_an_error()
    {
        await _app.LoginSuccessfullyAsync();
        var fields = Order(po: "PO-9+commit-fail");
        var first = await _app.PostFormAsync("/Orders/New", fields);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(1, (await _app.GetStateAsync()).OrderCount);
        Assert.Equal(HttpStatusCode.OK, (await _app.GetAsync("/Orders/Search?Reference=" + fields["Input.OperationId"])).StatusCode);
    }

    [Fact]
    public async Task An_invoice_is_approved_once_with_its_reference_recorded()
    {
        await _app.LoginSuccessfullyAsync();
        var page = await _app.GetHtmlAsync("/Invoices?Number=INV-1001");
        Assert.Equal(("1250.00", "Pending"), (DemoCrmApp.Attribute(page, "invoice-row", "data-amount"), DemoCrmApp.Attribute(page, "invoice-row", "data-status")));

        var operation = Guid.NewGuid().ToString("D");
        var approved = await _app.PostFormAsync("/Invoices?handler=Approve", new Dictionary<string, string> { ["Number"] = "INV-1001", ["OperationId"] = operation }, tokenFromPath: "/Invoices?Number=INV-1001");
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var after = await _app.GetHtmlAsync("/Invoices?Number=INV-1001");
        Assert.Equal(("Approved", operation), (DemoCrmApp.Attribute(after, "invoice-row", "data-status"), DemoCrmApp.Attribute(after, "invoice-row", "data-approval-operation")));
        Assert.StartsWith("APR-", DemoCrmApp.Text(after, "approval-reference"), StringComparison.Ordinal);
        Assert.False(DemoCrmApp.Has(after, "approve-invoice")); // nothing left to approve
        Assert.Equal(1, (await _app.GetStateAsync()).ApprovalCount);
    }

    [Fact]
    public async Task Invoice_failure_scenarios_fail_before_and_after_commit_exactly_once()
    {
        await _app.LoginSuccessfullyAsync();
        var before = await _app.PostFormAsync("/Invoices?handler=Approve", new Dictionary<string, string> { ["Number"] = "INV-1006", ["OperationId"] = Guid.NewGuid().ToString("D") }, tokenFromPath: "/Invoices?Number=INV-1006");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        Assert.Equal("Pending", DemoCrmApp.Attribute(await _app.GetHtmlAsync("/Invoices?Number=INV-1006"), "invoice-row", "data-status"));

        var after = await _app.PostFormAsync("/Invoices?handler=Approve", new Dictionary<string, string> { ["Number"] = "INV-1007", ["OperationId"] = Guid.NewGuid().ToString("D") }, tokenFromPath: "/Invoices?Number=INV-1007");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, after.StatusCode);
        Assert.Equal("Approved", DemoCrmApp.Attribute(await _app.GetHtmlAsync("/Invoices?Number=INV-1007"), "invoice-row", "data-status"));

        Assert.True(DemoCrmApp.Has(await _app.GetHtmlAsync("/Invoices?Number=INV-9999"), "invoice-not-found"));
    }

    [Fact]
    public async Task Reset_restores_stock_invoices_and_counters()
    {
        await _app.LoginSuccessfullyAsync();
        await _app.PostFormAsync("/Orders/New", Order("GADGET-X", "3"));
        await _app.PostFormAsync("/Invoices?handler=Approve", new Dictionary<string, string> { ["Number"] = "INV-1002" }, tokenFromPath: "/Invoices?Number=INV-1002");
        Assert.Equal(HttpStatusCode.NoContent, (await _app.ControlResetAsync(DemoCrmApp.ControlToken)).StatusCode);

        var state = await _app.GetStateAsync();
        Assert.Equal((0, 0, 0, 0), (state.OrderCount, state.OrderSubmissions, state.ApprovalCount, state.ApprovalSubmissions));
        Assert.Equal("Pending", DemoCrmApp.Attribute(await _app.GetHtmlAsync("/Invoices?Number=INV-1002"), "invoice-row", "data-status"));
        var store = new BackOfficeStore();
        Assert.Equal(3, store.Stock("GADGET-X"));
    }
}
