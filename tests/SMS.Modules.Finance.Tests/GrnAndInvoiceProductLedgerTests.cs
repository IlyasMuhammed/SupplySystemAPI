using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-03 §11.2/§11.3, TC-10 — the two places the product ledger is fed from, on a real SQL Server with
/// the real modules over it: a GRN's stock posting books a PURCHASE at the purchase order line's price in the
/// stock's own transaction, and an issued invoice books a SALE at the weighted-average cost.
/// </summary>
public class GrnAndInvoiceProductLedgerTests
{
    private const int Approver = 7;

    /// <summary>A catalogue variant, a warehouse, and a purchase order priced per line.</summary>
    private sealed class Catalogue
    {
        public required Guid Org;
        public required Guid ProductUuid;
        public required Guid VariantUuid;
        public required Guid WarehouseUuid;
        public required Guid SupplierId;
        public required PurchaseOrder Po;
        public required FakeVariants Variants;
    }

    private static async Task<Catalogue> SeedAsync(FinanceSqlServerHarness h, params decimal[] linePrices)
    {
        var org = Guid.NewGuid();
        var (productUuid, variantUuid, warehouseUuid, supplier) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using (var inv = h.NewInventoryContext(org))
        {
            var product = new Product { Uuid = productUuid, Sku = "LAP-001", Name = "Business Laptop", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
            inv.Products.Add(product);
            inv.Warehouses.Add(new InventoryWarehouse { Uuid = warehouseUuid, Code = "WH1", Name = "Main Warehouse", IsActive = true, CreatedBy = 1 });
            await inv.SaveChangesAsync();

            inv.ProductVariants.Add(new ProductVariant
            {
                Uuid = variantUuid, ProductId = product.Id, Sku = "LAP-001-DEFAULT", VariantName = "Default",
                PurchasePrice = 1m, IsDefault = true, IsActive = true, CreatedBy = 1
            });
            await inv.SaveChangesAsync();
        }

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00001", Title = "Laptops", SupplierId = supplier, SupplierName = "Lenovo Distributors",
            Status = "SENT", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        var lineNo = 1;
        foreach (var price in linePrices)
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = lineNo++, ItemDescription = "Business Laptop", UnitOfMeasure = "PC",
                Quantity = 100m, UnitPrice = price, LineTotal = 100m * price, VariantUuid = variantUuid
            });
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);

        await using (var demand = h.NewDemandContext(org))
        {
            demand.PurchaseOrders.Add(po);
            await demand.SaveChangesAsync();
        }

        // Inventory is not asked which product the variant belongs to: the poster already knows, and says.
        var variants = new FakeVariants();

        return new Catalogue
        {
            Org = org, ProductUuid = productUuid, VariantUuid = variantUuid, WarehouseUuid = warehouseUuid,
            SupplierId = supplier, Po = po, Variants = variants
        };
    }

    private static GrnLine Line(Catalogue c, int poLineIndex, decimal received, decimal? grnUnitCost = null, decimal rejected = 0m, bool inspected = false) => new()
    {
        UUID = Guid.NewGuid(), PoLineUuid = poLineIndex < c.Po.Lines.Count ? c.Po.Lines.ElementAt(poLineIndex).UUID : Guid.NewGuid(),
        VariantUuid = c.VariantUuid, LineNo = poLineIndex + 1, ItemDescription = "Business Laptop", UnitOfMeasure = "PC",
        QtyOrdered = 100m, QtyReceived = received, QtyAccepted = inspected ? received - rejected : received, QtyRejected = rejected,
        RequiresInspection = inspected, UnitCost = grnUnitCost
    };

    private static Grn Receipt(Catalogue c, params GrnLine[] lines) => new()
    {
        UUID = Guid.NewGuid(), GrnNumber = "GRN-2026-00001", PoUuid = c.Po.UUID, PoNumber = c.Po.PoNumber,
        SupplierId = c.SupplierId, SupplierName = "Lenovo Distributors", WarehouseUuid = c.WarehouseUuid,
        ReceivedAt = DateTime.UtcNow, Status = "PENDING_APPROVAL", ReceivedBy = 1, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
        Lines = lines.ToList()
    };

    /// <summary>The poster as production wires it: one request's contexts, the master and product ledgers on one Finance context.</summary>
    private static async Task PostAsync(FinanceSqlServerHarness h, Catalogue c, Grn grn)
    {
        await using var fin = h.NewContext(c.Org);
        await using var inv = h.NewInventoryContext(c.Org);
        await using var demand = h.NewDemandContext(c.Org);

        var inventoryLedger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(fin));
        var poster = new EfGrnInventoryPoster(inv, inventoryLedger, new ProductLedgerService(fin, c.Variants), demand);

        await poster.PostToInventoryAsync(grn, Approver);
    }

    private static async Task<List<ProductLedgerEntry>> ProductLedgerAsync(FinanceSqlServerHarness h, Catalogue c)
    {
        await using var fin = h.NewContext(c.Org);
        return await fin.ProductLedgerEntries.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync();
    }

    // ── GRN approved: PURCHASE ───────────────────────────────────────────────

    [FinanceSqlServerFact]
    public async Task A_grn_of_100_at_a_po_price_of_10_books_a_purchase_at_10_whatever_the_grn_line_says()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m);
        var grn = Receipt(c, Line(c, 0, received: 100m, grnUnitCost: 99m));    // the GRN line's own cost differs

        await PostAsync(h, c, grn);

        var purchase = (await ProductLedgerAsync(h, c)).Should().ContainSingle().Subject;
        purchase.EntryType.Should().Be("PURCHASE");
        purchase.Direction.Should().Be("IN");
        (purchase.Quantity, purchase.UnitCost, purchase.TotalCost, purchase.RunningQty, purchase.RunningValue)
            .Should().Be((100m, 10m, 1000m, 100m, 1000m), "cost = the purchase order line's price");
        purchase.VariantUuid.Should().Be(c.VariantUuid);
        purchase.ProductUuid.Should().Be(c.ProductUuid);
        purchase.ReferenceType.Should().Be("GRN");
        purchase.ReferenceId.Should().Be(grn.UUID);
        purchase.ReferenceNumber.Should().Be("GRN-2026-00001");
        purchase.PartnerId.Should().Be(c.SupplierId);
        purchase.CreatedBy.Should().Be(Approver);
        purchase.OrganizationId.Should().Be(c.Org);
        purchase.Narration.Should().Contain("Business Laptop");

        // And the stock itself went in, alongside.
        await using var inv = h.NewInventoryContext(c.Org);
        (await inv.InventoryItems.SingleAsync()).QtyOnHand.Should().Be(100m);
        await using var fin = h.NewContext(c.Org);
        (await fin.MasterProductLedgers.CountAsync()).Should().Be(1, "the existing master ledger is fed exactly as before");
    }

    [FinanceSqlServerFact]
    public async Task A_grn_line_with_no_cost_of_its_own_is_still_bought_at_the_po_price()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 12.5m);

        await PostAsync(h, c, Receipt(c, Line(c, 0, received: 4m, grnUnitCost: null)));

        (await ProductLedgerAsync(h, c)).Single().Should().Match<ProductLedgerEntry>(e => e.UnitCost == 12.5m && e.TotalCost == 50m);
    }

    [FinanceSqlServerFact]
    public async Task Only_what_reaches_stock_is_booked_a_rejected_quantity_is_not_and_a_fully_rejected_line_books_nothing()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m, 10m);

        await PostAsync(h, c, Receipt(c,
            Line(c, 0, received: 100m, rejected: 30m, inspected: true),      // 70 accepted
            Line(c, 1, received: 20m,  rejected: 20m, inspected: true)));    // none accepted

        var entries = await ProductLedgerAsync(h, c);
        entries.Should().ContainSingle().Which.Quantity.Should().Be(70m);
        await using var inv = h.NewInventoryContext(c.Org);
        (await inv.InventoryItems.SingleAsync()).QtyOnHand.Should().Be(70m, "the ledger books exactly the quantity the stock received");
    }

    [FinanceSqlServerFact]
    public async Task Two_receipts_of_one_variant_at_different_prices_blend_into_a_weighted_average()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m, 16m);

        await PostAsync(h, c, Receipt(c, Line(c, 0, received: 100m), Line(c, 1, received: 50m)));

        var entries = await ProductLedgerAsync(h, c);
        entries.Select(e => (e.SequenceNo, e.Quantity, e.UnitCost, e.RunningQty, e.RunningValue)).Should().Equal(
            (1, 100m, 10m, 100m, 1000m),
            (2, 50m,  16m,  150m, 1800m));
        (entries[^1].RunningValue / entries[^1].RunningQty).Should().Be(12m);
    }

    [FinanceSqlServerFact]
    public async Task A_receipt_that_fails_partway_takes_the_product_ledger_stock_and_master_ledger_back_together()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m);

        // The second line names a purchase order line that does not exist, so the poster refuses it — after the
        // first line's stock, inventory ledger, master ledger and product ledger entry have all been written.
        var grn = Receipt(c, Line(c, 0, received: 100m), Line(c, 5, received: 10m));

        await using (var fin = h.NewContext(c.Org))
        await using (var inv = h.NewInventoryContext(c.Org))
        await using (var demand = h.NewDemandContext(c.Org))
        {
            var inventoryLedger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(fin));
            var poster = new EfGrnInventoryPoster(inv, inventoryLedger, new ProductLedgerService(fin, c.Variants), demand);

            var act = () => poster.PostToInventoryAsync(grn, Approver);
            (await act.Should().ThrowAsync<UnprocessableEntityException>()).Which.Message.Should().Contain("purchase order line");
        }

        await using var fin2 = h.NewContext(c.Org);
        await using var inv2 = h.NewInventoryContext(c.Org);
        (await fin2.ProductLedgerEntries.CountAsync()).Should().Be(0, "no PURCHASE without the stock it describes");
        (await fin2.MasterProductLedgers.CountAsync()).Should().Be(0);
        (await inv2.InventoryItems.CountAsync()).Should().Be(0);
        (await inv2.InventoryLedgerEntries.CountAsync()).Should().Be(0);
    }

    [FinanceSqlServerFact]
    public async Task The_product_ledger_joins_the_stock_transaction_even_from_a_context_the_master_ledger_did_not_enlist()
    {
        // In production one Finance context serves both ledgers, so the master ledger's write has already
        // put it in the transaction. Here the product ledger has a context of its own, which only the
        // transaction it is handed can bring in.
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m);
        var grn = Receipt(c, Line(c, 0, received: 100m), Line(c, 5, received: 10m));    // the second line fails

        await using (var masterFin = h.NewContext(c.Org))
        await using (var productFin = h.NewContext(c.Org))
        await using (var inv = h.NewInventoryContext(c.Org))
        await using (var demand = h.NewDemandContext(c.Org))
        {
            var inventoryLedger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(masterFin));
            var poster = new EfGrnInventoryPoster(inv, inventoryLedger, new ProductLedgerService(productFin, c.Variants), demand);

            await FluentActions.Awaiting(() => poster.PostToInventoryAsync(grn, Approver)).Should().ThrowAsync<UnprocessableEntityException>();
        }

        (await ProductLedgerAsync(h, c)).Should().BeEmpty("it was written in the stock's transaction, so it went when that rolled back");

        // And when nothing fails, it commits with the stock.
        await using (var masterFin = h.NewContext(c.Org))
        await using (var productFin = h.NewContext(c.Org))
        await using (var inv = h.NewInventoryContext(c.Org))
        await using (var demand = h.NewDemandContext(c.Org))
        {
            var inventoryLedger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(masterFin));
            var poster = new EfGrnInventoryPoster(inv, inventoryLedger, new ProductLedgerService(productFin, c.Variants), demand);
            await poster.PostToInventoryAsync(Receipt(c, Line(c, 0, received: 100m)), Approver);
        }

        (await ProductLedgerAsync(h, c)).Should().ContainSingle();
    }

    [FinanceSqlServerFact]
    public async Task A_receipt_posted_where_finance_is_not_wired_in_still_posts_its_stock()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m);

        await using (var fin = h.NewContext(c.Org))
        await using (var inv = h.NewInventoryContext(c.Org))
        {
            var inventoryLedger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(fin));
            await new EfGrnInventoryPoster(inv, inventoryLedger).PostToInventoryAsync(Receipt(c, Line(c, 0, received: 10m)), Approver);
        }

        await using var inv2 = h.NewInventoryContext(c.Org);
        (await inv2.InventoryItems.SingleAsync()).QtyOnHand.Should().Be(10m);
        (await ProductLedgerAsync(h, c)).Should().BeEmpty();
    }

    // ── TC-10, both halves ───────────────────────────────────────────────────

    [FinanceSqlServerFact]
    public async Task TC10_a_grn_of_100_at_10_then_a_sale_of_30_at_15_books_a_purchase_and_a_sale_with_a_wac_of_10_and_a_cogs_of_300()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m);
        var customer = Guid.NewGuid();

        await PostAsync(h, c, Receipt(c, Line(c, 0, received: 100m)));

        // A draft invoice for 30 at 15, as CreateFromFulfillment leaves one.
        var invoice = Receivables.Invoice(c.Org, customer, "SINV-20260921-0001", new DateTime(2026, 9, 21), 450m, status: "DRAFT");
        invoice.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), OrganizationId = c.Org, LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = c.VariantUuid,
            Description = "Business Laptop", Quantity = 30m, UnitPrice = 15m, LineTotal = 450m
        });
        await using (var seed = h.NewContext(c.Org))
        {
            seed.SalesInvoices.Add(invoice);
            await seed.SaveChangesAsync();
        }

        SalesInvoiceIssued issued;
        await using (var fin = h.NewContext(c.Org))
        await using (var demand = h.NewDemandContext(c.Org))
        {
            var names = new Mock<ISupplierNameLookupService>();
            var lookups = new Mock<ILookupsService>();
            lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = Guid.NewGuid(), Name = "Rupee", Code = "PKR" }]);

            var service = new SalesInvoiceService(
                fin, demand, Mock.Of<IDeliveryFulfillmentReader>(), new CustomerLedgerService(fin),
                new ProductLedgerService(fin, c.Variants), names.Object, lookups.Object,
                Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance);

            issued = await service.IssueAsync(invoice.UUID, Approver);
        }

        var entries = await ProductLedgerAsync(h, c);
        entries.Select(e => (e.EntryType, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", "IN",  100m, 10m, 1000m, 100m, 1000m),
            ("SALE",     "OUT", 30m,  10m, 300m,  70m,  700m));

        var revenue = 30m * 15m;
        var cogs    = entries[1].TotalCost;
        revenue.Should().Be(450m);
        cogs.Should().Be(300m);
        (entries[1].RunningValue / entries[1].RunningQty).Should().Be(10m, "WAC");
        entries[1].ReferenceId.Should().Be(invoice.UUID);

        issued.Status.Should().Be("ISSUED");
        await using var after = h.NewContext(c.Org);
        (await after.SalesInvoices.SingleAsync()).Status.Should().Be("ISSUED");
        (await after.CustomerLedgerEntries.SingleAsync()).DebitAmount.Should().Be(450m, "the customer owes the price, not the cost");
    }

    // ── A month of it, through the real hooks (A29-P8-06) ────────────────────

    private static int _invoiceNumber;

    /// <summary>A draft invoice of one line for <paramref name="variant"/>, seeded and then issued by the real service.</summary>
    private static async Task<SalesInvoiceIssued> SellAsync(FinanceSqlServerHarness h, Catalogue c, Guid variant, decimal qty, decimal price)
    {
        var number  = $"SINV-{Interlocked.Increment(ref _invoiceNumber):D8}";
        var invoice = Receivables.Invoice(c.Org, Guid.NewGuid(), number, new DateTime(2026, 9, 21), qty * price, status: "DRAFT");
        invoice.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), OrganizationId = c.Org, LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = variant,
            Description = "Business Laptop", Quantity = qty, UnitPrice = price, LineTotal = qty * price
        });
        await using (var seed = h.NewContext(c.Org))
        {
            seed.SalesInvoices.Add(invoice);
            await seed.SaveChangesAsync();
        }

        await using var fin = h.NewContext(c.Org);
        await using var demand = h.NewDemandContext(c.Org);
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = Guid.NewGuid(), Name = "Rupee", Code = "PKR" }]);

        return await new SalesInvoiceService(
            fin, demand, Mock.Of<IDeliveryFulfillmentReader>(), new CustomerLedgerService(fin),
            new ProductLedgerService(fin, c.Variants), new Mock<ISupplierNameLookupService>().Object, lookups.Object,
            Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance).IssueAsync(invoice.UUID, Approver);
    }

    [FinanceSqlServerFact]
    public async Task Three_grns_at_three_po_prices_with_sales_between_them_cost_every_sale_at_the_average_of_its_moment()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var c = await SeedAsync(h, 10m, 20m, 10m);                     // three purchase order lines, three prices

        await PostAsync(h, c, Receipt(c, Line(c, 0, received: 100m)));
        await SellAsync(h, c, c.VariantUuid, 20m, 40m);
        await PostAsync(h, c, Receipt(c, Line(c, 1, received: 100m)));
        await SellAsync(h, c, c.VariantUuid, 30m, 25m);
        await PostAsync(h, c, Receipt(c, Line(c, 2, received: 50m)));
        await SellAsync(h, c, c.VariantUuid, 200m, 18m);

        var entries = await ProductLedgerAsync(h, c);

        // The same month as ProductLedgerAcceptanceTests works on paper — here through the real poster and
        // invoice service, into real decimal(18,4) and decimal(18,2) columns and back.
        entries.Select(e => (e.EntryType, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", 100m, 10m,      1000m,    100m, 1000m),
            ("SALE",      20m, 10m,       200m,     80m,  800m),
            ("PURCHASE", 100m, 20m,      2000m,    180m, 2800m),
            ("SALE",      30m, 15.5556m,  466.67m,  150m, 2333.33m),
            ("PURCHASE",  50m, 10m,       500m,     200m, 2833.33m),
            ("SALE",     200m, 14.1667m, 2833.33m,  0m,   0m));
        ProductLedgerInvariants.Violations(entries).Should().BeEmpty();
    }

    [FinanceSqlServerFact]
    public async Task A_grn_in_one_organization_never_reaches_another_and_the_other_cannot_sell_what_it_holds()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var a = await SeedAsync(h, 10m);
        var b = await SeedAsync(h, 99m);

        await PostAsync(h, a, Receipt(a, Line(a, 0, received: 100m)));
        await PostAsync(h, b, Receipt(b, Line(b, 0, received: 5m)));

        (await ProductLedgerAsync(h, a)).Should().ContainSingle().Which.RunningValue.Should().Be(1000m);
        (await ProductLedgerAsync(h, b)).Should().ContainSingle().Which.RunningValue.Should().Be(495m);

        // B raises an invoice for A's variant: B holds none of it, whatever A holds.
        var act = () => SellAsync(h, b, a.VariantUuid, 1m, 50m);
        await act.Should().ThrowAsync<ConflictException>();

        // And A's own sale is costed at A's own average, untouched by B's 99.
        await SellAsync(h, a, a.VariantUuid, 30m, 15m);
        var sale = (await ProductLedgerAsync(h, a)).Last();
        (sale.UnitCost, sale.TotalCost, sale.RunningQty).Should().Be((10m, 300m, 70m));
        (await ProductLedgerAsync(h, b)).Should().ContainSingle("B's ledger saw none of A's sale");
    }}
