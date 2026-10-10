using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A37 (OPSB) — Demand's side of module-aware routes (RTE-01..03: refusal of an unavailable line route, fallback to the
/// org default with a warning, no production at confirm), the D-9 gate on Demand's jobs, and the D-18 impact provider.
/// The unavailable flag comes from Logistics' lookup (FulfillmentRouteSummary.IsAvailable), faked here.
/// </summary>
public class ModuleRegistryDemandTests
{
    private const int User = A34SoHarness.User;

    private sealed class FakeGate : IModuleGate
    {
        public readonly HashSet<(Guid, string)> Off = [];
        public FakeGate SwitchOff(Guid org, string code) { Off.Add((org, code)); return this; }
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(!Off.Contains((organizationId, featureCode)));
    }

    /// <summary>Manufacturing switched off: Logistics' lookup reports both manufacture routes unavailable.</summary>
    private static A34SoHarness ManufacturingOff()
    {
        var h = A34SoHarness.Create(manufacturing: false);
        foreach (var route in new[] { h.MfgShip, h.MfgPickup })
            h.Routes.Routes[route.Uuid] = (h.OrgId, route with
            {
                IsAvailable = false, UnavailableReason = FulfillmentRouteAvailability.ManufacturingOffReason
            });
        return h;
    }

    // ── RTE-02 / RTE-03 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RTE_02_a_variant_on_an_unavailable_route_falls_back_to_the_org_default_with_a_warning()
    {
        var h = ManufacturingOff();
        var so = await h.DraftAsync((h.MakeToOrderVariant(), null));

        var model = (await h.Service.GetByIdAsync(so))!;

        var line = model.Lines.Single();
        line.RouteSource.Should().Be(FulfillmentRouteSource.OrgDefault);
        line.EffectiveRouteCode.Should().Be("PICK_AND_SHIP");
        line.EffectiveRouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        line.RouteBlocker.Should().BeNull();
        line.RouteWarning.Should().Be(
            "Fulfillment route 'MFG_PICK_SHIP' is not available (Manufacturing is switched off); the default route 'PICK_AND_SHIP' is used instead.");
        model.ConfirmBlockers.Should().BeEmpty();
    }

