using System.Globalization;

namespace DemoCrm.BackOffice;

/// <summary>A catalog item of the fictional shop.</summary>
public sealed record CatalogItem(string Sku, string Name, decimal UnitPrice, int InitialStock);

public sealed record Order(int Id, string Number, string OperationId, string CustomerEmail, string Sku, int Quantity, string Shipping, string PurchaseOrder, decimal Total, DateTimeOffset CreatedAt);

public enum OrderStatus
{
    Created,
    Replayed,
    OperationConflict,
    UnknownCustomer,
    UnknownProduct,
    InvalidQuantity,
    OutOfStock,
}

public sealed record OrderOutcome(OrderStatus Status, Order? Order = null);

public sealed record NewOrder(string CustomerEmail, string Sku, int Quantity, string Shipping, string PurchaseOrder);

public sealed record Invoice(string Number, string Supplier, decimal Amount, string Currency, bool Approved, string? ApprovalReference, string? ApprovalOperationId, DateTimeOffset? ApprovedAt);

public enum ApprovalStatus
{
    Approved,
    AlreadyApproved,
    NotFound,
}

public sealed record ApprovalOutcome(ApprovalStatus Status, Invoice? Invoice = null);

/// <summary>
/// The fictional back office of DemoCrm (test fixture, ADR-027): an order desk and an invoice approval queue, so that
/// workflow packages beyond customer registration can be verified against a controlled target (Phase 9). One lock makes
/// every operation atomic; orders are idempotent by operation reference like registrations; approving an invoice is a
/// state transition that can happen only once.
/// </summary>
public sealed class BackOfficeStore
{
    public static readonly IReadOnlyList<CatalogItem> Catalog =
    [
        new("WIDGET-S", "Widget, small", 12.50m, 1_000),
        new("WIDGET-L", "Widget, large", 39.00m, 1_000),
        new("GADGET-X", "Gadget X (limited)", 99.00m, 3),
    ];

    public static readonly IReadOnlyDictionary<string, decimal> ShippingRates = new Dictionary<string, decimal>(StringComparer.Ordinal)
    {
        ["Standard"] = 5.00m,
        ["Express"] = 15.00m,
    };

    private readonly Lock _gate = new();
    private readonly List<Order> _orders = [];
    private readonly Dictionary<string, Order> _ordersByOperation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _stock = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Invoice> _invoices = new(StringComparer.OrdinalIgnoreCase);
    private int _approvals;

    public BackOfficeStore() => Reset();

    public int OrderCount
    {
        get
        {
            lock (_gate)
            {
                return _orders.Count;
            }
        }
    }

    public int ApprovalCount
    {
        get
        {
            lock (_gate)
            {
                return _approvals;
            }
        }
    }

    public OrderOutcome PlaceOrder(NewOrder order, string? operationId, bool customerExists, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        lock (_gate)
        {
            var key = string.IsNullOrWhiteSpace(operationId) ? null : operationId.Trim();
            if (key is not null && _ordersByOperation.TryGetValue(key, out var original))
            {
                var same = string.Equals(original.CustomerEmail, order.CustomerEmail, StringComparison.OrdinalIgnoreCase)
                    && original.Sku == order.Sku && original.Quantity == order.Quantity;
                return new OrderOutcome(same ? OrderStatus.Replayed : OrderStatus.OperationConflict, original);
            }

            if (!customerExists)
            {
                return new OrderOutcome(OrderStatus.UnknownCustomer);
            }

            var item = Catalog.FirstOrDefault(c => c.Sku == order.Sku);
            if (item is null || !ShippingRates.TryGetValue(order.Shipping, out var shipping))
            {
                return new OrderOutcome(OrderStatus.UnknownProduct);
            }

            if (order.Quantity is < 1 or > 100)
            {
                return new OrderOutcome(OrderStatus.InvalidQuantity);
            }

            if (_stock[item.Sku] < order.Quantity)
            {
                return new OrderOutcome(OrderStatus.OutOfStock);
            }

            _stock[item.Sku] -= order.Quantity;
            var id = _orders.Count + 1;
            var created = new Order(id, $"SO-{10_000 + id}", key ?? Guid.NewGuid().ToString("D"), order.CustomerEmail.Trim().ToLowerInvariant(), item.Sku, order.Quantity, order.Shipping,
                order.PurchaseOrder.Trim(), (item.UnitPrice * order.Quantity) + shipping, now);
            _orders.Add(created);
            _ordersByOperation[created.OperationId] = created;
            return new OrderOutcome(OrderStatus.Created, created);
        }
    }

    public Order? FindOrder(int id)
    {
        lock (_gate)
        {
            return _orders.FirstOrDefault(o => o.Id == id);
        }
    }

    public Order? FindOrderByOperation(string operationId)
    {
        lock (_gate)
        {
            return _ordersByOperation.GetValueOrDefault(operationId.Trim());
        }
    }

    public int Stock(string sku)
    {
        lock (_gate)
        {
            return _stock.GetValueOrDefault(sku);
        }
    }

    public Invoice? FindInvoice(string number)
    {
        lock (_gate)
        {
            return _invoices.GetValueOrDefault(number.Trim());
        }
    }

    public ApprovalOutcome Approve(string number, string? operationId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_invoices.TryGetValue(number.Trim(), out var invoice))
            {
                return new ApprovalOutcome(ApprovalStatus.NotFound);
            }

            if (invoice.Approved)
            {
                return new ApprovalOutcome(ApprovalStatus.AlreadyApproved, invoice);
            }

            _approvals++;
            var approved = invoice with
            {
                Approved = true,
                ApprovalReference = $"APR-{5000 + _approvals}",
                ApprovalOperationId = string.IsNullOrWhiteSpace(operationId) ? null : operationId.Trim(),
                ApprovedAt = now,
            };
            _invoices[approved.Number] = approved;
            return new ApprovalOutcome(ApprovalStatus.Approved, approved);
        }
    }

    /// <summary>Discards all orders and approvals and restores the deterministic catalog stock and invoice queue.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _orders.Clear();
            _ordersByOperation.Clear();
            _stock.Clear();
            foreach (var item in Catalog)
            {
                _stock[item.Sku] = item.InitialStock;
            }

            _invoices.Clear();
            _approvals = 0;
            var approvedAt = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
            Invoice[] seed =
            [
                new("INV-1001", "Northwind Paper Co.", 1250.00m, "EUR", false, null, null, null),
                new("INV-1002", "Contoso Cleaning", 480.40m, "EUR", false, null, null, null),
                new("INV-1003", "Fabrikam Logistics", 3999.99m, "EUR", true, "APR-4999", null, approvedAt),
                new("INV-1004", "Adatum Office Supplies", 75.10m, "EUR", false, null, null, null),
                new("INV-1005", "Litware Software", 2200.00m, "EUR", false, null, null, null),
                new("INV-1006", "Tailspin Catering", 315.75m, "EUR", false, null, null, null),
                new("INV-1007", "Wide World Importers", 912.00m, "EUR", false, null, null, null),
                new("INV-1008", "Proseware Consulting", 6400.00m, "EUR", false, null, null, null),
            ];
            foreach (var invoice in seed)
            {
                _invoices[invoice.Number] = invoice;
            }
        }
    }

    /// <summary>Amounts as shown and as attributes: invariant culture, two decimals (e.g. 1250.00).</summary>
    public static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
