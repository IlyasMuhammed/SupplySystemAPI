using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using Line = SMS.Modules.Finance.Tests.SalesBookSeeder.Line;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-06 §17.1 — one organization's stock, costs and margins are invisible to another and cannot be
/// used by it. The tests keep the <i>same variant uuid</i> in both organizations wherever they can, since
/// that is the case a missing filter would betray: if the ledger looked past the organization at all, two
/// organizations holding "the same" variant would be one ledger.
/// </summary>
public class ProductLedgerTenantIsolationTests
{
    private const int User = ProductLedgerRig.User;

    private sealed record Orgs(ProductLedgerRig Rig, Guid A, Guid B, Guid Variant, Guid Product);

    private static Orgs Two()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();
        return new Orgs(rig, rig.Org, Guid.NewGuid(), variant, product);
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_organizations_holding_the_same_variant_keep_two_independent_ledgers()
    {
        var o = Two();

        var a1 = await o.Rig.Service(o.A).AppendEntryAsync(Buy(o.Variant, 100m, 10m));
        var b1 = await o.Rig.Service(o.B).AppendEntryAsync(Buy(o.Variant, 5m, 99m));
        var a2 = await o.Rig.Service(o.A).AppendEntryAsync(Sell(o.Variant, 30m));
        var b2 = await o.Rig.Service(o.B).AppendEntryAsync(Buy(o.Variant, 5m, 1m));

        (a1.SequenceNo, a2.SequenceNo).Should().Be((1, 2));
        (b1.SequenceNo, b2.SequenceNo).Should().Be((1, 2), "each organization numbers its own ledger from one");
        (a2.UnitCost, a2.TotalCost, a2.RunningQty, a2.RunningValue).Should().Be((10m, 300m, 70m, 700m), "the sale is costed at A's average, untouched by B paying 99");
        (b2.RunningQty, b2.RunningValue, b2.WeightedAverageCost).Should().Be((10m, 500m, 50m));
    }

    [Fact]
    public async Task An_organization_cannot_sell_stock_only_another_one_holds()
    {
        var o = Two();
        await o.Rig.Service(o.A).AppendEntryAsync(Buy(o.Variant, 100m, 10m));

        var act = () => o.Rig.Service(o.B).AppendEntryAsync(Sell(o.Variant, 1m));

        await act.Should().ThrowAsync<ConflictException>("B holds none of it, whatever A holds");
        (await o.Rig.EntriesAsync(o.Variant, o.B)).Should().BeEmpty();
        (await o.Rig.EntriesAsync(o.Variant, o.A)).Should().ContainSingle("A's ledger is untouched by the refusal");
    }

    [Fact]
    public async Task An_organization_cannot_take_stock_out_of_another_by_any_other_kind_of_entry()
    {
        var o = Two();
        await o.Rig.Service(o.A).AppendEntryAsync(Buy(o.Variant, 100m, 10m));

        foreach (var (type, direction) in new[] { ("SALE", "OUT"), ("WRITE_OFF", "OUT"), ("RETURN_OUT", "OUT"), ("ADJUSTMENT", "OUT") })
        {
            var act = () => o.Rig.Service(o.B).AppendEntryAsync(new ProductLedgerPosting(
                o.Variant, type, direction, 1m, null, "Doc", Guid.NewGuid(), "DOC-1", User));

            await act.Should().ThrowAsync<ConflictException>(type);
        }

        (await o.Rig.EntriesAsync(o.Variant, o.A)).Should().ContainSingle();
    }

