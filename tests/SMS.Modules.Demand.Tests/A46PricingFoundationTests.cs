using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A46 (BE-1) — PO pricing foundation, in memory: the order date (C1, TS-27..31), 4dp prices and the override test (C3, TS-35/36),
/// the pricing middleware on every update path (C4, TS-38..41), the line factory and the batch summary (C5, TS-42..47), and the
/// currency-aware resolver (C10, TS-50..52, 56). The migration, the real column types and the HTTP surface are covered by
/// <see cref="A46PricingMigrationTests"/> and SMS.Integration.Tests (Pricelist\A46PricingE2ETests).
/// </summary>
public class A46PricingFoundationTests
{
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid Gbp = Guid.NewGuid();
    private static readonly DateTime Oct17 = new(2026, 10, 17);
    private static readonly DateTime Oct15 = new(2026, 10, 15);

    /// <summary>Rates as "1 From = Rate To"; a pair with no direct rate is crossed through USD, like the real converter.</summary>
    private sealed class FakeConverter : ICurrencyConverter
    {
        public readonly Dictionary<(Guid From, Guid To), decimal> Rates = new();
        public DateTime RateDate = new(2026, 10, 15);
        public int Calls;
        private static readonly Dictionary<Guid, string> Codes = new() { [Usd] = "USD", [Eur] = "EUR", [Gbp] = "GBP" };

        private decimal? Rate(Guid from, Guid to)
        {
            if (from == to) return 1m;
            if (Rates.TryGetValue((from, to), out var direct)) return direct;
            if (Rates.TryGetValue((to, from), out var inverse)) return 1m / inverse;
            if (from != Usd && to != Usd && Rate(from, Usd) is { } a && Rate(Usd, to) is { } b) return a * b;
            return null;
        }

        public Task<CurrencyConvertResult> ConvertAsync(CurrencyConvertRequest r, CancellationToken ct = default)
        {
            Calls++;
            var from = r.FromCurrencyId!.Value; var to = r.ToCurrencyId!.Value;
            var rate = Rate(from, to);
            return Task.FromResult(rate is null
                ? new CurrencyConvertResult(false, 0m, 0m, r.EffectiveDate, Codes[from], Codes[to], "No exchange rate")
                : new CurrencyConvertResult(true, Math.Round(r.Amount * rate.Value, r.DecimalPlaces, MidpointRounding.AwayFromZero), rate.Value,
                    RateDate, Codes[from], Codes[to], null));
        }