    [Fact]
    public async Task RTE_02_confirm_raises_no_production_order_and_snapshots_the_stock_fallback()
    {
        var h = ManufacturingOff();
        var variant = h.MakeToOrderVariant();
        h.Available[variant] = 100m;
        var so = await h.DraftAsync((variant, null));

        await h.Service.ConfirmWithResultAsync(so, User);

        var order = await h.ReadAsync(so);
        order.Status.Should().Be("CONFIRMED");
        order.ProductionCreationPendingSince.Should().BeNull();
        h.Production.CreateCalls.Should().BeEmpty();
        order.Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickAndShip.Uuid);
        order.Lines.Single().FulfillmentRouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        h.Reserved.Should().ContainSingle("the line is now a stock line and is reserved");
    }

    [Fact]
    public async Task RTE_03_with_no_default_the_line_blocks_confirmation_and_says_why()
    {
        var h = ManufacturingOff();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var so = await h.DraftAsync((h.MakeToOrderVariant(), null));

        var line = (await h.Service.GetByIdAsync(so))!.Lines.Single();

        line.RouteBlocker.Should().Be(ConfirmBlockerCodes.RouteMissing);
        line.RouteWarning.Should().Contain("'MFG_PICK_SHIP' is not available").And.Contain("no default route");
        await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, User)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task RTE_01_an_unavailable_route_cannot_be_put_on_a_sale_order_line()
    {
        var h = ManufacturingOff();

        var act = () => h.DraftAsync((Guid.NewGuid(), h.MfgShip.Uuid));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should()
            .Contain("Manufacturing is switched off for your organization.");
    }

    [Fact]
    public async Task An_available_manufacture_route_is_not_touched()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((h.MakeToOrderVariant(), null));

        var line = (await h.Service.GetByIdAsync(so))!.Lines.Single();

        line.EffectiveRouteCode.Should().Be("MFG_PICK_SHIP");
        line.RouteWarning.Should().BeNull();
    }

    [Fact]
    public async Task The_resolver_asks_the_module_gate_for_manufacturing_when_one_is_registered()
    {
        var h = A34SoHarness.Create();   // the snapshot says manufacturing is on
        var variant = h.MakeToOrderVariant();
        var resolver = new EffectiveRouteResolver(h.Routes, h.Routes, h.Tenants, h.Readiness,
            new FakeGate().SwitchOff(h.OrgId, ModuleCodes.Manufacturing));

        var resolution = await resolver.ResolveAsync(h.OrgId, new RouteOrderContext("SHIP", Guid.NewGuid(), true, false),
            [new RouteLineInput(null, 1, variant, 1m, null, null)]);

        resolution.Lines.Single().Blocker.Should().Be(ConfirmBlockerCodes.ManufacturingDisabled, "grace counts as off");
    }

    // ── D-9 jobs ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Production_sweep_leaves_orders_of_an_organization_without_manufacturing_pending()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnCreate = new InvalidOperationException("down");
        var so = await h.DraftAsync((mto, null));
        await h.Service.ConfirmWithResultAsync(so, User);
        h.Production.ThrowOnCreate = null;
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.ProductionCreationPendingSince = DateTime.UtcNow.AddMinutes(-30);
        await h.Db.SaveChangesAsync();

        var sweep = new SaleOrderProductionSweepJob(h.Db, Mock.Of<Hangfire.IBackgroundJobClient>(),
            NullLogger<SaleOrderProductionSweepJob>.Instance, h.Production, h.LeadTimes, h.Tenants,
            gate: new FakeGate().SwitchOff(h.OrgId, ModuleCodes.Manufacturing));

        (await sweep.RunAsync()).Should().Be(0);
        h.Production.Pos.Should().BeEmpty();
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().NotBeNull("it resumes when the module is back");
    }

    [Fact]
    public async Task Quotation_expiry_skips_an_organization_without_demand()
    {
        var (on, off) = (Guid.NewGuid(), Guid.NewGuid());
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = on });
        var today = new DateTime(2026, 11, 2);
        SaleQuotation Q(Guid org, string number) => new()
        {
            OrganizationId = org, QuotationNumber = number, PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
            Status = "SENT", ValidFrom = today.AddDays(-30), ValidTo = today.AddDays(-1), CreatedBy = 1
        };
        db.SaleQuotations.AddRange(Q(on, "ON"), Q(off, "OFF"));
        await db.SaveChangesAsync();

        var job = new QuotationExpiryJob(db, NullLogger<QuotationExpiryJob>.Instance, new FakeGate().SwitchOff(off, ModuleCodes.Demand));

        (await job.RunAsync(today)).Should().Be(1);
        var status = await db.SaleQuotations.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(q => q.QuotationNumber, q => q.Status);
        status.Should().Equal(new Dictionary<string, string> { ["ON"] = "EXPIRED", ["OFF"] = "SENT" });
    }

    [Fact]
    public async Task Rfq_link_expiry_skips_an_organization_without_demand()
    {
        var (on, off) = (Guid.NewGuid(), Guid.NewGuid());
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = on });
        RfqAccessLink Link(Guid org) => new()
        {
            OrganizationId = org, QuotationId = 1, SupplierId = Guid.NewGuid(), ContactId = 1, TokenHash = Guid.NewGuid().ToString("N"),
            Status = "PENDING", GeneratedAt = DateTime.UtcNow.AddDays(-10), ExpiresAt = DateTime.UtcNow.AddDays(-1), CreatedBy = 1
        };
        db.RfqAccessLinks.AddRange(Link(on), Link(off));
        await db.SaveChangesAsync();

        await new RfqLinkExpiryJob(db, new FakeGate().SwitchOff(off, ModuleCodes.Demand)).RunAsync();

        var links = await db.RfqAccessLinks.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        links.Single(l => l.OrganizationId == on).Status.Should().Be("EXPIRED");
        links.Single(l => l.OrganizationId == off).Status.Should().Be("PENDING");
    }

    // ── D-18 impact ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Impact_counts_open_purchase_and_sale_orders_of_the_organization_only()
    {
        var h = A34SoHarness.Create();
        await h.DraftAsync((Guid.NewGuid(), null));
        var cancelled = await h.DraftAsync((Guid.NewGuid(), null));
        await h.Service.CancelAsync(cancelled, User, null);
        h.Db.PurchaseOrders.AddRange(
            new PurchaseOrder { OrganizationId = h.OrgId, PoNumber = "PO-1", Status = "SENT" },
            new PurchaseOrder { OrganizationId = h.OrgId, PoNumber = "PO-2", Status = "CLOSED" },
            new PurchaseOrder { OrganizationId = h.OrgId, PoNumber = "PO-3", Status = "DRAFT", IsDelete = true },
            new PurchaseOrder { OrganizationId = Guid.NewGuid(), PoNumber = "PO-4", Status = "SENT" });
        await h.Db.SaveChangesAsync();

        var impact = new DemandModuleImpact(h.Db);
        var items = await impact.GetInProgressAsync(h.OrgId);

        impact.ModuleCode.Should().Be("MODULE_DEMAND");
        items.Should().BeEquivalentTo([new ModuleImpactItem("Open purchase orders", 1), new ModuleImpactItem("Open sale orders", 1)]);
    }
}