    [Fact]
    public async Task What_each_organization_writes_carries_its_own_organization_and_an_auditor_sees_both()
    {
        var o = Two();
        await o.Rig.Service(o.A).AppendEntryAsync(Buy(o.Variant, 10m, 1m));
        await o.Rig.Service(o.B).AppendEntryAsync(Buy(o.Variant, 10m, 1m));
        await o.Rig.Service(o.A).AppendEntryAsync(Sell(o.Variant, 5m));

        await using var auditor = Receivables.Auditor(o.Rig.DbName);
        var all = await auditor.ProductLedgerEntries.AsNoTracking().ToListAsync();

        all.Should().HaveCount(3);
        all.Should().OnlyContain(e => e.OrganizationId == o.A || e.OrganizationId == o.B);
        all.Count(e => e.OrganizationId == o.A).Should().Be(2);
        all.Count(e => e.OrganizationId == o.B).Should().Be(1);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_ledger_the_summary_and_the_report_each_show_an_organization_only_its_own()
    {
        var o = Two();
        var seeder = new SalesBookSeeder();
        o.Rig.Variants.ProductNames[o.Product] = "Laptop";

        await using (var seed = Receivables.Auditor(o.Rig.DbName))
            await SalesBookSeeder.WriteAsync(seed,
                seeder.Sale(o.A, "SINV-A", "ISSUED", "PKR", new DateTime(2026, 1, 5), new Line(o.Product, o.Variant, Qty: 30m, Price: 15m, Cost: 10m)),
                seeder.Sale(o.B, "SINV-B", "ISSUED", "PKR", new DateTime(2026, 1, 5), new Line(o.Product, o.Variant, Qty: 2m, Price: 900m, Cost: 500m)));

        await using var dbB = Receivables.Db(o.B, o.Rig.DbName);
        var svc = new ProductLedgerQueryService(dbB, o.Rig.Variants);

        var ledger = await svc.GetLedgerAsync(o.Variant, new ProductLedgerFilter());
        ledger.Data.Should().ContainSingle().Which.Quantity.Should().Be(2m);

        var summary = await svc.GetSummaryAsync(o.Variant);
        (summary.SoldQuantity, summary.CostOfGoodsSold).Should().Be((2m, 1000m));

        var row = (await svc.GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();
        (row.QuantitySold, row.Revenue, row.CostOfGoodsSold, row.GrossProfit).Should().Be((2m, 1800m, 1000m, 800m), "not the 32 units and 450 of revenue A sold");
    }

    [Fact]
    public async Task An_organization_with_no_sales_has_an_empty_report_whatever_another_has_sold_of_the_same_product()
    {
        var o = Two();
        var seeder = new SalesBookSeeder();
        await using (var seed = Receivables.Auditor(o.Rig.DbName))
            await SalesBookSeeder.WriteAsync(seed,
                seeder.Sale(o.A, "SINV-A", "ISSUED", "PKR", new DateTime(2026, 1, 5), new Line(o.Product, o.Variant, Qty: 30m, Price: 15m, Cost: 10m)));

        await using var dbB = Receivables.Db(o.B, o.Rig.DbName);
        var svc = new ProductLedgerQueryService(dbB, o.Rig.Variants);

        (await svc.GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Should().BeEmpty();
        (await svc.GetLedgerAsync(o.Variant, new ProductLedgerFilter())).Data.Should().BeEmpty();
        (await svc.GetSummaryAsync(o.Variant)).EntryCount.Should().Be(0);
    }

    // ── Isolation by replay ──────────────────────────────────────────────────

    [Fact]
    public async Task What_organization_A_ends_up_with_is_the_same_whether_or_not_organization_B_was_busy_beside_it()
    {
        var alone  = await RunScriptForA(withNoiseFromB: false);
        var beside = await RunScriptForA(withNoiseFromB: true);

        beside.Should().Equal(alone, "nothing B did may change a sequence, a cost, a running total or a refusal of A's");
        alone.Should().HaveCountGreaterThan(9, "the script leaves a real ledger to compare");
    }

    /// <summary>A's month on the product ledger as comparable facts, with B trafficking in the same variant between every step.</summary>
    private static async Task<List<string>> RunScriptForA(bool withNoiseFromB)
    {
        var o = Two();
        var log = new List<string>();

        async Task Noise()
        {
            if (!withNoiseFromB) return;
            await o.Rig.Service(o.B).AppendEntryAsync(Buy(o.Variant, 1234.5m, 77.77m));
            await o.Rig.Service(o.B).AppendEntryAsync(Sell(o.Variant, 1000m));
            await o.Rig.Service(o.B).AppendEntryAsync(Buy(o.Variant, 0.0001m, 0m));
        }

        async Task Step(string what, Func<Task<ProductLedgerPosted>> act)
        {
            try
            {
                var p = await act();
                log.Add($"{what}: #{p.SequenceNo} {p.Direction} {p.Quantity} @ {p.UnitCost} = {p.TotalCost}; {p.RunningQty} worth {p.RunningValue} avg {p.WeightedAverageCost}");
            }
            catch (ConflictException) { log.Add($"{what}: refused"); }
            await Noise();
        }

        var svc = () => o.Rig.Service(o.A);
        await Step("buy 100@10",  () => svc().AppendEntryAsync(Buy(o.Variant, 100m, 10m)));
        await Step("sell 20",     () => svc().AppendEntryAsync(Sell(o.Variant, 20m)));
        await Step("buy 100@20",  () => svc().AppendEntryAsync(Buy(o.Variant, 100m, 20m)));
        await Step("sell 30",     () => svc().AppendEntryAsync(Sell(o.Variant, 30m)));
        await Step("oversell",    () => svc().AppendEntryAsync(Sell(o.Variant, 1000m)));
        await Step("write off 5", () => svc().AppendEntryAsync(new ProductLedgerPosting(o.Variant, "WRITE_OFF", "OUT", 5m, null, "WO", Guid.NewGuid(), "WO-1", User)));
        await Step("return in 2", () => svc().AppendEntryAsync(new ProductLedgerPosting(o.Variant, "RETURN_IN", "IN", 2m, 15.5556m, "SR", Guid.NewGuid(), "SR-1", User)));
        await Step("sell all",    () => svc().AppendEntryAsync(Sell(o.Variant, 147m)));
        await Step("sell more",   () => svc().AppendEntryAsync(Sell(o.Variant, 0.0001m)));
        await Step("buy 7@3",     () => svc().AppendEntryAsync(Buy(o.Variant, 7m, 3m)));

        return log;
    }
}

/// <summary>
/// A29-P8-06 §17.1 on a real SQL Server: the unique index that guards the sequence is per organization, so
/// two organizations writing the same variant at the same moment must neither collide nor wait on each other.
/// </summary>
public class ProductLedgerTenantIsolationSqlServerTests
{
    [FinanceSqlServerFact]
    public async Task Two_organizations_writing_the_same_variant_at_once_each_get_a_whole_ledger_of_their_own()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var variants = new FakeVariants();
        var (variant, product) = variants.New();
        var orgs = new[] { Guid.NewGuid(), Guid.NewGuid() };

        // Five writers per organization at the same instant, all on one variant uuid: ten in all.
        var postings = orgs.SelectMany(org => Enumerable.Range(1, 5).Select(i => (Org: org, I: i))).ToList();

        await Task.WhenAll(postings.Select(async p =>
        {
            await using var db = h.NewContext(p.Org);
            await new ProductLedgerService(db, variants).AppendEntryAsync(
                Buy(variant, p.I * 10m, p.Org == orgs[0] ? 10m : 1_000m) with { ProductUuid = product });
        })).WaitAsync(TimeSpan.FromSeconds(60));

        foreach (var org in orgs)
        {
            await using var db = h.NewContext(org);
            var entries = await db.ProductLedgerEntries.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync();

            entries.Select(e => e.SequenceNo).Should().Equal(1, 2, 3, 4, 5);
            entries.Should().OnlyContain(e => e.OrganizationId == org);
            ProductLedgerInvariants.Violations(entries).Should().BeEmpty();
            entries[^1].RunningQty.Should().Be(150m, "10 + 20 + 30 + 40 + 50, and nothing of the other organization's");
            entries[^1].RunningValue.Should().Be(org == orgs[0] ? 1_500m : 150_000m, "at this organization's own price, never a blend with the other's");
        }
    }
}
