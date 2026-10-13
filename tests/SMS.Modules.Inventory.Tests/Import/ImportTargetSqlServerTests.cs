using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Import;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Inventory.Tests.Import.ImportHarness;

namespace SMS.Modules.Inventory.Tests.Import;

/// <summary>
/// A40 (TINV) on SQL Server (LocalDB): every target's queries translate, a batch is one transaction (a failure anywhere —
/// the Finance product ledger included — leaves nothing behind), and the organization is always the explicit one.
/// </summary>
public class ImportTargetSqlServerTests
{
    [SqlServerFact]
    public async Task Every_target_validates_and_applies_on_SQL_Server()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        // The ambient tenant is someone else: only the explicit organization may be used.
        await using var db = harness.NewContext(Guid.NewGuid());
        var services = new ServiceCollection().AddSingleton<IImportTarget>(new FakeUnitTarget(["PCS"])).BuildServiceProvider();
        var currencies = new FakeCurrencies();
        var customers = new FakeCustomers();
        var acme = customers.Add("C-1");
        var ledger = new FakeProductLedger();
        var dates = new FakeOpeningDate { Date = new DateOnly(2026, 1, 1) };

        async Task Run(IImportTarget target, params ImportApplyRow[] rows)
        {
            var checks = await target.ValidateAsync(org, rows.Select(r => new ImportRowInput(r.RowNumber, r.Values, null)).ToList());
            checks.SelectMany(c => c.Errors).Should().BeEmpty();
            (await target.ApplyAsync(org, 3, rows)).Should().HaveCount(rows.Length);
        }

        await Run(new ProductCategoryImportTarget(db),
            Create(2, ("code", "ELEC"), ("name", "Electronics")), Create(3, ("code", "CAB"), ("name", "Cables"), ("parent_code", "ELEC")));
        await Run(new ProductImportTarget(db, services),
            Create(2, ("sku", "CAB-1"), ("name", "Cable"), ("category_code", "CAB"), ("unit_code", "PCS"), ("sale_price", "5"), ("barcode", "123")),
            Create(3, ("sku", "MILK"), ("name", "Milk"), ("cost_price", "1")));
        await Run(new WarehouseImportTarget(db), Create(2, ("code", "WH1"), ("name", "Main")));
        await Run(new WarehouseLocationImportTarget(db), Create(2, ("warehouse_code", "WH1"), ("zone_code", "A"), ("bin_code", "A-1")));
        await Run(new PricingRuleImportTarget(db, customers, currencies, currencies),
            Create(2, ("sku", "CAB-1"), ("customer_code", "C-1"), ("price_type", "CONTRACT"), ("price", "4.5")));
        await Run(new OpeningStockImportTarget(db, new InventoryLedgerService(db, NullLogger<InventoryLedgerService>.Instance), dates, ledger),
            Create(2, ("product_sku", "CAB-1"), ("warehouse_code", "WH1"), ("location_code", "A/A-1"), ("quantity", "7"), ("unit_cost", "2")));

        // Matching on SQL Server too: each re-validated key finds its row.
        (await new ProductImportTarget(db, services).ValidateAsync(org, [Row(2, ("sku", "cab-1"))])).Single().ExistingEntityUuid.Should().NotBeNull();
        (await new OpeningStockImportTarget(db, new InventoryLedgerService(db, NullLogger<InventoryLedgerService>.Instance), dates, ledger)
                .ValidateAsync(org, [Row(2, ("product_sku", "CAB-1"), ("warehouse_code", "WH1"), ("quantity", "1"), ("unit_cost", "1"))]))
            .Single().Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB");

        await using var check = harness.NewContext(org);
        (await check.Products.CountAsync()).Should().Be(2);
        (await check.PricingRules.SingleAsync()).PartnerId.Should().Be(acme.Uuid);
        (await check.InventoryItems.SingleAsync()).QtyOnHand.Should().Be(7m);
        ledger.Calls.Should().ContainSingle().Which.Transaction.Should().NotBeNull("the product ledger joins Inventory's transaction");
    }

    [SqlServerFact]
    public async Task A_failing_product_ledger_rolls_the_whole_opening_batch_back()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var seeded = await harness.SeedStockAsync(onHand: 0m);
        var org = seeded.OrganizationId;
        await using var db = harness.NewContext(Guid.NewGuid());
        var sku = await db.ProductVariants.IgnoreQueryFilters().Where(v => v.Uuid == seeded.VariantUuid).Select(v => v.Sku).SingleAsync();
        var code = await db.Warehouses.IgnoreQueryFilters().Where(w => w.Uuid == seeded.WarehouseUuid).Select(w => w.Code).SingleAsync();
        db.Warehouses.Add(new Domain.Warehouse { Uuid = Guid.NewGuid(), OrganizationId = org, Code = "WH-B", Name = "Second" });
        await db.SaveChangesAsync();

        var ledger = new FakeProductLedger { ThrowOnCall = 1 };
        var target = new OpeningStockImportTarget(db, new InventoryLedgerService(db, NullLogger<InventoryLedgerService>.Instance),
            new FakeOpeningDate(), ledger);

        var act = () => target.ApplyAsync(org, 1,
        [
            Create(2, ("product_sku", sku), ("warehouse_code", "WH-B"), ("quantity", "5"), ("unit_cost", "1")),
            Create(3, ("product_sku", sku), ("warehouse_code", code), ("quantity", "5"), ("unit_cost", "1"))
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("product ledger down");
        await using var check = harness.NewContext(org);
        (await check.InventoryLedgerEntries.CountAsync()).Should().Be(0, "row 2's stock and ledger entry were rolled back with row 3");
        (await check.InventoryItems.SumAsync(i => i.QtyOnHand)).Should().Be(0m);
        db.ChangeTracker.Entries().Should().BeEmpty();

        // The same batch succeeds afterwards on the same (scoped) context.
        ledger.ThrowOnCall = -1;
        (await target.ApplyAsync(org, 1,
        [
            Create(2, ("product_sku", sku), ("warehouse_code", "WH-B"), ("quantity", "5"), ("unit_cost", "1")),
            Create(3, ("product_sku", sku), ("warehouse_code", code), ("quantity", "5"), ("unit_cost", "1"))
        ])).Should().HaveCount(2);
        (await check.InventoryLedgerEntries.CountAsync()).Should().Be(2);
    }
}
