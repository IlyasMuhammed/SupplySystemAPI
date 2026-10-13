using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Import;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.Import.ImportHarness;

namespace SMS.Modules.Inventory.Tests.Import;

/// <summary>A40 (TINV) — PRICING_RULE (D-6) and OPENING_STOCK (D-7) import targets.</summary>
public class StockImportTargetTests
{
    // ── Pricing rules ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pricing_rules_resolve_sku_customer_and_currency_and_enforce_the_rule_service_rules()
    {
        var h = new ImportHarness();
        var variant = await h.ProductAsync("CAB", variantSku: "CAB-RED");
        var acme = h.Customers.Add("C-00001");
        h.Customers.Add("C-00002", active: false);

        var checks = await h.PricingRules().ValidateAsync(h.Org,
        [
            Row(2, ("sku", "CAB-RED"), ("price_type", "SELLING"), ("price", "150")),
            Row(3, ("sku", "CAB"), ("customer_code", "C-00001"), ("price_type", "contract"), ("price", "140"), ("currency_code", "USD")),
            Row(4, ("sku", "CAB"), ("price_type", "CONTRACT"), ("price", "140")),
            Row(5, ("sku", "NOPE"), ("price_type", "SELLING"), ("price", "1")),
            Row(6, ("sku", "CAB"), ("customer_code", "C-00002"), ("price_type", "SELLING"), ("price", "1")),
            Row(7, ("sku", "CAB"), ("price_type", "COST"), ("price", "1")),
            Row(8, ("sku", "CAB"), ("price_type", "SELLING"), ("price", "1"), ("currency_code", "EUR")),
            Row(9, ("sku", "CAB"), ("price_type", "PROMOTIONAL"), ("price", "1"), ("valid_from", "2026-05-01"), ("valid_to", "2026-04-01")),
            Row(10, ("sku", "CAB"), ("price_type", "SELLING"), ("price", "155"))   // same key as row 2 (product SKU → default variant)
        ]);

        checks[0].Errors.Should().BeEmpty();
        checks[1].Errors.Should().BeEmpty();
        checks[2].Errors.Should().ContainSingle(e => e.Field == "customer_code" && e.Message.Contains("CONTRACT"));
        checks[3].Errors.Should().ContainSingle(e => e.Code == "LOOKUP_NOT_FOUND" && e.Field == "sku");
        checks[4].Errors.Should().ContainSingle(e => e.Field == "customer_code" && e.Message.Contains("inactive"));
        checks[5].Errors.Should().ContainSingle(e => e.Field == "price_type");
        checks[6].Errors.Should().ContainSingle(e => e.Field == "currency_code");
        checks[7].Errors.Should().ContainSingle(e => e.Code == "INVALID_DATE");
        checks[8].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_FILE");

        var outcomes = await h.PricingRules().ApplyAsync(h.Org, 9,
        [
            Create(2, ("sku", "CAB-RED"), ("price_type", "SELLING"), ("price", "150"), ("valid_from", "2026-01-01")),
            Create(3, ("sku", "CAB"), ("customer_code", "C-00001"), ("price_type", "CONTRACT"), ("price", "140"), ("currency_code", "USD"),
                ("min_qty", "10"), ("valid_from", "2026-01-01"), ("valid_to", "2026-12-31"))
        ]);

        await using var db = h.NewContext();
        var rules = await db.PricingRules.IgnoreQueryFilters().OrderBy(r => r.Id).ToListAsync();
        rules.Should().HaveCount(2).And.OnlyContain(r => r.OrganizationId == h.Org && r.VariantId == variant.Id && r.CreatedBy == 9 && r.IsActive);
        (rules[0].PriceType, rules[0].PartnerId, rules[0].UnitPrice, rules[0].CurrencyId).Should()
            .Be(("SELLING", (Guid?)null, 150m, h.Currencies.Pkr), "no currency = the sale base currency");
        (rules[1].PriceType, rules[1].PartnerId, rules[1].MinQty, rules[1].CurrencyId, rules[1].EffectiveTo).Should()
            .Be(("CONTRACT", (Guid?)acme.Uuid, (decimal?)10m, h.Currencies.Usd, (DateTime?)new DateTime(2026, 12, 31)));
        outcomes.Select(o => o.EntityUuid).Should().Equal(rules.Select(r => r.Uuid));

        // The same key again matches the rule; an UPDATE changes the price and keeps what was not sent.
        var again = await h.PricingRules().ValidateAsync(h.Org,
            [Row(2, ("sku", "CAB-RED"), ("price_type", "SELLING"), ("price", "160"), ("valid_from", "2026-01-01"))]);
        again.Single().ExistingEntityUuid.Should().Be(rules[0].Uuid);

        var updated = await h.PricingRules().ApplyAsync(h.Org, 9,
            [Update(2, rules[0].Uuid, ("sku", "CAB-RED"), ("price_type", "SELLING"), ("price", "160"), ("valid_from", "2026-01-01"), ("currency_code", null))]);
        updated.Single().Status.Should().Be("UPDATED");
        await using var db2 = h.NewContext();
        var rule = await db2.PricingRules.IgnoreQueryFilters().SingleAsync(r => r.Uuid == rules[0].Uuid);
        (rule.UnitPrice, rule.CurrencyId).Should().Be((160m, h.Currencies.Pkr));
    }

