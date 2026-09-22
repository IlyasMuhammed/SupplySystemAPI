using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A tenant's sale orders and the purchase orders raised for their lines — written the way Demand writes them,
/// a PO linked to the order line it was bought for — and the margin analysis that reads them. In-memory, so it
/// shows what the service decides; that the queries also translate to SQL is the SQL Server tests' job.
/// </summary>
internal sealed class MarginWorld
{
    internal static readonly Guid Org      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid OtherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static readonly Guid AcmeId     = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid GlobexId   = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid StrangerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal readonly string DatabaseName = Guid.NewGuid().ToString();

    internal readonly Guid Pkr = Guid.NewGuid();
    internal readonly Guid Usd = Guid.NewGuid();

    internal Guid Acme   => AcmeId;
    internal Guid Globex => GlobexId;

    /// <summary>What the customer lookup knows. A customer absent from it has no name to show.</summary>
    internal readonly Dictionary<Guid, string> Names = new() { [AcmeId] = "Acme Ltd", [GlobexId] = "Globex Corp" };

    /// <summary>What the catalog knows. An order in a currency absent from it has no code.</summary>
    internal readonly List<CurrencyModel> Currencies = [];

    /// <summary>What Inventory knows: a variant's product and names. A variant absent from it has no product to be part of.</summary>
    internal readonly Dictionary<Guid, VariantDescription> Variants = new();

    internal string? CompanyName { get; set; } = "Northwind Trading";

    /// <summary>Set to point the world at another store, such as a real SQL Server database, instead of the in-memory one.</summary>
    internal Func<Guid, DemandDbContext>? ContextFactory { get; set; }

    private int _sequence;

    internal MarginWorld()
    {
        Currencies.Add(new CurrencyModel { Id = Pkr, Name = "Pakistani Rupee", Code = "PKR" });
        Currencies.Add(new CurrencyModel { Id = Usd, Name = "US Dollar", Code = "USD" });
    }