        public Task<CurrencyRateLookup> GetRateAsync(Guid? f, string? fc, Guid? t, string? tc, DateTime d, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class W
    {
        public required Guid Org;
        public required DemandDbContext Db;
        public required InventoryDbContext Inv;
        public required PurchasePriceResolver Resolver;
        public required PurchaseOrderRepository Repo;
        public required IPurchaseOrderLinePricingMiddleware Pricing;
        public required IPurchaseOrderLineFactory Factory;
        public FakeConverter? Converter;
        public Guid Supplier = Guid.NewGuid();
        public Guid Product, Variant, Variant2, Product2, OtherProductVariant;
        public Mock<IPurchasePriceResolver>? Spy;
    }

    private static W NewWorld(bool withConverter = true, IPurchasePriceResolver? resolverOverride = null)
    {
        var orgId = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = orgId };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var inv = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var p1 = new Product { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001", Name = "Widget-X", IsActive = true, CreatedBy = 1 };
        var v1 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001-R", VariantName = "Red", IsDefault = true, IsActive = true, CreatedBy = 1, PurchasePrice = 7m };
        var v2 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001-B", VariantName = "Blue", IsActive = true, CreatedBy = 1 };
        p1.Variants.Add(v1); p1.Variants.Add(v2);
        var p2 = new Product { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "GAD-002", Name = "Gadget", IsActive = true, CreatedBy = 1 };
        var v3 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "GAD-002-1", VariantName = "Std", IsDefault = true, IsActive = true, CreatedBy = 1 };
        p2.Variants.Add(v3);
        inv.Products.AddRange(p1, p2);
        inv.SaveChanges();

        // The organization buys in USD.
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>(), It.IsAny<TransactionDomain>(), It.IsAny<CancellationToken>())).ReturnsAsync(Usd);
        var codes = new Mock<ICurrencyCodeLookup>();
        codes.Setup(c => c.GetCodeAsync(Usd, It.IsAny<CancellationToken>())).ReturnsAsync("USD");
        codes.Setup(c => c.GetCodeAsync(Eur, It.IsAny<CancellationToken>())).ReturnsAsync("EUR");
        codes.Setup(c => c.GetCodeAsync(Gbp, It.IsAny<CancellationToken>())).ReturnsAsync("GBP");
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => (IReadOnlyDictionary<Guid, string>)ids.ToDictionary(i => i, _ => "Acme Corp"));

        var converter = withConverter ? new FakeConverter() : null;
        var resolver = new PurchasePriceResolver(db, inv, orgCurrency.Object, codes.Object, names.Object, converter);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"PO-2026-{++seq:D5}");
        var pricing = new PurchaseOrderLinePricingMiddleware(db, resolverOverride ?? resolver, inv);
        var factory = new PurchaseOrderLineFactory(pricing);
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance, numbers.Object, inv,
            orgCurrency: orgCurrency.Object, currencyCodes: codes.Object, priceResolver: resolverOverride ?? resolver,
            currencyConverter: converter, linePricing: pricing, lineFactory: factory);

        return new W
        {
            Org = orgId, Db = db, Inv = inv, Product = p1.Uuid, Variant = v1.Uuid, Variant2 = v2.Uuid, Product2 = p2.Uuid, OtherProductVariant = v3.Uuid,
            Resolver = resolver, Repo = repo, Pricing = pricing, Factory = factory, Converter = converter
        };
    }

    private static SupplierPricelist Row(W w, decimal price, decimal minQty = 1m, int priority = 0, string from = "2026-01-01", string to = "2026-12-31",
        PricingDateBasis basis = PricingDateBasis.OrderDate, Guid? variant = null, Guid? currency = null, int? leadTime = 14, bool active = true) =>
        new()
        {
            OrganizationId = w.Org, ProductUuid = w.Product, VariantUuid = variant, SupplierUuid = w.Supplier, MinQuantity = minQty,
            UnitPrice = price, CurrencyId = currency ?? Usd, PricingDateBasis = basis, ValidFrom = DateTime.Parse(from), ValidTo = DateTime.Parse(to),
            Priority = priority, IsActive = active, CreatedBy = 1, LeadTimeDays = leadTime
        };

    private static async Task<SupplierPricelist[]> Seed(W w, params SupplierPricelist[] rows)
    {
        w.Db.SupplierPricelists.AddRange(rows);
        await w.Db.SaveChangesAsync();
        return rows;
    }

    private static CreatePoLineRequest Line(W w, decimal qty, decimal price = 0m, int? source = null, Guid? row = null, decimal? resolved = null,
        string? reason = null, DateTime? required = null, Guid? variant = null, Guid? uuid = null, Guid? prLine = null) =>
        new()
        {
            Uuid = uuid, VariantUuid = variant ?? w.Variant, ItemDescription = "Widget-X", Quantity = qty, UnitPrice = price, PriceSource = source,
            SupplierPricelistUuid = row, SystemResolvedPrice = resolved, PriceOverrideReason = reason, RequiredDate = required, SourcePrLineUuid = prLine
        };

    private static CreatePoRequest Po(W w, DateTime? orderDate = null, Guid? currency = null, DateTime? delivery = null, params CreatePoLineRequest[] lines) =>
        new() { SupplierId = w.Supplier, SupplierName = "Acme Corp", OrderDate = orderDate, CurrencyId = currency, DeliveryDate = delivery, Lines = [.. lines] };

    private static async Task<PurchaseOrder> StoredPo(W w, Guid po)
    {
        w.Db.ChangeTracker.Clear();
        return await w.Db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == po);
    }

    private static async Task<PurchaseOrderLine> OnlyLine(W w, Guid po) => (await StoredPo(w, po)).Lines.Single();

    // ── C1: the order date ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_27_A46_0015_a_new_PO_is_dated_today_UTC_and_an_explicit_date_wins()
    {
        var w = NewWorld();
        var before = DateTime.UtcNow.Date;
        var today = await StoredPo(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, 5m)), 1));
        today.OrderDate.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.Date);

        var dated = await StoredPo(w, await w.Repo.CreateAsync(Po(w, orderDate: new DateTime(2026, 9, 1, 14, 30, 0), lines: Line(w, 1m, 5m)), 1));
        dated.OrderDate.Should().Be(new DateTime(2026, 9, 1), "the time of day is ignored");
    }

    [Fact]
    public async Task TS_27_the_PO_reads_and_lists_its_order_date_as_yyyy_MM_dd()
    {
        var w = NewWorld();
        var uuid = await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 1m, 5m)), 1);
        (await w.Repo.GetByIdAsync(uuid))!.OrderDate.Should().Be("2026-10-15");
        (await w.Repo.GetListAsync(new PoListFilter())).Data.Single().OrderDate.Should().Be("2026-10-15");
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2500)]
    public async Task An_absurd_order_date_is_a_400(int year)
    {
        var w = NewWorld();
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, orderDate: new DateTime(year, 1, 1), lines: Line(w, 1m, 5m)), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*order date*");
    }

    [Fact]
    public async Task TS_28_A46_0016_a_draft_PO_accepts_a_new_order_date_and_keeps_it_when_omitted()
    {
        var w = NewWorld();
        var uuid = await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 1m, 5m)), 1);

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { OrderDate = new DateTime(2026, 11, 2) }, 1);
        (await StoredPo(w, uuid)).OrderDate.Should().Be(new DateTime(2026, 11, 2));

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Title = "renamed" }, 1);
        (await StoredPo(w, uuid)).OrderDate.Should().Be(new DateTime(2026, 11, 2), "an omitted date is not a change");
    }

    [Fact]
    public async Task TS_29_A46_0017_a_PO_that_left_DRAFT_refuses_another_order_date_with_a_400()
    {
        var w = NewWorld();
        var uuid = await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 1m, 5m)), 1);
        var po = await w.Db.PurchaseOrders.SingleAsync(); po.Status = "APPROVED"; await w.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => w.Repo.UpdateAsync(uuid, new PatchPoRequest { OrderDate = Oct17 }, 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*order date*draft*");
        (await StoredPo(w, uuid)).OrderDate.Should().Be(Oct15);
        // Anything else on a non-draft PO is still the existing 422.
        await FluentActions.Awaiting(() => w.Repo.UpdateAsync(uuid, new PatchPoRequest { Title = "x" }, 1)).Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task TS_31_A46_0018_the_resolver_is_given_the_POs_order_date_never_today_or_the_creation_date()
    {
        // A row valid only on Oct 15: a PO dated Oct 15 is priced from it however late the line is added; one dated Oct 17 is not.
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m, from: "2026-10-15", to: "2026-10-15")))[0];

        var dated15 = await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 5m)), 1);
        var line = await OnlyLine(w, dated15);
        line.PriceSource.Should().Be(PriceSource.Pricelist);
        line.UnitPrice.Should().Be(12.5m);
        line.SupplierPricelistUuid.Should().Be(row.UUID);

        // The line is edited later (today is not Oct 15): still resolved for Oct 15.
        await w.Repo.UpdateAsync(dated15, new PatchPoRequest { Lines = [Line(w, 5m), Line(w, 3m, variant: w.Variant2)] }, 1);
        (await StoredPo(w, dated15)).Lines.Single(l => l.VariantUuid == w.Variant2).PriceSource.Should().Be(PriceSource.Pricelist);

        var dated17 = await w.Repo.CreateAsync(Po(w, orderDate: Oct17, lines: Line(w, 5m)), 1);
        (await OnlyLine(w, dated17)).PriceSource.Should().Be(PriceSource.Manual, "the row is not valid on the 17th");
    }

    [Fact]
    public async Task TS_31_a_spy_resolver_receives_PO_OrderDate_in_the_request()
    {
        var spy = new Mock<IPurchasePriceResolver>();
        PriceResolutionRequest? seen = null;
        spy.Setup(r => r.ResolveAsync(It.IsAny<PriceResolutionRequest>(), It.IsAny<CancellationToken>()))
           .Callback<PriceResolutionRequest, CancellationToken>((req, _) => seen = req)
           .ReturnsAsync(new PriceResolutionResult { Found = false });
        var w = NewWorld(resolverOverride: spy.Object);
        await w.Repo.CreateAsync(Po(w, orderDate: new DateTime(2026, 3, 9), currency: Eur, lines: Line(w, 5m)), 1);
        seen.Should().NotBeNull();
        seen!.OrderDate.Should().Be(new DateTime(2026, 3, 9));
        seen.PoCurrencyId.Should().Be(Eur, "the PO's currency travels as poCurrencyId");
    }

    [Fact]
    public async Task C1_lead_time_defaults_an_empty_required_date_to_the_order_date_plus_the_rows_lead_time()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 10m, leadTime: 14));

        var empty = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 5m)), 1));
        empty.RequiredDate.Should().Be(new DateTime(2026, 10, 29));

        var typed = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 5m, required: new DateTime(2026, 12, 1))), 1));
        typed.RequiredDate.Should().Be(new DateTime(2026, 12, 1), "a date the buyer typed is never replaced");

        var headerDate = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, orderDate: Oct15, delivery: new DateTime(2026, 11, 20), lines: Line(w, 5m)), 1));
        headerDate.RequiredDate.Should().BeNull("the PO already has a delivery date");

        var manual = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, orderDate: Oct15, lines: Line(w, 5m, 9m)), 1));
        manual.RequiredDate.Should().BeNull("a Manual line has no pricelist row to take a lead time from");
    }

    // ── C3: 4dp prices ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_35_A46_0028_a_four_decimal_pricelist_price_reaches_the_line_unrounded_and_the_total_is_rounded_to_2dp()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.3456m));
        var line = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 3m)), 1));
        line.UnitPrice.Should().Be(12.3456m);
        line.SystemResolvedPrice.Should().Be(12.3456m);
        line.LineTotal.Should().Be(37.04m, "Round(3 x 12.3456, 2)");
    }

    [Fact]
    public async Task TS_36_A46_0029_0030_an_unchanged_4dp_price_stays_Pricelist_and_one_cent_off_is_an_Override()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.3456m)))[0];

        var same = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 3m, 12.3456m, 1, row.UUID, 12.3456m)), 1));
        same.PriceSource.Should().Be(PriceSource.Pricelist, "no false trigger: 12.3456 is 12.3456");

        var edited = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 3m, 12.35m, 1, row.UUID, 12.3456m)), 1));
        edited.PriceSource.Should().Be(PriceSource.Override, "12.3500 is not 12.3456");
        edited.SystemResolvedPrice.Should().Be(12.3456m);
        edited.UnitPrice.Should().Be(12.35m);

        // The A45 two-decimal comparison is gone: 12.35 vs 12.3456 used to be "the same price".
        var fiveDecimals = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 3m, 12.34564m, 1, row.UUID, 12.3456m)), 1));
        fiveDecimals.PriceSource.Should().Be(PriceSource.Pricelist, "compared at the 4dp it is stored at");
    }

    // ── C4: server-side pricing on create and every update ─────────────────────────

    [Fact]
    public async Task TS_38_A46_0153_a_new_line_with_no_price_is_priced_by_the_server_from_the_pricelist()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m, minQty: 5m)))[0];
        var line = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 10m)), 1));
        line.PriceSource.Should().Be(PriceSource.Pricelist);
        line.UnitPrice.Should().Be(12.5m);
        line.SystemResolvedPrice.Should().Be(12.5m);
        line.SupplierPricelistUuid.Should().Be(row.UUID);
        line.LineTotal.Should().Be(125m);
    }

    [Fact]
    public async Task TS_38_a_client_claiming_Manual_or_Pricelist_without_a_price_is_priced_the_same_way()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.5m));
        (await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, source: 0)), 1))).PriceSource.Should().Be(PriceSource.Pricelist);
        (await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, source: 1)), 1))).UnitPrice.Should().Be(12.5m);
    }

    [Fact]
    public async Task A46_0154_a_new_line_with_no_price_and_no_pricelist_is_Manual_at_the_variants_catalogue_price_or_zero()
    {
        var w = NewWorld();
        var catalogue = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m)), 1));
        catalogue.PriceSource.Should().Be(PriceSource.Manual);
        catalogue.UnitPrice.Should().Be(7m, "the variant's purchase price has always stood in for a missing price");

        var none = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, variant: w.Variant2)), 1));
        none.PriceSource.Should().Be(PriceSource.Manual);
        none.UnitPrice.Should().Be(0m);
        none.SystemResolvedPrice.Should().BeNull();
        none.SupplierPricelistUuid.Should().BeNull();
    }

    [Fact]
    public async Task A46_0155_TS_39_raising_the_quantity_of_a_Pricelist_line_re_prices_it_silently_to_the_new_tier()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 50m, 15m, 1, rows[0].UUID, 15m)), 1);
        var stored = await OnlyLine(w, uuid);
        stored.UnitPrice.Should().Be(15m);

        // The client changes only the quantity (it still sends the old price and the old row).
        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 200m, 15m, 1, rows[0].UUID, 15m, uuid: stored.UUID)] }, 1);
        var after = await OnlyLine(w, uuid);
        after.PriceSource.Should().Be(PriceSource.Pricelist);
        after.UnitPrice.Should().Be(12.5m);
        after.SystemResolvedPrice.Should().Be(12.5m);
        after.SupplierPricelistUuid.Should().Be(rows[1].UUID);
        after.LineTotal.Should().Be(2500m);
        (await StoredPo(w, uuid)).TotalAmount.Should().Be(2500m);
    }

    [Fact]
    public async Task A46_0155_a_quantity_that_stays_in_its_tier_changes_nothing()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 50m)), 1);
        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 60m, 15m, 1, rows[0].UUID, 15m)] }, 1);
        var line = await OnlyLine(w, uuid);
        (line.PriceSource, line.UnitPrice, line.SupplierPricelistUuid).Should().Be((PriceSource.Pricelist, 15m, (Guid?)rows[0].UUID));
    }

    [Fact]
    public async Task A46_0156_TS_40_raising_the_quantity_of_an_Override_line_updates_only_the_system_price()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 50m, 11m, 2, rows[0].UUID, 15m, "Spot discount")), 1);
        var stored = await OnlyLine(w, uuid);
        stored.PriceSource.Should().Be(PriceSource.Override);

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 200m, 11m, 2, rows[0].UUID, 15m, "Spot discount", uuid: stored.UUID)] }, 1);
        var after = await OnlyLine(w, uuid);
        after.PriceSource.Should().Be(PriceSource.Override);
        after.UnitPrice.Should().Be(11m, "the buyer's price is theirs");
        after.SystemResolvedPrice.Should().Be(12.5m, "what the pricelist now says for 200");
        after.SupplierPricelistUuid.Should().Be(rows[1].UUID);
        after.PriceOverrideReason.Should().Be("Spot discount");
    }

    [Fact]
    public async Task A46_0159_raising_the_quantity_of_a_Manual_line_keeps_its_price_and_source()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 5m, 9m)), 1);
        var stored = await OnlyLine(w, uuid);
        stored.SystemResolvedPrice.Should().BeNull();

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 200m, 9m, uuid: stored.UUID)] }, 1);
        var after = await OnlyLine(w, uuid);
        after.PriceSource.Should().Be(PriceSource.Manual);
        after.UnitPrice.Should().Be(9m);
        after.SystemResolvedPrice.Should().Be(15m, "only the system price is refreshed, so the buyer can see what the pricelist offers");
    }

    [Fact]
    public async Task A46_0157_TS_41_an_RFQ_price_is_never_re_priced_by_a_quantity_change()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 5m, minQty: 100m));
        var prLine = new PrLine { UUID = Guid.NewGuid(), ItemDescription = "Widget", Quantity = 1, QuotationStatus = "AWARDED", OrganizationId = w.Org };
        w.Db.PrLines.Add(prLine); await w.Db.SaveChangesAsync();

        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 50m, 8m, 3, prLine: prLine.UUID)), 1);
        var stored = await OnlyLine(w, uuid);
        stored.PriceSource.Should().Be(PriceSource.RFQ);

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 500m, 8m, 3, prLine: prLine.UUID, uuid: stored.UUID)] }, 1);
        var after = await OnlyLine(w, uuid);
        (after.PriceSource, after.UnitPrice, after.Quantity).Should().Be((PriceSource.RFQ, 8m, 500m));
        after.SystemResolvedPrice.Should().BeNull();
        after.SupplierPricelistUuid.Should().BeNull();
    }

    [Fact]
    public async Task A46_0158_a_BlanketOrder_price_is_never_re_priced_by_any_mutation()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 5m, minQty: 1m));
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT" };
        var made = await w.Factory.CreateLineAsync(po, new CreateLineInput
        {
            Origin = LineOrigin.BlanketOrder, LineNo = 1, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 10m,
            SuppliedPrice = 9.1234m, SuppliedPriceSource = PriceSource.BlanketOrder
        });
        made.Line.PriceSource.Should().Be(PriceSource.BlanketOrder);
        made.Line.UnitPrice.Should().Be(9.1234m);
        made.PriceWasResolved.Should().BeTrue();

        made.Line.Quantity = 500m;
        await w.Pricing.ApplyPricingAsync(made.Line, po, new LineChangeContext { QuantityChanged = true, ProductChanged = true });
        (made.Line.UnitPrice, made.Line.PriceSource, made.Line.SystemResolvedPrice).Should().Be((9.1234m, PriceSource.BlanketOrder, (decimal?)null));
    }

    [Fact]
    public async Task A46_0160_a_changed_product_is_resolved_afresh()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 15m, variant: w.Variant), Row(w, 9m, variant: w.Variant2));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 10m)), 1);
        var stored = await OnlyLine(w, uuid);
        stored.UnitPrice.Should().Be(15m);

        // Another variant on the same line, sent with its own (resolved) price.
        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 10m, 9m, 1, variant: w.Variant2, uuid: stored.UUID)] }, 1);
        var after = await OnlyLine(w, uuid);
        (after.VariantUuid, after.UnitPrice, after.PriceSource, after.SystemResolvedPrice).Should().Be((w.Variant2, 9m, PriceSource.Pricelist, (decimal?)9m));
    }

    [Fact]
    public async Task A46_0161_the_middleware_does_nothing_to_a_PO_that_left_DRAFT_but_prices_a_line_born_on_one()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m)))[0];
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "APPROVED" };
        var line = (await w.Factory.CreateLineAsync(po, new CreateLineInput { LineNo = 1, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 50m })).Line;
        line.PriceSource.Should().Be(PriceSource.Pricelist, "creation prices once, whatever the PO's status (an AUTO_SEND PO is born APPROVED)");

        line.Quantity = 500m;
        var outcome = await w.Pricing.ApplyPricingAsync(line, po, new LineChangeContext { QuantityChanged = true });
        outcome.Skipped.Should().BeTrue();
        line.UnitPrice.Should().Be(row.UnitPrice, "an approved PO's prices are not touched");
    }

    [Fact]
    public async Task A46_0162_0163_a_delivery_date_change_re_prices_only_a_line_whose_row_is_priced_by_delivery_date()
    {
        var w = NewWorld();
        var rows = await Seed(w,
            Row(w, 14m, from: "2026-11-01", to: "2026-11-30", basis: PricingDateBasis.DeliveryDate),
            Row(w, 16m, from: "2026-12-01", to: "2026-12-31", basis: PricingDateBasis.DeliveryDate));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 5m, required: new DateTime(2026, 11, 15))), 1);
        var stored = await OnlyLine(w, uuid);
        stored.UnitPrice.Should().Be(14m);

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 5m, 14m, 1, rows[0].UUID, 14m, required: new DateTime(2026, 12, 10), uuid: stored.UUID)] }, 1);
        var moved = await OnlyLine(w, uuid);
        (moved.UnitPrice, moved.SupplierPricelistUuid).Should().Be((16m, (Guid?)rows[1].UUID));

        // The same move on a row priced by ORDER date changes nothing.
        var w2 = NewWorld();
        var orderBasis = (await Seed(w2, Row(w2, 14m), Row(w2, 3m, minQty: 1000m)))[0];
        var uuid2 = await w2.Repo.CreateAsync(Po(w2, lines: Line(w2, 5m, required: new DateTime(2026, 11, 15))), 1);
        var s2 = await OnlyLine(w2, uuid2);
        await w2.Repo.UpdateAsync(uuid2, new PatchPoRequest { Lines = [Line(w2, 5m, 14m, 1, orderBasis.UUID, 14m, required: new DateTime(2026, 12, 10), uuid: s2.UUID)] }, 1);
        (await OnlyLine(w2, uuid2)).UnitPrice.Should().Be(14m);
    }

    [Fact]
    public async Task A_Pricelist_line_whose_new_quantity_no_row_covers_becomes_Manual_and_keeps_its_price_with_a_warning()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 15m, minQty: 50m)))[0];
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT" };
        var line = (await w.Factory.CreateLineAsync(po, new CreateLineInput { LineNo = 1, VariantUuid = w.Variant, ItemDescription = "Widget-X", Quantity = 60m })).Line;
        line.PriceSource.Should().Be(PriceSource.Pricelist);

        line.Quantity = 10m;
        var outcome = await w.Pricing.ApplyPricingAsync(line, po, new LineChangeContext { QuantityChanged = true });
        (line.PriceSource, line.UnitPrice, line.SystemResolvedPrice, line.SupplierPricelistUuid).Should().Be((PriceSource.Manual, 15m, (decimal?)null, (Guid?)null));
        outcome.Warning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task E_7_a_client_claiming_BlanketOrder_gets_Manual_and_an_unknown_source_is_a_400()
    {
        var w = NewWorld();
        var claimed = await OnlyLine(w, await w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, 5m, 4)), 1));
        claimed.PriceSource.Should().Be(PriceSource.Manual);
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, 5m, 5)), 1)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, lines: Line(w, 1m, 5m, -1)), 1)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Consolidating_onto_a_production_draft_re_prices_a_Pricelist_line_for_the_bumped_quantity_and_prices_a_new_one()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m), Row(w, 20m, variant: w.Variant2));
        PatchLine(out var first, w, 60m);
        var created = await w.Repo.AddOrIncreaseProductionLineAsync(new CreatePoRequest { SupplierId = w.Supplier, SupplierName = "Acme", Lines = [first] }, 1);
        created.IsNewPo.Should().BeTrue();
        (await OnlyLine(w, created.PoUuid)).UnitPrice.Should().Be(15m, "production lines are priced from the pricelist too");

        PatchLine(out var bump, w, 60m);
        var bumped = await w.Repo.AddOrIncreaseProductionLineAsync(new CreatePoRequest { SupplierId = w.Supplier, SupplierName = "Acme", Lines = [bump] }, 1);
        bumped.IsNewPo.Should().BeFalse();
        bumped.LineUuid.Should().Be(created.LineUuid, "the same line is bumped in place");
        var line = await OnlyLine(w, created.PoUuid);
        (line.Quantity, line.UnitPrice, line.SupplierPricelistUuid, line.LineTotal).Should().Be((120m, 12.5m, (Guid?)rows[1].UUID, 1500m));

        PatchLine(out var other, w, 5m, w.Variant2);
        await w.Repo.AddOrIncreaseProductionLineAsync(new CreatePoRequest { SupplierId = w.Supplier, SupplierName = "Acme", Lines = [other] }, 1);
        (await StoredPo(w, created.PoUuid)).Lines.Single(l => l.VariantUuid == w.Variant2).UnitPrice.Should().Be(20m);

        static void PatchLine(out CreatePoLineRequest l, W w, decimal qty, Guid? variant = null) =>
            l = new CreatePoLineRequest { VariantUuid = variant ?? w.Variant, ItemDescription = "Widget-X", Quantity = qty };
    }

    // ── C5: the line factory ───────────────────────────────────────────────────────

    [Fact]
    public async Task A46_0185_an_RFQ_line_with_a_supplied_price_is_RFQ_and_never_calls_the_resolver()
    {
        var spy = new Mock<IPurchasePriceResolver>(MockBehavior.Strict);
        var w = NewWorld(resolverOverride: spy.Object);
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT" };
        var made = await w.Factory.CreateLineAsync(po, new CreateLineInput
        {
            Origin = LineOrigin.Rfq, LineNo = 1, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 4m, SuppliedPrice = 8.5m, SuppliedPriceSource = PriceSource.RFQ
        });
        (made.Line.PriceSource, made.Line.UnitPrice, made.Line.LineTotal, made.PriceWasResolved).Should().Be((PriceSource.RFQ, 8.5m, 34m, true));
        spy.Verify(r => r.ResolveAsync(It.IsAny<PriceResolutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A46_0187_0188_an_import_line_with_a_price_is_Manual_and_without_one_tries_the_pricelist()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12m));
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT" };
        var priced = await w.Factory.CreateLineAsync(po, new CreateLineInput
        {
            Origin = LineOrigin.Import, LineNo = 1, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 4m, SuppliedPrice = 9.99m, SuppliedPriceSource = PriceSource.Manual
        });
        (priced.Line.PriceSource, priced.Line.UnitPrice).Should().Be((PriceSource.Manual, 9.99m));

        foreach (var supplied in new decimal?[] { 0m, null })
        {
            var unpriced = await w.Factory.CreateLineAsync(po, new CreateLineInput
            {
                Origin = LineOrigin.Import, LineNo = 2, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 4m, SuppliedPrice = supplied, SuppliedPriceSource = PriceSource.Manual
            });
            (unpriced.Line.PriceSource, unpriced.Line.UnitPrice).Should().Be((PriceSource.Pricelist, 12m));
        }

        var nothing = await w.Factory.CreateLineAsync(po, new CreateLineInput
        {
            Origin = LineOrigin.Import, LineNo = 3, VariantUuid = w.OtherProductVariant, ItemDescription = "y", Quantity = 4m, SuppliedPrice = 0m, SuppliedPriceSource = PriceSource.Manual
        });
        (nothing.Line.PriceSource, nothing.Line.UnitPrice, nothing.PriceWasResolved).Should().Be((PriceSource.Manual, 0m, false));
        nothing.WarningMessage.Should().NotBeNullOrEmpty();
    }

    private static async Task<(Guid Pr, PrLine[] Lines)> ApprovedPr(W w, params (Guid? Product, decimal Qty, decimal Estimate, string? Quotation)[] lines)
    {
        var pr = new PurchaseRequisition { UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PrNumber = "PR-1", PrTitle = "t", Status = "APPROVED", OrganizationId = w.Org };
        var no = 1;
        foreach (var l in lines)
            pr.Lines.Add(new PrLine
            {
                UUID = Guid.NewGuid(), OrganizationId = w.Org, LineNo = no++, ProductId = l.Product, ItemDescription = $"Item {no - 1}", Quantity = l.Qty,
                EstimatedUnitPrice = l.Estimate, LineTotal = l.Qty * l.Estimate, QuotationStatus = l.Quotation, RequiresQuotation = l.Quotation is not null
            });
        w.Db.PurchaseRequisitions.Add(pr);
        await w.Db.SaveChangesAsync();
        return (pr.UUID, pr.Lines.OrderBy(l => l.LineNo).ToArray());
    }

    [Fact]
    public async Task TS_44_A46_0189_converting_a_5_line_requisition_prices_the_lines_that_have_a_pricelist_and_summarises()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m)))[0];
        // 2 lines for the widget (pricelist), 1 awarded RFQ, 2 free-text / unknown product (no pricelist).
        var (prUuid, prLines) = await ApprovedPr(w,
            (w.Product, 10m, 99m, null), (w.Product, 20m, 98m, null), (w.Product, 5m, 8m, "AWARDED"), (null, 1m, 4m, null), (Guid.NewGuid(), 2m, 0m, null));

        var result = await w.Repo.CreateFromPrWithSummaryAsync(prUuid, new ConvertPrToPoRequest { SupplierId = w.Supplier, SupplierName = "Acme" }, 1);
        result.Summary.TotalLines.Should().Be(5);
        result.Summary.PricedCount.Should().Be(3, "two from the pricelist, one awarded RFQ");
        result.Summary.UnpricedCount.Should().Be(2);
        result.Summary.Warnings.Should().HaveCount(2).And.OnlyContain(m => !string.IsNullOrWhiteSpace(m));

        var lines = (await StoredPo(w, result.PoUuids.Single())).Lines.OrderBy(l => l.LineNo).ToList();
        (lines[0].PriceSource, lines[0].UnitPrice, lines[0].SupplierPricelistUuid).Should().Be((PriceSource.Pricelist, 12.5m, (Guid?)row.UUID));
        (lines[1].PriceSource, lines[1].UnitPrice).Should().Be((PriceSource.Pricelist, 12.5m));
        (lines[2].PriceSource, lines[2].UnitPrice).Should().Be((PriceSource.RFQ, 8m), "an awarded RFQ's price is contractual");
        (lines[3].PriceSource, lines[3].UnitPrice).Should().Be((PriceSource.Manual, 4m), "no pricelist: the requisition's own estimate stands in as a Manual price");
        (lines[4].PriceSource, lines[4].UnitPrice).Should().Be((PriceSource.Manual, 0m));
        lines.Select(l => l.SourcePrLineUuid).Should().BeEquivalentTo(prLines.Select(p => (Guid?)p.UUID));
        (await StoredPo(w, result.PoUuids.Single())).TotalAmount.Should().Be(10m * 12.5m + 20m * 12.5m + 5m * 8m + 4m);
        (await w.Db.PurchaseRequisitions.SingleAsync()).Status.Should().Be("FULLY_CONVERTED");
    }

    [Fact]
    public async Task TS_44_the_split_conversion_also_reports_one_summary_for_all_its_POs()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.5m));
        var (prUuid, prLines) = await ApprovedPr(w, (w.Product, 10m, 99m, null), (null, 1m, 4m, null));
        var other = Guid.NewGuid();
        var result = await w.Repo.CreateFromPrSplitWithSummaryAsync(prUuid, new ConvertPrSplitRequest
        {
            Lines =
            [
                new PrLineVendorAssignment { PrLineUuid = prLines[0].UUID, SupplierId = w.Supplier, SupplierName = "Acme" },
                new PrLineVendorAssignment { PrLineUuid = prLines[1].UUID, SupplierId = other, SupplierName = "Other" }
            ]
        }, 1);
        result.PoUuids.Should().HaveCount(2);
        (result.Summary.TotalLines, result.Summary.PricedCount, result.Summary.UnpricedCount).Should().Be((2, 1, 1));
    }

    [Fact]
    public async Task The_old_conversion_signatures_still_work_and_return_the_same_POs()
    {
        var w = NewWorld();
        var (prUuid, _) = await ApprovedPr(w, (w.Product, 10m, 99m, null));
        var po = await w.Repo.CreateFromPrAsync(prUuid, new ConvertPrToPoRequest { SupplierId = w.Supplier, SupplierName = "Acme" }, 1);
        (await StoredPo(w, po)).Lines.Single().UnitPrice.Should().Be(99m);
    }

    [Fact]
    public async Task A_deficit_PO_is_priced_from_the_pricelist_when_there_is_one_and_AutoGeneratedPrice_follows()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.5m));
        Task<CreatedPurchaseOrder> Deficit(Guid variant) => w.Repo.CreateFromSaleOrderDeficitAsync(
            new SaleOrderDeficitPo(PurchaseOrderSources.BackToBack, "DRAFT", Guid.NewGuid(), 1, 1, "SO-1", w.Supplier, "Acme", variant, 10m, 99m, null), 1);

        var priced = await Deficit(w.Variant);
        priced.UnitPrice.Should().Be(12.5m);
        var po = await StoredPo(w, priced.Uuid);
        (po.Lines.Single().PriceSource, po.Lines.Single().UnitPrice, po.AutoGeneratedPrice).Should().Be((PriceSource.Pricelist, 12.5m, (decimal?)12.5m));

        var fallback = await Deficit(w.OtherProductVariant);
        var po2 = await StoredPo(w, fallback.Uuid);
        (po2.Lines.Single().PriceSource, po2.Lines.Single().UnitPrice, po2.AutoGeneratedPrice).Should().Be((PriceSource.Manual, 99m, (decimal?)99m), "the selected price is the fallback");
    }

    [Fact]
    public async Task Splitting_prices_the_moved_quantity_for_the_new_supplier_and_re_prices_what_is_left()
    {
        var w = NewWorld();
        var newSupplier = Guid.NewGuid();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m));
        // The new supplier has its own price.
        var theirs = Row(w, 11m); theirs.SupplierUuid = newSupplier;
        await Seed(w, theirs);

        var created = await w.Repo.CreateFromSaleOrderDeficitAsync(
            new SaleOrderDeficitPo(PurchaseOrderSources.BackToBack, "DRAFT", Guid.NewGuid(), 1, 1, "SO-1", w.Supplier, "Acme", w.Variant, 150m, 0m, null), 1);
        (await OnlyLine(w, created.Uuid)).UnitPrice.Should().Be(12.5m);

        var split = await w.Repo.SplitAsync(created.Uuid, new SplitPoRequest
        { LineUuid = (await OnlyLine(w, created.Uuid)).UUID, Quantity = 100m, SupplierId = newSupplier, SupplierName = "Other" }, 1);

        var moved = await OnlyLine(w, split.NewPoUuid);
        (moved.Quantity, moved.UnitPrice, moved.PriceSource, moved.SupplierPricelistUuid).Should().Be((100m, 11m, PriceSource.Pricelist, (Guid?)theirs.UUID));
        var left = await OnlyLine(w, created.Uuid);
        (left.Quantity, left.UnitPrice, left.PriceSource, left.LineTotal).Should().Be((50m, 15m, PriceSource.Pricelist, 750m));
        (await StoredPo(w, split.NewPoUuid)).OrderDate.Should().Be((await StoredPo(w, created.Uuid)).OrderDate);

        // A price typed for the new supplier is kept (Manual).
        var created2 = await w.Repo.CreateFromSaleOrderDeficitAsync(
            new SaleOrderDeficitPo(PurchaseOrderSources.BackToBack, "DRAFT", Guid.NewGuid(), 2, 2, "SO-2", w.Supplier, "Acme", w.Variant, 150m, 0m, null), 1);
        var typed = await w.Repo.SplitAsync(created2.Uuid, new SplitPoRequest
        { LineUuid = (await OnlyLine(w, created2.Uuid)).UUID, Quantity = 40m, SupplierId = Guid.NewGuid(), SupplierName = "Third", UnitPrice = 13.3333m }, 1);
        var typedLine = await OnlyLine(w, typed.NewPoUuid);
        (typedLine.UnitPrice, typedLine.PriceSource).Should().Be((13.3333m, PriceSource.Manual));
    }

    [Fact]
    public async Task A_reissue_carries_every_price_field_of_every_line_untouched()
    {
        var w = NewWorld();
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT" };
        var rate = new DateTime(2026, 10, 15);
        var blanket = (await w.Factory.CreateLineAsync(po, new CreateLineInput
        { LineNo = 1, VariantUuid = w.Variant, ItemDescription = "b", Quantity = 2m, SuppliedPrice = 4.5678m, SuppliedPriceSource = PriceSource.BlanketOrder })).Line;
        var converted = (await w.Factory.CreateLineAsync(po, new CreateLineInput { LineNo = 2, VariantUuid = w.Variant2, ItemDescription = "c", Quantity = 3m, UnitPrice = 5m })).Line;
        converted.PriceSource = PriceSource.Pricelist; converted.SystemResolvedPrice = 54.35m; converted.SupplierPricelistUuid = Guid.NewGuid();
        converted.OriginalCurrencyId = Eur; converted.OriginalCurrencyPrice = 50m; converted.ExchangeRateUsed = 1.087m; converted.ExchangeRateDate = rate;
        po.Lines.Add(blanket); po.Lines.Add(converted);
        po.UUID = Guid.NewGuid(); po.PoNumber = "PO-X"; po.SupplierName = "Acme"; po.CreatedDate = DateTime.UtcNow;
        w.Db.PurchaseOrders.Add(po); await w.Db.SaveChangesAsync();

        var clone = await new PoCloneHandler(w.Db, w.Factory).CloneDocumentAsync(po.UUID);
        var lines = (await StoredPo(w, clone)).Lines.OrderBy(l => l.LineNo).ToList();
        (lines[0].PriceSource, lines[0].UnitPrice).Should().Be((PriceSource.BlanketOrder, 4.5678m));
        (lines[1].PriceSource, lines[1].SystemResolvedPrice, lines[1].SupplierPricelistUuid).Should().Be((PriceSource.Pricelist, (decimal?)54.35m, converted.SupplierPricelistUuid));
        (lines[1].OriginalCurrencyId, lines[1].OriginalCurrencyPrice, lines[1].ExchangeRateUsed, lines[1].ExchangeRateDate).Should().Be((Eur, (decimal?)50m, (decimal?)1.087m, (DateTime?)rate));
        (await StoredPo(w, clone)).OrderDate.Should().Be(DateTime.UtcNow.Date, "a reissue is a new order dated today");
    }

    // ── C10: currency-aware resolution ─────────────────────────────────────────────

    private static PriceResolutionRequest Req(W w, decimal qty = 1m, Guid? poCurrency = null, Guid? legacyCurrency = null) =>
        new() { VariantUuid = w.Variant, SupplierUuid = w.Supplier, Quantity = qty, OrderDate = Oct17, PoCurrencyId = poCurrency, CurrencyId = legacyCurrency };

    [Fact]
    public async Task TS_50_A46_0202_0205_0206_a_EUR_row_on_a_USD_PO_is_converted_with_the_original_and_the_rate_kept()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        var row = (await Seed(w, Row(w, 50m, currency: Eur)))[0];

        var r = await w.Resolver.ResolveAsync(Req(w, poCurrency: Usd));
        r.Found.Should().BeTrue();
        r.UnitPrice.Should().Be(54.35m);
        r.CurrencyId.Should().Be(Usd);
        r.CurrencyCode.Should().Be("USD");
        r.CurrencyConverted.Should().BeTrue();
        r.CurrencyMismatchWarning.Should().BeFalse();
        r.OriginalPrice.Should().Be(50m);
        r.OriginalCurrencyId.Should().Be(Eur);
        r.OriginalCurrencyCode.Should().Be("EUR");
        r.ExchangeRateUsed.Should().Be(1.087m);
        r.ExchangeRateDate.Should().Be(new DateTime(2026, 10, 15));
        r.SupplierPricelistUuid.Should().Be(row.UUID);
        r.WarningMessage.Should().BeNull();
    }

    [Fact]
    public async Task TS_51_A46_0203_no_rate_is_found_true_with_no_price_a_mismatch_flag_and_the_original()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 50m, currency: Eur));
        var r = await w.Resolver.ResolveAsync(Req(w, poCurrency: Usd));
        r.Found.Should().BeTrue();
        r.UnitPrice.Should().BeNull();
        r.CurrencyMismatchWarning.Should().BeTrue();
        r.CurrencyConverted.Should().BeFalse();
        r.OriginalPrice.Should().Be(50m);
        r.OriginalCurrencyCode.Should().Be("EUR");
        r.WarningMessage.Should().Contain("No exchange rate available for EUR → USD on 2026-10-17");
    }

    [Fact]
    public async Task TS_52_A46_0204_0208_a_same_currency_row_or_a_PO_with_no_currency_is_not_converted_at_all()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        await Seed(w, Row(w, 12m, currency: Usd));
        var same = await w.Resolver.ResolveAsync(Req(w, poCurrency: Usd));
        (same.UnitPrice, same.CurrencyConverted, same.OriginalPrice, same.ExchangeRateUsed).Should().Be((12m, false, (decimal?)null, (decimal?)null));
        w.Converter.Calls.Should().Be(0, "BR-36: no rate lookup for the same currency");

        // No PO currency = the purchase base (USD), so a USD row needs nothing either.
        (await w.Resolver.ResolveAsync(Req(w))).UnitPrice.Should().Be(12m);
        w.Converter.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A46_0207_a_cross_rate_EUR_to_GBP_goes_through_the_converter_unchanged()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        w.Converter.Rates[(Usd, Gbp)] = 0.8m;
        await Seed(w, Row(w, 100m, currency: Eur));
        var r = await w.Resolver.ResolveAsync(Req(w, poCurrency: Gbp));
        r.UnitPrice.Should().Be(86.96m, "100 x 1.087 x 0.8");
        r.CurrencyId.Should().Be(Gbp);
        r.ExchangeRateUsed.Should().Be(0.8696m);
    }

    [Fact]
    public async Task E_9_the_A45_currencyId_is_accepted_and_poCurrencyId_wins_when_both_are_sent()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        w.Converter.Rates[(Eur, Gbp)] = 0.86m;
        await Seed(w, Row(w, 50m, currency: Eur));

        (await w.Resolver.ResolveAsync(Req(w, legacyCurrency: Usd))).UnitPrice.Should().Be(54.35m, "the old name still means the PO currency");
        (await w.Resolver.ResolveAsync(Req(w, poCurrency: Gbp, legacyCurrency: Usd))).CurrencyId.Should().Be(Gbp);
    }

    [Fact]
    public async Task E_9_currency_is_not_part_of_matching_so_the_higher_priority_row_wins_whatever_its_currency()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        await Seed(w, Row(w, 50m, priority: 0, currency: Eur), Row(w, 10m, priority: 5, currency: Usd));
        (await w.Resolver.ResolveAsync(Req(w, poCurrency: Usd))).UnitPrice.Should().Be(54.35m);
    }

    [Fact]
    public async Task Without_a_converter_a_row_in_another_currency_is_still_not_a_match_exactly_as_in_A45()
    {
        var w = NewWorld(withConverter: false);
        await Seed(w, Row(w, 12m, currency: Eur));
        var r = await w.Resolver.ResolveAsync(Req(w, poCurrency: Usd));
        r.Found.Should().BeFalse();
        r.WarningMessage.Should().Contain("EUR").And.Contain("USD");
        (await w.Resolver.ResolveAsync(Req(w, poCurrency: Eur))).UnitPrice.Should().Be(12m);
    }

    [Fact]
    public async Task TS_50_56_A46_0201_0209_a_PO_line_priced_across_currencies_stores_the_audit_and_reads_it_back()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        var row = (await Seed(w, Row(w, 50m, currency: Eur)))[0];

        var uuid = await w.Repo.CreateAsync(Po(w, orderDate: Oct17, currency: Usd, lines: Line(w, 2m)), 1);
        var stored = await OnlyLine(w, uuid);
        (stored.PriceSource, stored.UnitPrice, stored.SystemResolvedPrice, stored.LineTotal).Should().Be((PriceSource.Pricelist, 54.35m, (decimal?)54.35m, 108.7m));
        (stored.OriginalCurrencyId, stored.OriginalCurrencyPrice, stored.ExchangeRateUsed, stored.ExchangeRateDate)
            .Should().Be((Eur, (decimal?)50m, (decimal?)1.087m, (DateTime?)new DateTime(2026, 10, 15)));
        stored.SupplierPricelistUuid.Should().Be(row.UUID);

        var read = (await w.Repo.GetByIdAsync(uuid))!.Lines.Single();
        (read.OriginalCurrencyId, read.OriginalCurrencyCode, read.OriginalCurrencyPrice, read.ExchangeRateUsed, read.ExchangeRateDate)
            .Should().Be((Eur, "EUR", (decimal?)50m, (decimal?)1.087m, "2026-10-15"));
    }

    [Fact]
    public async Task A_line_saved_unchanged_after_conversion_stays_Pricelist_with_its_audit_and_a_buyer_edit_is_an_Override()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        var row = (await Seed(w, Row(w, 50m, currency: Eur)))[0];
        var uuid = await w.Repo.CreateAsync(Po(w, currency: Usd, lines: Line(w, 2m)), 1);
        var stored = await OnlyLine(w, uuid);

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest
        { Lines = [Line(w, 2m, 54.35m, 1, row.UUID, 54.35m, uuid: stored.UUID)] }, 1);
        var same = await OnlyLine(w, uuid);
        (same.PriceSource, same.OriginalCurrencyPrice, same.ExchangeRateUsed).Should().Be((PriceSource.Pricelist, (decimal?)50m, (decimal?)1.087m));

        await w.Repo.UpdateAsync(uuid, new PatchPoRequest
        { Lines = [Line(w, 2m, 50m, 1, row.UUID, 54.35m, "Negotiated", uuid: same.UUID)] }, 1);
        var edited = await OnlyLine(w, uuid);
        (edited.PriceSource, edited.UnitPrice, edited.SystemResolvedPrice, edited.PriceOverrideReason).Should().Be((PriceSource.Override, 50m, (decimal?)54.35m, "Negotiated"));
        edited.OriginalCurrencyId.Should().Be(Eur, "the audit of what the pricelist said stays with the Override");
    }

    [Fact]
    public async Task With_no_rate_the_new_line_is_left_for_the_buyer_to_price_by_hand_with_the_warning()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 50m, currency: Eur));
        var po = new PurchaseOrder { SupplierId = w.Supplier, OrderDate = Oct17, Status = "DRAFT", CurrencyId = Usd };
        var made = await w.Factory.CreateLineAsync(po, new CreateLineInput { LineNo = 1, VariantUuid = w.Variant, ItemDescription = "x", Quantity = 1m });
        (made.Line.PriceSource, made.Line.OriginalCurrencyId, made.PriceWasResolved).Should().Be((PriceSource.Manual, (Guid?)null, false));
        made.WarningMessage.Should().Contain("No exchange rate available for EUR → USD");
    }

    [Fact]
    public async Task A46_0231_0233_the_resolve_price_endpoint_accepts_poCurrencyId_and_the_old_currencyId_and_returns_the_conversion_fields()
    {
        var w = NewWorld();
        w.Converter!.Rates[(Eur, Usd)] = 1.087m;
        await Seed(w, Row(w, 50m, currency: Eur));
        var controller = new SupplierPricelistsController(Mock.Of<ISupplierPricelistService>(), w.Resolver, Mock.Of<IAuditService>());

        async Task<ResolvePriceResponse> Call(Guid? poCurrency, Guid? legacy)
        {
            var ok = (Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.ResolvePrice(new ResolvePriceRequest
            { VariantUuid = w.Variant, SupplierUuid = w.Supplier, Quantity = 1m, OrderDate = Oct17, PoCurrencyId = poCurrency, CurrencyId = legacy }, default);
            return ((ApiResponse<ResolvePriceResponse>)ok.Value!).Result!;
        }

        var viaNew = await Call(Usd, null);
        (viaNew.UnitPrice, viaNew.CurrencyCode, viaNew.OriginalPrice, viaNew.OriginalCurrencyCode, viaNew.ExchangeRateUsed, viaNew.ExchangeRateDate, viaNew.CurrencyConverted, viaNew.CurrencyMismatchWarning)
            .Should().Be((54.35m, "USD", (decimal?)50m, "EUR", (decimal?)1.087m, "2026-10-15", true, false));
        (await Call(null, Usd)).UnitPrice.Should().Be(54.35m);

        // Same currency (A45 behaviour): no conversion fields set.
        var same = await Call(Eur, null);
        (same.UnitPrice, same.CurrencyConverted, same.OriginalPrice, same.ExchangeRateUsed, same.ExchangeRateDate).Should().Be((50m, false, (decimal?)null, (decimal?)null, (string?)null));

        w.Converter.Rates.Clear();
        var noRate = await Call(Usd, null);
        (noRate.Found, noRate.UnitPrice, noRate.CurrencyMismatchWarning, noRate.OriginalPrice).Should().Be((true, (decimal?)null, true, (decimal?)50m));
    }

    // ── BR-29: nothing builds a PurchaseOrderLine except the factory ───────────────

    [Fact]
    public void A46_0180_0192_BR_29_no_production_code_constructs_a_PurchaseOrderLine_outside_the_factory()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetFileName(file) == "PurchaseOrderLineFactory.cs") continue;
            var code = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"//.*|/\*[\s\S]*?\*/", "");
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"new\s+(?:[A-Za-z_.]+\.)?PurchaseOrderLine\s*[({]|new\s*\(\s*\)\s*\{[^}]*\}\s*;\s*//\s*PurchaseOrderLine"))
                offenders.Add(Path.GetRelativePath(root, file));
        }
        offenders.Should().BeEmpty("BR-29: every PO line is created by IPurchaseOrderLineFactory");

        var factory = File.ReadAllText(Path.Combine(root, "src", "SMS.Modules.Demand", "Services", "PurchaseOrderLineFactory.cs"));
        System.Text.RegularExpressions.Regex.Matches(factory, @"new\s+PurchaseOrderLine\s*[({]").Count.Should().Be(1, "exactly one construction site, inside the factory");
    }

    [Fact]
    public void The_repository_and_clone_handler_use_the_factory_and_never_assign_a_PriceSource_themselves()
    {
        var root = RepoRoot();
        var repo = File.ReadAllText(Path.Combine(root, "src", "SMS.Modules.Demand", "Repositories", "PurchaseOrderRepository.cs"));
        repo.Should().Contain("_lines.CreateLineAsync").And.Contain("_pricing.ApplyPricingAsync");
        System.Text.RegularExpressions.Regex.IsMatch(repo, @"\bPriceSource\s*=\s*PriceSource\.").Should().BeFalse("the price source is decided in the middleware/factory only");
        File.ReadAllText(Path.Combine(root, "src", "SMS.Modules.Demand", "Services", "PoCloneHandler.cs")).Should().Contain("_lines.CreateLineAsync");
    }

    [Fact]
    public void A46_0182_the_convert_response_carries_pricingSummary_beside_result()
    {
        var json = System.Text.Json.JsonSerializer.Serialize<object>(
            PricedApiResponse<Guid>.Ok(Guid.Empty, new PricingSummary { TotalLines = 5, PricedCount = 3, UnpricedCount = 2, Warnings = ["a", "b"] }),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("result", out _).Should().BeTrue();
        var s = root.GetProperty("pricingSummary");
        (s.GetProperty("totalLines").GetInt32(), s.GetProperty("pricedCount").GetInt32(), s.GetProperty("unpricedCount").GetInt32(), s.GetProperty("warnings").GetArrayLength())
            .Should().Be((5, 3, 2, 2));
    }

    // ── A46-0152: PATCH answers with the per-line pricing result ───────────────────

    private static async Task<(W W, Guid Po, Guid LineUuid, SupplierPricelist[] Rows)> PricedPo(int source, decimal price, string? reason = null)
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 50m, price, source, source == 0 ? null : rows[0].UUID, source == 0 ? null : 15m, reason)), 1);
        return (w, uuid, (await OnlyLine(w, uuid)).UUID, rows);
    }

    [Fact]
    public async Task A46_0152_a_Pricelist_line_re_priced_to_a_new_tier_reports_resolvedChanged_and_no_offer()
    {
        var (w, uuid, line, rows) = await PricedPo(1, 15m);
        var result = await w.Repo.UpdateWithPricingAsync(uuid, new PatchPoRequest { Lines = [Line(w, 200m, 15m, 1, rows[0].UUID, 15m, uuid: line)] }, 1);

        var l = result.Pricing.Lines.Should().ContainSingle().Subject;
        (l.PriceSource, l.UnitPrice, l.SystemResolvedPrice, l.ResolvedChanged).Should().Be((1, 12.5m, (decimal?)12.5m, true));
        l.Offer.Should().BeNull();
        l.Warning.Should().BeNull();
        l.CurrencyMismatchWarning.Should().BeFalse();
        l.LineUuid.Should().Be((await OnlyLine(w, uuid)).UUID);
        (result.Pricing.PricedCount, result.Pricing.UnpricedCount).Should().Be((1, 0));
        result.Pricing.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A46_0152_an_Override_line_whose_system_price_moved_gets_a_KEEP_OR_UPDATE_offer_and_keeps_the_buyers_price()
    {
        var (w, uuid, line, rows) = await PricedPo(2, 11m, "Spot discount");
        var result = await w.Repo.UpdateWithPricingAsync(uuid,
            new PatchPoRequest { Lines = [Line(w, 200m, 11m, 2, rows[0].UUID, 15m, "Spot discount", uuid: line)] }, 1);

        var l = result.Pricing.Lines.Single();
        (l.PriceSource, l.UnitPrice, l.SystemResolvedPrice, l.ResolvedChanged).Should().Be((2, 11m, (decimal?)12.5m, true));
        l.Offer.Should().NotBeNull();
        (l.Offer!.Kind, l.Offer.SystemPrice, l.Offer.YourPrice, l.Offer.Qty).Should().Be(("KEEP_OR_UPDATE", 12.5m, 11m, 200m));
    }

    [Fact]
    public async Task A46_0152_a_Manual_line_that_now_has_a_system_price_gets_a_KEEP_OR_APPLY_offer()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 15m, minQty: 100m));
        var uuid = await w.Repo.CreateAsync(Po(w, lines: Line(w, 5m, 9m)), 1);
        var line = (await OnlyLine(w, uuid)).UUID;

        var result = await w.Repo.UpdateWithPricingAsync(uuid, new PatchPoRequest { Lines = [Line(w, 200m, 9m, uuid: line)] }, 1);

        var l = result.Pricing.Lines.Single();
        (l.PriceSource, l.UnitPrice, l.ResolvedChanged).Should().Be((0, 9m, true));
        (l.Offer!.Kind, l.Offer.SystemPrice, l.Offer.YourPrice, l.Offer.Qty).Should().Be(("KEEP_OR_APPLY", 15m, 9m, 200m));
        (result.Pricing.PricedCount, result.Pricing.UnpricedCount).Should().Be((0, 1));
    }

    [Fact]
    public async Task A46_0152_an_edit_that_changes_nothing_in_the_tier_reports_no_change_and_no_offer()
    {
        var (w, uuid, line, rows) = await PricedPo(2, 11m, "Spot discount");
        var result = await w.Repo.UpdateWithPricingAsync(uuid,
            new PatchPoRequest { Lines = [Line(w, 60m, 11m, 2, rows[0].UUID, 15m, "Spot discount", uuid: line)] }, 1);

        var l = result.Pricing.Lines.Single();
        (l.ResolvedChanged, l.Offer, l.Warning).Should().Be((false, (PoPricingOffer?)null, (string?)null));
    }

    [Fact]
    public async Task A46_0152_a_new_line_nothing_can_price_carries_a_warning_that_is_also_in_pricing_warnings()
    {
        var (w, uuid, line, rows) = await PricedPo(1, 15m);
        var result = await w.Repo.UpdateWithPricingAsync(uuid, new PatchPoRequest
        {
            Lines = [Line(w, 50m, 15m, 1, rows[0].UUID, 15m, uuid: line),
                     new CreatePoLineRequest { VariantUuid = w.OtherProductVariant, ItemDescription = "Gadget", Quantity = 3m }]
        }, 1);

        result.Pricing.Lines.Should().HaveCount(2);
        result.Pricing.Lines[1].Warning.Should().NotBeNullOrWhiteSpace();
        result.Pricing.Lines[1].ResolvedChanged.Should().BeFalse("a brand-new line has nothing to have changed from");
        (result.Pricing.PricedCount, result.Pricing.UnpricedCount).Should().Be((1, 1));
        result.Pricing.Warnings.Should().Equal(result.Pricing.Lines[1].Warning!);
    }

    [Fact]
    public async Task A46_0152_a_header_only_edit_reports_the_stored_lines_and_nothing_changed()
    {
        var (w, uuid, _, _) = await PricedPo(1, 15m);
        var result = await w.Repo.UpdateWithPricingAsync(uuid, new PatchPoRequest { Title = "renamed" }, 1);

        var l = result.Pricing.Lines.Single();
        (l.ResolvedChanged, l.Offer, l.PriceSource).Should().Be((false, (PoPricingOffer?)null, 1));
        result.Pricing.PricedCount.Should().Be(1);
    }

    [Fact]
    public async Task A46_0152_the_old_UpdateAsync_still_returns_the_field_changes()
    {
        var (w, uuid, line, rows) = await PricedPo(1, 15m);
        var changes = await w.Repo.UpdateAsync(uuid, new PatchPoRequest { Lines = [Line(w, 60m, 15m, 1, rows[0].UUID, 15m, uuid: line)] }, 1);
        changes.Should().NotBeNull();
    }

    [Fact]
    public void A46_0152_the_PATCH_response_serialises_pricing_beside_success_and_message()
    {
        var response = new PoPricingApiResponse
        {
            Success = true, Message = "ok",
            Pricing = new PoPricingResult
            {
                PricedCount = 1, UnpricedCount = 0, Warnings = ["w"],
                Lines = [new PoPricingLine
                {
                    LineNo = 1, PriceSource = 2, PriceSourceName = "Override", UnitPrice = 11m, SystemResolvedPrice = 12.5m, ResolvedChanged = true,
                    Offer = new PoPricingOffer { Kind = "KEEP_OR_UPDATE", SystemPrice = 12.5m, YourPrice = 11m, Qty = 200m }
                }]
            }
        };
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize<object>(
            response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("message").GetString().Should().Be("ok");
        var p = root.GetProperty("pricing");
        (p.GetProperty("pricedCount").GetInt32(), p.GetProperty("unpricedCount").GetInt32(), p.GetProperty("warnings").GetArrayLength()).Should().Be((1, 0, 1));
        var line = p.GetProperty("lines")[0];
        (line.GetProperty("priceSource").GetInt32(), line.GetProperty("resolvedChanged").GetBoolean(), line.GetProperty("currencyMismatchWarning").GetBoolean())
            .Should().Be((2, true, false));
        line.TryGetProperty("lineUuid", out _).Should().BeTrue();
        var offer = line.GetProperty("offer");
        (offer.GetProperty("kind").GetString(), offer.GetProperty("systemPrice").GetDecimal(), offer.GetProperty("yourPrice").GetDecimal(), offer.GetProperty("qty").GetDecimal())
            .Should().Be(("KEEP_OR_UPDATE", 12.5m, 11m, 200m));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        // The test binaries may live outside the repo (--artifacts-path), so walk up from this source file instead.
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SMS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SMS.sln not found above the test binaries.");
    }
}