    [Fact]
    public async Task Without_the_customer_source_a_customer_code_is_refused()
    {
        var h = new ImportHarness();
        await h.ProductAsync("CAB");

        var checks = await h.PricingRules(withCustomers: false).ValidateAsync(h.Org,
            [Row(2, ("sku", "CAB"), ("customer_code", "C-1"), ("price_type", "SELLING"), ("price", "1"))]);

        checks.Single().Errors.Should().ContainSingle(e => e.Code == "LOOKUP_NOT_FOUND" && e.Field == "customer_code");
    }

    // ── Opening stock ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_stock_is_refused_until_the_opening_balance_date_is_set()
    {
        var h = new ImportHarness();
        await h.ProductAsync("CAB");
        await h.WarehouseAsync("WH1");
        ImportRowInput[] rows = [Row(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "3"))];

        var noProvider = await h.OpeningStock(withDateProvider: false).ValidateAsync(h.Org, rows);
        noProvider.Single().Errors.Should().ContainSingle(e => e.Code == "OPENING_DATE_NOT_SET"
                                                            && e.Message == "Set the opening balance date in Finance settings first.");

        h.OpeningDate.Date = null;
        (await h.OpeningStock().ValidateAsync(h.Org, rows)).Single().Errors.Should().ContainSingle(e => e.Code == "OPENING_DATE_NOT_SET");