    internal DemandDbContext Db(Guid? org = null) =>
        ContextFactory?.Invoke(org ?? Org)
        ?? new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(DatabaseName).Options,
            new StaticTenantContext { OrganizationId = org ?? Org });

    internal MarginAnalysisReportService Service(Guid? org = null, Mock<IProductVariantResolver>? resolver = null) => Service(Db(org), resolver);

    internal MarginAnalysisReportService Service(DemandDbContext db, Mock<IProductVariantResolver>? resolver = null)
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(() => [.. Currencies]);

        resolver ??= new Mock<IProductVariantResolver>();
        resolver.Setup(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                    (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(Variants.ContainsKey).ToDictionary(id => id, id => Variants[id]));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new MarginAnalysisReportService(db, names.Object, lookups.Object, resolver.Object, templates.Object, new FixedClock());
    }

    /// <summary>A variant Inventory can name, as a variant of <paramref name="product"/>.</summary>
    internal void Catalog(Guid variant, Guid product, string productName) =>
        Variants[variant] = new VariantDescription(variant, product, "SKU-" + variant.ToString("N")[..6], "Default", productName, true, "PCS");

    /// <summary>What was bought for an order line: one purchase order of one line, unless said to be something that must not count.</summary>
    /// <param name="Status">The purchase order's status.</param>
    /// <param name="Deleted">A purchase order that was deleted.</param>
    /// <param name="OtherVariant">A purchase order whose line is for some other variant.</param>
    /// <param name="OtherLine">A purchase order raised for some other order line.</param>
    internal sealed record Buy(decimal Quantity, decimal Price, string Status = "APPROVED", bool Deleted = false, bool OtherVariant = false, bool OtherLine = false);

    /// <summary>What an order line sold: <paramref name="Buys"/> is the purchase orders raised for it, none for a line filled from stock.</summary>
    internal sealed record Sold(Guid Variant, decimal Quantity, decimal Price, decimal Discount = 0m, string Status = "OPEN", IReadOnlyList<Buy>? Buys = null);

    /// <summary>An open line with no discount; anything else is a <see cref="Sold"/> built by hand.</summary>
    internal static Sold Line(Guid variant, decimal quantity, decimal price, params Buy[] buys) =>
        new(variant, quantity, price, Buys: buys);

    /// <summary>An order with its lines, and the purchase orders raised for them, saved.</summary>
    internal SaleOrder Order(
        string number, DateTime orderDate, IReadOnlyList<Sold> lines, Guid? partner = null, Guid? currency = null,
        string status = "CONFIRMED", bool deleted = false, Guid? org = null)
    {
        var orgId = org ?? Org;
        var order = new SaleOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = orgId, SoNumber = number, PartnerId = partner ?? Acme,
            OrderDate = orderDate, CurrencyId = currency ?? Pkr, Status = status, DeliveryMode = "SHIP", IsDeleted = deleted, CreatedBy = 1
        };

        foreach (var l in lines)
            order.Lines.Add(new SaleOrderLine
            {
                UUID = Guid.NewGuid(), OrganizationId = orgId, VariantUuid = l.Variant, Quantity = l.Quantity, UnitPrice = l.Price,
                DiscountPercent = l.Discount, LineTotal = Math.Round(l.Quantity * l.Price * (1 - l.Discount / 100m), 2), Status = l.Status
            });

        using var db = Db(orgId);
        db.SaleOrders.Add(order);
        db.SaveChanges();

        var orderLines = order.Lines.ToList();
        for (var i = 0; i < lines.Count; i++)
            foreach (var buy in lines[i].Buys ?? [])
                AddPurchaseOrder(db, order, orderLines[i], lines[i].Variant, buy);
        db.SaveChanges();

        return order;
    }

    private void AddPurchaseOrder(DemandDbContext db, SaleOrder order, SaleOrderLine line, Guid variant, Buy buy)
    {
        var n = Interlocked.Increment(ref _sequence);
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = order.TraceId, OrganizationId = order.OrganizationId, PoNumber = $"PO-2026-{n:00000}",
            SupplierId = Guid.NewGuid(), SupplierName = "Supplier", Status = buy.Status, TotalAmount = buy.Quantity * buy.Price,
            IsDelete = buy.Deleted, Source = "BACK_TO_BACK", LinkedSoId = order.Id,
            LinkedSoLineId = buy.OtherLine ? line.Id + 100_000 : line.Id, CreatedBy = 1, CreatedDate = order.OrderDate
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            UUID = Guid.NewGuid(), OrganizationId = order.OrganizationId, LineNo = 1,
            VariantUuid = buy.OtherVariant ? Guid.NewGuid() : variant, ItemDescription = "Item", Quantity = buy.Quantity,
            UnitPrice = buy.Price, LineTotal = buy.Quantity * buy.Price
        });
        db.PurchaseOrders.Add(po);
    }

    /// <summary>
    /// Sixty orders from a fixed pseudo-random hand: three customers (one the lookup does not know), two
    /// currencies and one the catalog no longer knows, every status, some deleted and some another
    /// organization's, one to four lines each over variants of three products (and two that Inventory cannot
    /// name), a fifth of them cancelled, and for each line no purchase order, one, or a split of two or three —
    /// some cancelled, rejected, deleted, for another variant or another line, some bought in fewer units than
    /// the line and some in more. Identical in every world that seeds it, so two worlds can be compared.
    /// </summary>
    internal void SeedScatter()
    {
        var random   = new Random(20260923);
        var variants = Enumerable.Range(1, 7).Select(i => Guid.Parse($"d0000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var products = Enumerable.Range(1, 3).Select(i => Guid.Parse($"e0000000-0000-0000-0000-{i:000000000000}")).ToArray();

        Catalog(variants[0], products[0], "Laptop");
        Catalog(variants[1], products[0], "Laptop");
        Catalog(variants[2], products[1], "Mouse");
        Catalog(variants[3], products[2], "Cable");
        Catalog(variants[4], products[2], "Cable");
        // variants[5] and [6] are ones Inventory cannot name.

        string[] statuses  = ["DRAFT", "CONFIRMED", "PARTIALLY_FULFILLED", "FULFILLED", "INVOICED", "CLOSED", "CANCELLED", "CONFIRMED", "CONFIRMED"];
        string[] poStatus  = ["DRAFT", "APPROVED", "SENT", "PARTIALLY_RECEIVED", "RECEIVED", "CANCELLED", "REJECTED", "APPROVED"];
        string[] lineStatus = ["OPEN", "RESERVED", "OPEN", "FULFILLED", "CANCELLED", "OPEN"];
        Guid[]   partners  = [Acme, Globex, StrangerId];
        Guid[]   currencies = [Pkr, Usd, Guid.Parse("99999999-9999-9999-9999-999999999999")];
        decimal[] discounts = [0m, 0m, 5m, 12.5m];

        for (var i = 0; i < 60; i++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            {
                var quantity = random.Next(4, 200) / 4m;
                var buys = Enumerable.Range(0, random.Next(0, 4) == 3 ? 3 : random.Next(0, 3)).Select(_ => new Buy(
                    Quantity:     Math.Max(0.25m, Math.Round(quantity * random.Next(10, 130) / 100m / 2m, 2)),
                    Price:        random.Next(50, 60000) / 100m, // a purchase order line's price is held to the cent
                    Status:       poStatus[random.Next(poStatus.Length)],
                    Deleted:      random.Next(0, 12) == 0,
                    OtherVariant: random.Next(0, 15) == 0,
                    OtherLine:    random.Next(0, 15) == 0)).ToList();

                return new Sold(variants[random.Next(variants.Length)], quantity, random.Next(100, 90000) / 100m,
                    discounts[random.Next(discounts.Length)], lineStatus[random.Next(lineStatus.Length)], buys);
            }).ToList();

            Order($"SO-2026-{i + 1:00000}", new DateTime(2026, 8, 1).AddDays(random.Next(0, 60)).AddHours(random.Next(0, 24)), lines,
                partners[i % 3], currencies[i % 5 == 4 ? 2 : i % 3 == 1 ? 1 : 0], statuses[i % 9], deleted: i % 13 == 12,
                org: i % 17 == 15 ? OtherOrg : null);
        }
    }

    internal static DateTime D(int month, int day) => new(2026, month, day);
}