        var act = () => h.OpeningStock().ApplyAsync(h.Org, 1, [Create(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "3"))]);
        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*Set the opening balance date*");
        (await h.NewContext().InventoryItems.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Opening_stock_posts_stock_the_ledgers_and_the_product_ledger_at_the_opening_date()
    {
        var h = new ImportHarness();
        h.OpeningDate.Date = new DateOnly(2026, 3, 31);
        var plain = await h.ProductAsync("CAB");
        var batched = await h.ProductAsync("MILK", batch: true);
        var warehouse = await h.WarehouseAsync("WH1");
        var zone = new Domain.Zone { OrganizationId = h.Org, WarehouseId = warehouse.Id, Code = "A", Name = "A" };
        h.Db.Zones.Add(zone);
        await h.Db.SaveChangesAsync();
        var bin = new Domain.Bin { OrganizationId = h.Org, ZoneId = zone.Id, Code = "A-01" };
        h.Db.Bins.Add(bin);
        await h.Db.SaveChangesAsync();

        var outcomes = await h.OpeningStock().ApplyAsync(h.Org, 77,
        [
            Create(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("location_code", "A-01"), ("quantity", "10"), ("unit_cost", "2.5")),
            Create(3, ("product_sku", "MILK"), ("warehouse_code", "WH1"), ("quantity", "4"), ("unit_cost", "1"), ("batch_number", "B1"), ("expiry_date", "2026-06-30")),
            Create(4, ("product_sku", "MILK"), ("warehouse_code", "WH1"), ("quantity", "6"), ("unit_cost", "1.5"), ("batch_number", "B2"))
        ]);

        outcomes.Should().HaveCount(3).And.OnlyContain(o => o.Status == "IMPORTED");
        await using var db = h.NewContext();
        var items = await db.InventoryItems.IgnoreQueryFilters().OrderBy(i => i.Id).ToListAsync();
        items.Select(i => (i.VariantId, i.QtyOnHand, i.UnitCost, i.BatchNumber, i.BinId, i.OrganizationId)).Should().Equal(
            (plain.Id, 10m, (decimal?)2.5m, (string?)null, (int?)bin.Id, h.Org),
            (batched.Id, 4m, (decimal?)1m, "B1", (int?)null, h.Org),
            (batched.Id, 6m, (decimal?)1.5m, "B2", (int?)null, h.Org));
        items[1].ExpiryDate.Should().Be(new DateTime(2026, 6, 30));

        var ledger = await db.InventoryLedgerEntries.IgnoreQueryFilters().OrderBy(e => e.CreatedAt).ToListAsync();
        ledger.Should().HaveCount(3).And.OnlyContain(e => e.TransactionType == "OPENING_STOCK" && e.ReferenceType == "OPENING_STOCK"
                                                          && e.TransactionDate == new DateTime(2026, 3, 31) && e.CreatedBy == 77
                                                          && e.OrganizationId == h.Org);
        ledger.Select(e => (e.ReferenceNumber, e.QuantityIn, e.BalanceAfter, e.TransactionValue)).Should().Equal(
            ("OPENING", (decimal?)10m, 10m, 25m), ("OPENING/B1", (decimal?)4m, 4m, 4m), ("OPENING/B2", (decimal?)6m, 10m, 9m));
        ledger.Select(e => e.ReferenceId).Should().Equal(outcomes.Select(o => o.EntityUuid));

        // ML-003 mirror (master product ledger): OPENING_BALANCE → WAREHOUSE.
        var master = await h.Fin.MasterProductLedgers.ToListAsync();
        master.Should().HaveCount(3).And.OnlyContain(m => m.TransactionType == "OPENING_STOCK"
                                                          && m.TransactionDate == new DateTime(2026, 3, 31)
                                                          && m.SourceType == "OPENING_BALANCE" && m.DestinationType == "WAREHOUSE");

        // A29 §11 — the Finance product ledger IN at the unit cost, dated at the opening date.
        h.ProductLedger.Calls.Select(c => (c.Posting.VariantUuid, c.Posting.Direction, c.Posting.Quantity, c.Posting.UnitCost,
                c.Posting.EntryType, c.Posting.EntryDate, c.Posting.ReferenceType, c.Posting.CreatedBy))
            .Should().Equal(
                (plain.Uuid, "IN", 10m, (decimal?)2.5m, OpeningStockImportTarget.ProductLedgerEntryType, (DateTime?)new DateTime(2026, 3, 31), "OPENING_STOCK", 77),
                (batched.Uuid, "IN", 4m, (decimal?)1m, OpeningStockImportTarget.ProductLedgerEntryType, (DateTime?)new DateTime(2026, 3, 31), "OPENING_STOCK", 77),
                (batched.Uuid, "IN", 6m, (decimal?)1.5m, OpeningStockImportTarget.ProductLedgerEntryType, (DateTime?)new DateTime(2026, 3, 31), "OPENING_STOCK", 77));
        h.ProductLedger.Calls.Select(c => c.Posting.ReferenceId).Should().Equal(outcomes.Select(o => o.EntityUuid));
    }

    [Fact]
    public async Task One_opening_per_variant_and_warehouse_and_the_stock_rules_are_enforced()
    {
        var h = new ImportHarness();
        await h.ProductAsync("CAB");
        await h.ProductAsync("MILK", batch: true);
        await h.ProductAsync("PHONE", serial: true);
        await h.ProductAsync("SVC", type: ProductType.Service, stockable: false);
        await h.WarehouseAsync("WH1");
        await h.WarehouseAsync("WH2");
        await h.OpeningStock().ApplyAsync(h.Org, 1,
            [Create(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "1"), ("unit_cost", "1"))]);

        var checks = await h.OpeningStock().ValidateAsync(h.Org,
        [
            Row(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "1")),     // already opened
            Row(3, ("product_sku", "CAB"), ("warehouse_code", "WH2"), ("quantity", "5"), ("unit_cost", "1")),
            Row(4, ("product_sku", "CAB"), ("warehouse_code", "WH2"), ("quantity", "5"), ("unit_cost", "1")),     // twice in the file
            Row(5, ("product_sku", "MILK"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "1")),    // batch required
            Row(6, ("product_sku", "PHONE"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "1")),
            Row(7, ("product_sku", "SVC"), ("warehouse_code", "WH1"), ("quantity", "5"), ("unit_cost", "1")),
            Row(8, ("product_sku", "CAB-DEFAULT"), ("warehouse_code", "WH2"), ("quantity", "-1"), ("unit_cost", "1"), ("batch_number", "X")),
            Row(9, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("location_code", "Z/NOPE"), ("quantity", "0"), ("unit_cost", "1"))
        ]);

        checks[0].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB");
        checks[1].Errors.Should().BeEmpty();
        checks[2].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_FILE");
        checks[3].Errors.Should().ContainSingle(e => e.Field == "batch_number" && e.Code == "REQUIRED");
        checks[4].Errors.Should().ContainSingle(e => e.Code == "SERIAL_TRACKED");
        checks[5].Errors.Should().ContainSingle(e => e.Code == "NOT_STOCKABLE");
        checks[6].Errors.Select(e => e.Code).Should().BeEquivalentTo("OUT_OF_RANGE", "BATCH_NOT_TRACKED", "DUPLICATE_IN_FILE");
        checks[7].Errors.Select(e => e.Code).Should().BeEquivalentTo("LOOKUP_NOT_FOUND", "DUPLICATE_IN_DB");
        checks.Should().OnlyContain(c => c.ExistingEntityUuid == null);

        var update = () => h.OpeningStock().ApplyAsync(h.Org, 1,
            [Update(2, Guid.NewGuid(), ("product_sku", "CAB"), ("warehouse_code", "WH2"), ("quantity", "5"), ("unit_cost", "1"))]);
        await update.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*never updated*");
    }

    [Fact]
    public async Task A_zero_opening_records_the_opening_without_a_product_ledger_entry()
    {
        var h = new ImportHarness();
        await h.ProductAsync("CAB");
        await h.WarehouseAsync("WH1");

        await h.OpeningStock().ApplyAsync(h.Org, 1,
            [Create(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "0"), ("unit_cost", "1"))]);

        (await h.NewContext().InventoryLedgerEntries.IgnoreQueryFilters().SingleAsync()).QuantityIn.Should().Be(0m);
        h.ProductLedger.Calls.Should().BeEmpty();
        (await h.OpeningStock().ValidateAsync(h.Org,
                [Row(2, ("product_sku", "CAB"), ("warehouse_code", "WH1"), ("quantity", "3"), ("unit_cost", "1"))]))
            .Single().Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB");
    }

    [Fact]
    public void The_opening_entry_type_is_one_the_product_ledger_accepts()
    {
        ProductLedgerEntryTypes.All.Should().Contain(OpeningStockImportTarget.ProductLedgerEntryType);
    }
}
