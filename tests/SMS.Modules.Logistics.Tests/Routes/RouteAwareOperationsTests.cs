using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using Inv = SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 PE-01..03 / T-C5-01..08 in the repo's real statuses (contract §7/§8, D-2, D-3, D-7). A route never adds an edge
/// to the delivery state machine: a step it leaves out is <b>auto-completed</b> by the operation before it — no PACK →
/// confirming the pick boxes everything into one LOOSE handling unit (PACKED); no STAGE → staged as soon as PACKED — so
/// goods issue, booking and invoicing see exactly what they see today. A delivery with no route behaves as today (R-1).
/// Real Inventory ledger and goods-issue poster underneath; real creator, release, pick, pack, issue and collection.
/// </summary>
public class RouteAwareOperationsTests
{
    private const int Store = 42;

    private sealed class World
    {
        public required Guid OrgId;
        public required string DbName;
        public required LogisticsDbContext Log;
        public required DemandDbContext Demand;
        public required InventoryDbContext Inv;
        public required SaleOrderDeliveryCreator Creator;
        public required DeliveryFromSourceRepository FromSource;
        public required DeliveryRepository Deliveries;
        public required DeliveryReleaseRepository Release;
        public required DeliveryStatusRepository Status;
        public required PickListRepository PickLists;
        public required PackageRepository Packages;
        public required GoodsIssueRepository GoodsIssue;
        public required ConsignmentRepository Consignments;
        public required DeliveryRouteService Routes;
        public required IReadOnlyDictionary<string, Guid> RouteIds;
        public required int WarehouseId;

        public void Fresh()
        {
            Log.ChangeTracker.Clear();
            Demand.ChangeTracker.Clear();
            Inv.ChangeTracker.Clear();
        }
    }

    private static async Task<World> NewWorld(Guid? orgId = null, string? dbName = null, bool superAdmin = false)
    {
        var org    = orgId ?? Guid.NewGuid();
        dbName   ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(dbName).Options;

        var log    = LogisticsTestDb.Open(dbName, tenant);
        var demand = new DemandDbContext(Options<DemandDbContext>(), tenant);
        var inv    = new InventoryDbContext(Options<InventoryDbContext>(), tenant);
        var stock  = new StockReservationService(inv);
        var poster = new GoodsIssuePoster(inv, new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance));

        var warehouse = new Inv.Warehouse { Uuid = Guid.NewGuid(), Name = "Karachi Main", Code = "KHI", IsActive = true, CreatedBy = 1 };
        inv.Warehouses.Add(warehouse);
        await inv.SaveChangesAsync();

        await new FulfillmentRouteSeeder(log).EnsureSeededAsync(org);
        AddRoute(log, org, "FULL", "PICK", "PACK", "STAGE", "APPROVAL", "GOODS_ISSUE", "SHIP");
        AddRoute(log, org, "PICK_APPROVE", "PICK", "APPROVAL", "GOODS_ISSUE");
        await log.SaveChangesAsync();
        var routeIds = await log.FulfillmentRoutes.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org).ToDictionaryAsync(r => r.Code, r => r.UUID);

        var numbers    = new DocumentNumberGenerator(log, tenant);
        var addresses  = new AddressNormalizer(new FakeCityLookup());
        var variants   = new ProductVariantResolver(inv);
        var release    = new DeliveryReleaseRepository(log, stock, numbers);
        var goodsIssue = new GoodsIssueRepository(
            log, demand, stock, poster, Mock.Of<ISaleOrderFulfillmentService>(), NullLogger<GoodsIssueRepository>.Instance);

        var world = new World
        {
            OrgId = org, DbName = dbName, Log = log, Demand = demand, Inv = inv,
            Creator     = new SaleOrderDeliveryCreator(log, demand, numbers, variants, stock, new FulfillmentRouteLookup(log)),
            FromSource  = new DeliveryFromSourceRepository(
                log, demand, new WarehouseDbContext(Options<WarehouseDbContext>(), tenant),
                new MaterialDbContext(Options<MaterialDbContext>(), tenant), numbers, addresses, variants, stock),
            Deliveries  = new DeliveryRepository(log, numbers, addresses),
            Release     = release,
            Status      = new DeliveryStatusRepository(log, stock),
            PickLists   = new PickListRepository(log, stock, numbers),
            Packages    = new PackageRepository(log, numbers),
            GoodsIssue  = goodsIssue,
            Consignments = new ConsignmentRepository(log, numbers),
            Routes      = new DeliveryRouteService(log, release, goodsIssue),
            RouteIds    = routeIds,
            WarehouseId = warehouse.Id
        };
        world.Fresh();
        return world;
    }

    private static void AddRoute(LogisticsDbContext db, Guid org, string code, params string[] steps)
    {
        var route = new FulfillmentRoute
        {
            UUID = Guid.NewGuid(), OrganizationId = org, Code = code, Name = code, IsActive = true,
            RequiresPacking = steps.Contains("PACK"), RequiresShipping = steps.Contains("SHIP"),
            DisplayOrder = 50, CreatedDate = DateTime.UtcNow
        };
        for (var i = 0; i < steps.Length; i++)
            route.Steps.Add(new FulfillmentRouteStep { OrganizationId = org, StepCode = steps[i], StepOrder = i + 1 });
        db.FulfillmentRoutes.Add(route);
    }

    private static async Task<Guid> SeedVariant(World w, decimal onHand = 100m)
    {
        var sku = $"V{Guid.NewGuid():N}"[..10];
        var product = new Inv.Product
        {
            Uuid = Guid.NewGuid(), Sku = sku, Name = "Steel Pipes", UomCode = "EA", IsActive = true, CreatedBy = 1,
            Variants = { new Inv.ProductVariant { Uuid = Guid.NewGuid(), Sku = sku, VariantName = "Default", IsDefault = true, IsActive = true, CreatedBy = 1 } }
        };
        w.Inv.Products.Add(product);
        await w.Inv.SaveChangesAsync();
        w.Inv.InventoryItems.Add(new Inv.InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = product.Variants.Single().Id, WarehouseId = w.WarehouseId,
            QtyOnHand = onHand, UnitCost = 25m
        });
        await w.Inv.SaveChangesAsync();
        w.Fresh();
        return product.Variants.Single().Uuid;
    }

    /// <summary>A confirmed order of one line on <paramref name="route"/> (null = no snapshot), and its address.</summary>
    private static async Task<SaleOrder> SeedOrder(World w, string? route, decimal qty = 10m, string mode = "SHIP")
    {
        var address = new Address
        {
            UUID = Guid.NewGuid(), Line1 = "Plot 12, Korangi", CityName = "Karachi", CountryName = "Pakistan",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        w.Log.Addresses.Add(address);
        await w.Log.SaveChangesAsync();

        var order = new SaleOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), SoNumber = $"SO-2026-{Random.Shared.Next(10000, 99999)}",
            PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(), Status = "CONFIRMED",
            DeliveryMode = mode, ShippingAddressId = address.UUID, CreatedBy = 1,
            Lines =
            {
                new SaleOrderLine
                {
                    VariantUuid = await SeedVariant(w), Quantity = qty, UnitPrice = 40m, LineTotal = qty * 40m, Status = "OPEN",
                    FulfillmentMode = "IN_STOCK",
                    FulfillmentRouteUuid = route is null ? null : w.RouteIds[route], FulfillmentRouteCode = route,
                    RouteSource = route is null ? null : "VARIANT"
                }
            }
        };
        w.Demand.SaleOrders.Add(order);
        await w.Demand.SaveChangesAsync();
        w.Fresh();
        return order;
    }

    /// <summary>The auto-created delivery for a one-line order on <paramref name="route"/>.</summary>
    private static async Task<Guid> RoutedDelivery(World w, string route, decimal qty = 10m)
    {
        var so     = await SeedOrder(w, route, qty);
        var line   = so.Lines.Single();
        var result = await w.Creator.CreateForConfirmedOrderAsync(
            w.OrgId, so.UUID, [new SaleOrderLineRoute(line.UUID, w.RouteIds[route])], Store);
        w.Fresh();
        return result.Created.Single().DeliveryUuid;
    }

    private static async Task ReleaseAndPick(World w, Guid delivery, decimal? picked = null)
    {
        (await w.Release.ReleaseAsync(delivery, null, Store)).Should().BeTrue();
        w.Fresh();
        var pickListUuid = await w.PickLists.GenerateAsync(delivery, null, Store);
        w.Fresh();
        var pickList = (await w.PickLists.GetByUuidAsync(pickListUuid))!;
        await w.PickLists.ConfirmAsync(pickListUuid, new ConfirmPickRequest
        {
            Lines = [.. pickList.Lines.Select(l => new ConfirmPickLineRequest
            {
                LineUuid = l.UUID, QtyPicked = picked ?? l.QtyToPick,
                ShortReasonCode = (picked ?? l.QtyToPick) < l.QtyToPick ? "NOT_FOUND" : null
            })]
        }, Store);
        w.Fresh();
    }

    private static async Task PackAll(World w, Guid delivery)
    {
        var packing = (await w.Packages.GetForDeliveryAsync(delivery))!;
        await w.Packages.PackAsync(delivery, new PackRequest
        {
            Contents = [.. packing.Lines.Select(l => new PackContentRequest { DeliveryLineUuid = l.DeliveryLineUuid, Qty = l.QtyToPack })]
        }, Store);
        w.Fresh();
    }

    private static async Task<DeliveryDetailModel> Detail(World w, Guid delivery)
    {
        w.Fresh();
        return (await w.Deliveries.GetByUuidAsync(delivery))!;
    }

    private static string Tracker(DeliveryDetailModel d) =>
        string.Join(" ", d.RouteSteps.Select(s => $"{s.StepCode}:{s.State}"));

    private static Task<PickupResultModel?> Collect(World w, Guid delivery) =>
        w.GoodsIssue.RecordPickupAsync(delivery, new RecordPickupRequest
        {
            PickupPersonName = "Ahmed Raza", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-1234567-1"
        }, Store);

    private static Func<string, bool> Holding(params string[] codes) => c => codes.Contains(c);

    // ── PICK_ONLY (T-C5-01/02) ───────────────────────────────────────────────

    [Fact]
    public async Task T_C5_01_pick_only_confirming_the_pick_packs_into_a_loose_unit_and_stages_so_goods_issue_is_next()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY", qty: 10m);

        await ReleaseAndPick(w, d);

        var detail = await Detail(w, d);
        detail.Status.Should().Be("STAGED", "no PACK → auto LOOSE unit → PACKED; no STAGE → auto-staged");
        detail.Lines.Single().QtyPacked.Should().Be(10m);
        var packing = (await w.Packages.GetForDeliveryAsync(d))!;
        packing.Packages.Should().ContainSingle().Which.PackageType.Should().Be("LOOSE");
        packing.IsFullyPacked.Should().BeTrue();

        Tracker(detail).Should().Be("PICK:DONE GOODS_ISSUE:CURRENT COMPLETE:PENDING");
        detail.NextStep.Should().Be("GOODS_ISSUE");
        detail.NextActions.Should().Equal("GOODS_ISSUE", "RECORD_COLLECTION", "HOLD", "SHORT_CLOSE", "CANCEL");

        var issued = (await w.GoodsIssue.IssueAsync(d, Store))!;
        issued.QtyShipped.Should().Be(10m, "goods issue ships the auto-packed quantity, as it always ships QtyPacked");
    }

    [Fact]
    public async Task T_C5_02_pick_only_ends_at_delivered_through_record_collection()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");
        await ReleaseAndPick(w, d);
        await w.GoodsIssue.IssueAsync(d, Store);
        w.Fresh();

        var gi = await Detail(w, d);
        Tracker(gi).Should().Be("PICK:DONE GOODS_ISSUE:DONE COMPLETE:CURRENT");
        gi.NextStep.Should().Be("COMPLETE");
        gi.NextActions.Should().Equal("RECORD_COLLECTION");

        (await Collect(w, d))!.Status.Should().Be("DELIVERED", "D-2: COMPLETED is DELIVERED, so the order fulfils and invoices");

        var done = await Detail(w, d);
        Tracker(done).Should().Be("PICK:DONE GOODS_ISSUE:DONE COMPLETE:DONE");
        done.NextStep.Should().BeNull();
        done.NextActions.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_collection_straight_from_staged_issues_and_delivers_a_pick_only_delivery()
    {
        // QA's SapKit path: pickup on a STAGED PICK_ONLY delivery does GI + DELIVERED, as today.
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");
        await ReleaseAndPick(w, d);

        var result = (await Collect(w, d))!;

        result.Status.Should().Be("DELIVERED");
        result.GoodsIssue!.QtyShipped.Should().Be(10m);
    }

    // ── PICK_AND_SHIP (T-C5-03/04) ───────────────────────────────────────────

    [Fact]
    public async Task T_C5_03_04_pick_and_ship_goes_from_picked_to_goods_issue_then_ships_by_consignment()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_AND_SHIP");

        await ReleaseAndPick(w, d);
        var staged = await Detail(w, d);
        staged.Status.Should().Be("STAGED");
        staged.NextStep.Should().Be("GOODS_ISSUE");
        staged.NextActions.Should().NotContain("RECORD_COLLECTION", "this route ships");

        await w.GoodsIssue.IssueAsync(d, Store);
        var issued = await Detail(w, d);
        Tracker(issued).Should().Be("PICK:DONE GOODS_ISSUE:DONE SHIP:CURRENT COMPLETE:PENDING");
        issued.NextActions.Should().Equal("CREATE_CONSIGNMENT");

        var consignment = await w.Consignments.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [d] }, Store);
        consignment.Should().NotBeEmpty("the LOOSE unit is a package, so the consignment has something to book");
        (await Detail(w, d)).NextActions.Should().BeEmpty("a consignment already carries it; the carrier moves it on");
    }

    [Fact]
    public async Task Record_collection_is_refused_for_a_route_that_ships()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_AND_SHIP");
        await ReleaseAndPick(w, d);

        var act = () => Collect(w, d);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*PICK_AND_SHIP*ship*");
        (await Detail(w, d)).Status.Should().Be("STAGED", "nothing was issued");
    }

    [Fact]
    public async Task A_consignment_is_refused_for_a_route_without_ship_and_none_is_left_behind()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");
        await ReleaseAndPick(w, d);

        var act = () => w.Consignments.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [d] }, Store);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*PICK_ONLY*");
        (await w.Log.Consignments.CountAsync()).Should().Be(0);
    }

    // ── PICK_PACK_SHIP (T-C5-05/06) ──────────────────────────────────────────

    [Fact]
    public async Task T_C5_05_pick_pack_ship_waits_for_the_packer_after_the_pick()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_PACK_SHIP");

        await ReleaseAndPick(w, d);

        var detail = await Detail(w, d);
        detail.Status.Should().Be("PICKED");
        (await w.Packages.GetForDeliveryAsync(d))!.Packages.Should().BeEmpty("the route packs by hand");
        Tracker(detail).Should().Be("PICK:DONE PACK:CURRENT GOODS_ISSUE:PENDING SHIP:PENDING COMPLETE:PENDING");
        detail.NextActions.Should().Contain("PACK");
    }

    [Fact]
    public async Task T_C5_06_pick_pack_ship_once_packed_is_staged_automatically_so_goods_issue_is_next()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_PACK_SHIP");
        await ReleaseAndPick(w, d);

        await PackAll(w, d);

        var detail = await Detail(w, d);
        detail.Status.Should().Be("STAGED", "no STAGE step → staged as soon as PACKED");
        detail.NextStep.Should().Be("GOODS_ISSUE");
    }

    // ── An auto-staged delivery is still the packer's (REV-04) ───────────────

    private static async Task<Guid> OnlyPackage(World w, Guid delivery) =>
        (await w.Packages.GetForDeliveryAsync(delivery))!.Packages.Single(p => !p.IsVoided).UUID;

    [Fact]
    public async Task REV_04_a_the_auto_loose_unit_of_an_auto_staged_delivery_can_still_be_weighed_and_ships_with_it()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_AND_SHIP");
        await ReleaseAndPick(w, d);
        var package = await OnlyPackage(w, d);

        (await w.Packages.PatchAsync(package, new PatchPackageRequest { GrossWeightKg = 12m }, Store)).Should().BeTrue(
            "a carrier that books by weight needs it, and nobody stood at a pack station to give it");
        w.Fresh();

        await w.GoodsIssue.IssueAsync(d, Store);
        (await w.Packages.GetByUuidAsync(package))!.GrossWeightKg.Should().Be(12m);
    }

    [Fact]
    public async Task REV_04_b_voiding_a_carton_of_an_auto_staged_delivery_reopens_it_and_repacking_stages_it_again()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_PACK_SHIP");
        await ReleaseAndPick(w, d);
        await PackAll(w, d);
        (await Detail(w, d)).Status.Should().Be("STAGED");

        (await w.Packages.VoidAsync(await OnlyPackage(w, d), new DeliveryReasonRequest { Reason = "Wrong carton" }, Store))
            .Should().BeTrue();

        var reopened = await Detail(w, d);
        reopened.Status.Should().Be("PICKED", "the picked units are out of a carton again");
        reopened.NextActions.First().Should().Be("PACK");

        await PackAll(w, d);
        (await Detail(w, d)).Status.Should().Be("STAGED");
    }

    [Fact]
    public async Task REV_04_c_after_goods_issue_the_cartons_are_frozen()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_AND_SHIP");
        await ReleaseAndPick(w, d);
        var package = await OnlyPackage(w, d);
        await w.GoodsIssue.IssueAsync(d, Store);
        w.Fresh();

        var patch = () => w.Packages.PatchAsync(package, new PatchPackageRequest { GrossWeightKg = 12m }, Store);
        var voidIt = () => w.Packages.VoidAsync(package, new DeliveryReasonRequest { Reason = "Too late" }, Store);

        await patch.Should().ThrowAsync<ConflictException>();
        await voidIt.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task REV_04_d_once_on_a_consignment_or_approved_an_auto_staged_delivery_is_frozen_too()
    {
        var w = await NewWorld();
        var onConsignment = await RoutedDelivery(w, "PICK_AND_SHIP");
        await ReleaseAndPick(w, onConsignment);
        await w.Consignments.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [onConsignment] }, Store);
        w.Fresh();
        var patchShipped = async () => await w.Packages.PatchAsync(
            await OnlyPackage(w, onConsignment), new PatchPackageRequest { GrossWeightKg = 3m }, Store);
        await patchShipped.Should().ThrowAsync<ConflictException>("its packages are declared to the carrier now");

        var approved = await RoutedDelivery(w, "PICK_APPROVE");
        await ReleaseAndPick(w, approved);
        await w.Routes.ApproveAsync(approved, Store);
        w.Fresh();
        var patchApproved = async () => await w.Packages.PatchAsync(
            await OnlyPackage(w, approved), new PatchPackageRequest { GrossWeightKg = 3m }, Store);
        await patchApproved.Should().ThrowAsync<ConflictException>("the dispatch was approved as it stood");
    }

    [Fact]
    public async Task REV_04_e_a_delivery_staged_by_hand_on_a_route_with_stage_is_frozen_as_today()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "FULL");
        await ReleaseAndPick(w, d);
        await PackAll(w, d);
        await w.GoodsIssue.StageAsync(d, Store);
        w.Fresh();

        var patch = async () => await w.Packages.PatchAsync(
            await OnlyPackage(w, d), new PatchPackageRequest { GrossWeightKg = 3m }, Store);

        await patch.Should().ThrowAsync<ConflictException>();
    }

    // ── Full route, APPROVAL (T-C5-07, D-7) ──────────────────────────────────

    [Fact]
    public async Task T_C5_07_a_route_with_stage_stops_at_packed_and_advance_stages_it()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "FULL");
        await ReleaseAndPick(w, d);
        await PackAll(w, d);
        (await Detail(w, d)).Status.Should().Be("PACKED");

        var result = (await w.Routes.AdvanceAsync(d, new AdvanceDeliveryRequest { ExpectedStatus = "PACKED" },
            Holding(PermissionCodes.DISPATCH), Store))!;

        result.Should().BeEquivalentTo(new AdvanceDeliveryResultModel { PreviousStatus = "PACKED", Status = "STAGED", Action = "STAGE" });
    }

    [Fact]
    public async Task Goods_issue_waits_for_approve_dispatch_on_a_route_with_approval()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "FULL");
        await ReleaseAndPick(w, d);
        await PackAll(w, d);
        await w.GoodsIssue.StageAsync(d, Store);

        var staged = await Detail(w, d);
        staged.RequiresApproval.Should().BeTrue();
        Tracker(staged).Should().Be("PICK:DONE PACK:DONE STAGE:DONE APPROVAL:CURRENT GOODS_ISSUE:PENDING SHIP:PENDING COMPLETE:PENDING");
        staged.NextActions.Should().Contain("APPROVE").And.NotContain("GOODS_ISSUE");

        var early = () => w.GoodsIssue.IssueAsync(d, Store);
        (await early.Should().ThrowAsync<BadRequestException>()).WithMessage("*approv*");

        var approval = (await w.Routes.ApproveAsync(d, 7))!;
        approval.AlreadyApproved.Should().BeFalse();
        approval.ApprovedBy.Should().Be(7);

        var approved = await Detail(w, d);
        approved.ApprovedAt.Should().NotBeNull();
        approved.ApprovedBy.Should().Be(7);
        approved.NextStep.Should().Be("GOODS_ISSUE");

        (await w.GoodsIssue.IssueAsync(d, Store))!.Status.Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task Record_collection_cannot_skip_the_approval_either()
    {
        // REV: RecordPickupAsync issues through IssueAsync itself; the guard must live there.
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_APPROVE");
        await ReleaseAndPick(w, d);
        (await Detail(w, d)).Status.Should().Be("STAGED");

        var act = () => Collect(w, d);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*approv*");
        (await Detail(w, d)).Status.Should().Be("STAGED");

        await w.Routes.ApproveAsync(d, Store);
        (await Collect(w, d))!.Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task Approve_is_only_for_a_staged_delivery_whose_route_has_approval_and_is_idempotent()
    {
        var w = await NewWorld();
        var plain = await RoutedDelivery(w, "PICK_AND_SHIP");
        await ReleaseAndPick(w, plain);
        var noStep = () => w.Routes.ApproveAsync(plain, Store);
        (await noStep.Should().ThrowAsync<BadRequestException>()).WithMessage("*APPROVAL*");

        var full = await RoutedDelivery(w, "FULL");
        var notStaged = () => w.Routes.ApproveAsync(full, Store);
        (await notStaged.Should().ThrowAsync<BadRequestException>()).WithMessage("*STAGED*");

        await ReleaseAndPick(w, full);
        await PackAll(w, full);
        await w.GoodsIssue.StageAsync(full, Store);
        var first = (await w.Routes.ApproveAsync(full, 7))!;
        w.Fresh();
        var second = (await w.Routes.ApproveAsync(full, 8))!;

        second.AlreadyApproved.Should().BeTrue();
        second.ApprovedAt.Should().Be(first.ApprovedAt);
        second.ApprovedBy.Should().Be(7, "a repeat changes nothing");
    }

    // ── Advance (PE-02) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Advance_releases_a_draft_with_the_release_permission_and_refuses_without_it()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");

        var denied = () => w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DISPATCH), Store);
        await denied.Should().ThrowAsync<ForbiddenException>();
        (await Detail(w, d)).Status.Should().Be("DRAFT");

        var result = (await w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DELIVERY_EDIT), Store))!;
        result.Action.Should().Be("RELEASE");
        result.Status.Should().Be("RELEASED");
    }

    [Fact]
    public async Task Advance_walks_approve_then_goods_issue_on_a_route_with_approval()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "FULL");
        await ReleaseAndPick(w, d);
        await PackAll(w, d);
        await w.GoodsIssue.StageAsync(d, Store);
        w.Fresh();

        var noApprover = () => w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DISPATCH), Store);
        await noApprover.Should().ThrowAsync<ForbiddenException>("the next step is approval, which needs DELIVERY_APPROVE");

        (await w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DELIVERY_APPROVE), Store))!.Action.Should().Be("APPROVE");
        w.Fresh();
        var gi = (await w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DISPATCH), Store))!;
        gi.Action.Should().Be("GOODS_ISSUE");
        gi.Status.Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task Advance_refuses_a_stale_expected_status()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");

        var act = () => w.Routes.AdvanceAsync(d, new AdvanceDeliveryRequest { ExpectedStatus = "PACKED" },
            Holding(PermissionCodes.DELIVERY_EDIT), Store);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*DRAFT*");
    }

    [Theory]
    [InlineData("PICK_PACK_SHIP", "pack")]
    [InlineData("PICK_ONLY", "collection")]
    [InlineData("PICK_AND_SHIP", "consignment")]
    public async Task Advance_names_the_step_that_needs_input_with_a_409(string route, string named)
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, route);
        await ReleaseAndPick(w, d);
        if (route != "PICK_PACK_SHIP") await w.GoodsIssue.IssueAsync(d, Store);
        w.Fresh();

        var act = () => w.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DELIVERY_EDIT, PermissionCodes.DISPATCH), Store);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{named}*");
    }

    [Fact]
    public async Task Advance_and_approve_do_not_reach_another_organizations_delivery()
    {
        var a = await NewWorld();
        var d = await RoutedDelivery(a, "PICK_ONLY");
        var b = await NewWorld(dbName: a.DbName, superAdmin: true);

        (await b.Routes.AdvanceAsync(d, null, Holding(PermissionCodes.DELIVERY_EDIT), Store)).Should().BeNull();
        (await b.Routes.ApproveAsync(d, Store)).Should().BeNull();
        (await Detail(a, d)).Status.Should().Be("DRAFT");
    }

    // ── R-14: another organization's delivery is absent, super admin included ─

    private sealed record Prepared(Guid Delivery, Guid? PickList = null, Guid? Package = null);

    /// <summary>Org A's delivery in the state where <paramref name="op"/> would otherwise succeed.</summary>
    private static async Task<Prepared> PrepareFor(World a, string op)
    {
        switch (op)
        {
            case "detail" or "list" or "so-deliveries" or "patch" or "delete" or "availability" or "release" or "advance":
                return new(await RoutedDelivery(a, "PICK_ONLY"));

            case "pick-list" or "hold":
            {
                var d = await RoutedDelivery(a, "PICK_ONLY");
                await a.Release.ReleaseAsync(d, null, Store);
                a.Fresh();
                return new(d);
            }
            case "pick-list-of-delivery" or "pick-list-by-uuid" or "pick-lists" or "assign-pick-list" or "confirm-pick" or "cancel-pick-list":
            {
                var d = await RoutedDelivery(a, "PICK_ONLY");
                await a.Release.ReleaseAsync(d, null, Store);
                a.Fresh();
                var pl = await a.PickLists.GenerateAsync(d, null, Store);
                a.Fresh();
                return new(d, PickList: pl);
            }
            case "resume":
            {
                var d = await RoutedDelivery(a, "PICK_ONLY");
                await a.Release.ReleaseAsync(d, null, Store);
                a.Fresh();
                await a.Status.HoldAsync(d, new DeliveryReasonRequest { Reason = "Waiting" }, Store);
                a.Fresh();
                return new(d);
            }
            case "pack" or "short-close":
            {
                var d = await RoutedDelivery(a, "PICK_PACK_SHIP");
                await ReleaseAndPick(a, d);
                return new(d);
            }
            case "packages-of-delivery" or "package" or "package-patch" or "package-void" or "stage":
            {
                var d = await RoutedDelivery(a, "FULL");
                await ReleaseAndPick(a, d);
                await PackAll(a, d);
                return new(d, Package: await OnlyPackage(a, d));
            }
            case "approve":
            {
                var d = await RoutedDelivery(a, "PICK_APPROVE");
                await ReleaseAndPick(a, d);
                return new(d);
            }
            case "create-from-sale-order":
            {
                // The order's quantity is outstanding again once its delivery is cancelled.
                var d = await RoutedDelivery(a, "PICK_ONLY");
                await a.Status.CancelAsync(d, new DeliveryReasonRequest { Reason = "Redo" }, Store);
                a.Fresh();
                return new(d);
            }
            case "goods-issue" or "pickup" or "cancel":
            {
                var d = await RoutedDelivery(a, "PICK_ONLY");
                await ReleaseAndPick(a, d);
                return new(d);
            }
            case "consignment":
            {
                var d = await RoutedDelivery(a, "PICK_AND_SHIP");
                await ReleaseAndPick(a, d);
                await a.GoodsIssue.IssueAsync(d, Store);
                a.Fresh();
                return new(d);
            }
            default: throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }

    private static async Task<object?> Run(World a, World b, string op, Prepared p)
    {
        var d = p.Delivery;
        var reason = new DeliveryReasonRequest { Reason = "Not yours" };
        return op switch
        {
            "detail"        => await b.Deliveries.GetByUuidAsync(d),
            "list"          => (await b.Deliveries.GetListAsync(new DeliveryFilter())).Data.Any(x => x.UUID == d) ? "listed" : null,
            "so-deliveries" => (await b.Deliveries.GetForSaleOrderAsync((await Detail(a, d)).SaleOrderUuid!.Value)).Count > 0 ? "listed" : null,
            "patch"         => await b.Deliveries.PatchAsync(d, new PatchDeliveryRequest { Notes = "Not yours" }, Store),
            "delete"        => await b.Deliveries.DeleteAsync(d),
            "availability"  => await b.Release.GetAvailabilityAsync(d),
            "release"       => await b.Release.ReleaseAsync(d, null, Store),
            "advance"       => await b.Routes.AdvanceAsync(d, null, _ => true, Store),
            "pick-list"     => await b.PickLists.GenerateAsync(d, null, Store),
            "pick-list-of-delivery" => await b.PickLists.GetForDeliveryAsync(d),
            "pick-list-by-uuid"     => await b.PickLists.GetByUuidAsync(p.PickList!.Value),
            "pick-lists"            => (await b.PickLists.GetListAsync(new PickListFilter())).Data.Any(x => x.UUID == p.PickList) ? "listed" : null,
            "assign-pick-list"      => await b.PickLists.AssignAsync(p.PickList!.Value, 77, Store),
            "cancel-pick-list"      => await b.PickLists.CancelAsync(p.PickList!.Value, reason, Store),
            "confirm-pick"          => await b.PickLists.ConfirmAsync(p.PickList!.Value, new ConfirmPickRequest
            {
                Lines = [.. (await a.PickLists.GetByUuidAsync(p.PickList!.Value))!.Lines
                    .Select(l => new ConfirmPickLineRequest { LineUuid = l.UUID, QtyPicked = l.QtyToPick })]
            }, Store),
            "pack" => await b.Packages.PackAsync(d, new PackRequest
            {
                Contents = [.. (await Detail(a, d)).Lines.Select(l => new PackContentRequest { DeliveryLineUuid = l.UUID, Qty = l.QtyPicked })]
            }, Store),
            "packages-of-delivery" => await b.Packages.GetForDeliveryAsync(d),
            "package"       => await b.Packages.GetByUuidAsync(p.Package!.Value),
            "package-patch" => await b.Packages.PatchAsync(p.Package!.Value, new PatchPackageRequest { GrossWeightKg = 9m }, Store),
            "package-void"  => await b.Packages.VoidAsync(p.Package!.Value, reason, Store),
            "stage"         => await b.GoodsIssue.StageAsync(d, Store),
            "approve"       => await b.Routes.ApproveAsync(d, Store),
            "goods-issue"   => await b.GoodsIssue.IssueAsync(d, Store),
            "pickup"        => await Collect(b, d),
            "consignment"   => await b.Consignments.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [d] }, Store),
            "hold"          => await b.Status.HoldAsync(d, reason, Store),
            "resume"        => await b.Status.ResumeAsync(d, Store),
            "cancel"        => await b.Status.CancelAsync(d, reason, Store),
            "short-close"   => await b.Status.ShortCloseAsync(d, reason, Store),
            "create-from-sale-order" => await b.FromSource.CreateFromSourceAsync(new CreateDeliveryFromSourceRequest
            {
                SourceType = "SALE_ORDER", SourceUuid = (await Detail(a, d)).SaleOrderUuid!.Value
            }, Store),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
        };
    }

    /// <summary>Everything an operation could change about org A's delivery, as one comparable string.</summary>
    private static async Task<string> Fingerprint(World w, Guid delivery)
    {
        w.Fresh();
        var d = await w.Log.DeliveryOrders.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.UUID == delivery);
        var lines = string.Join(",", d.Lines.OrderBy(l => l.LineNo).Select(l => $"{l.QtyPicked}/{l.QtyPacked}/{l.QtyShipped}/{l.QtyDelivered}"));
        var packages = await w.Log.ShipmentPackages.IgnoreQueryFilters().AsNoTracking().Where(p => p.DeliveryOrderId == d.Id)
            .OrderBy(p => p.Id).Select(p => $"{p.UUID}:{p.IsVoided}:{p.GrossWeightKg}:{p.ModifiedDate}").ToListAsync();
        var pickLists = await w.Log.PickLists.IgnoreQueryFilters().AsNoTracking().Where(p => p.DeliveryOrderId == d.Id)
            .OrderBy(p => p.Id).Select(p => $"{p.UUID}:{p.Status}:{p.AssignedToUserId}:{p.ModifiedDate}").ToListAsync();
        var consignments = await w.Log.Consignments.IgnoreQueryFilters().CountAsync();
        var deliveries   = await w.Log.DeliveryOrders.IgnoreQueryFilters().CountAsync();
        return $"{d.Status}|{d.StatusBeforeHold}|{d.ModifiedDate:o}|{d.IsDelete}|{d.Notes}|{d.ApprovedAt:o}|{d.GoodsIssuedAt:o}|" +
               $"{d.PickedUpAt:o}|{lines}|{string.Join(";", packages)}|{string.Join(";", pickLists)}|{consignments}|{deliveries}";
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("list")]
    [InlineData("so-deliveries")]
    [InlineData("patch")]
    [InlineData("delete")]
    [InlineData("availability")]
    [InlineData("release")]
    [InlineData("advance")]
    [InlineData("pick-list")]
    [InlineData("pick-list-of-delivery")]
    [InlineData("pick-list-by-uuid")]
    [InlineData("pick-lists")]
    [InlineData("assign-pick-list")]
    [InlineData("confirm-pick")]
    [InlineData("cancel-pick-list")]
    [InlineData("pack")]
    [InlineData("packages-of-delivery")]
    [InlineData("package")]
    [InlineData("package-patch")]
    [InlineData("package-void")]
    [InlineData("stage")]
    [InlineData("approve")]
    [InlineData("goods-issue")]
    [InlineData("pickup")]
    [InlineData("consignment")]
    [InlineData("hold")]
    [InlineData("resume")]
    [InlineData("cancel")]
    [InlineData("short-close")]
    [InlineData("create-from-sale-order")]
    public async Task R_14_a_super_admin_of_another_organization_finds_no_delivery_and_changes_nothing(string op)
    {
        var a = await NewWorld();
        var prepared = await PrepareFor(a, op);
        var before = await Fingerprint(a, prepared.Delivery);

        var b = await NewWorld(dbName: a.DbName, superAdmin: true);
        string outcome;
        try
        {
            var result = await Run(a, b, op, prepared);
            outcome = result is null or false ? "absent" : $"acted: {result}";
        }
        catch (NotFoundException) { outcome = "absent"; }
        catch (Exception e)       { outcome = $"{e.GetType().Name}: {e.Message}"; }

        outcome.Should().Be("absent", $"{op}: another organization's delivery is a 404, super admin included (R-14)");
        (await Fingerprint(a, prepared.Delivery)).Should().Be(before, $"{op} must change nothing");
    }

    // ── Cancel (T-C5-08) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("PICK_ONLY")]
    [InlineData("PICK_PACK_SHIP")]
    [InlineData("FULL")]
    public async Task T_C5_08_cancel_works_before_goods_issue_on_any_route_and_gives_the_stock_back(string route)
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, route);
        await ReleaseAndPick(w, d);

        (await w.Status.CancelAsync(d, new DeliveryReasonRequest { Reason = "Customer changed plan" }, Store)).Should().BeTrue();

        var detail = await Detail(w, d);
        detail.Status.Should().Be("CANCELLED");
        var stock = new StockReservationService(w.Inv);
        (await stock.GetBySourceAsync(ReservationSourceType.Delivery, d)).Where(h => h.Status == "ACTIVE")
            .Should().BeEmpty("a cancelled delivery holds nothing");
        (await stock.GetBySourceAsync(ReservationSourceType.SalesOrder, detail.SaleOrderUuid!.Value))
            .Where(h => h.Status == "ACTIVE").Sum(h => h.ReservedQty)
            .Should().Be(10m, "the units go back to the order, whose promise to the customer still stands");
    }

    // ── No route: today's behaviour (R-1) ────────────────────────────────────

    [Fact]
    public async Task A_delivery_with_no_route_is_picked_packed_staged_and_issued_exactly_as_today()
    {
        var w  = await NewWorld();
        var so = await SeedOrder(w, route: null);
        var d  = await w.FromSource.CreateFromSourceAsync(
            new CreateDeliveryFromSourceRequest { SourceType = "SALE_ORDER", SourceUuid = so.UUID }, Store);
        w.Fresh();

        await ReleaseAndPick(w, d);
        var picked = await Detail(w, d);
        picked.Status.Should().Be("PICKED", "no route, no auto-pack");
        picked.FulfillmentRouteUuid.Should().BeNull();
        picked.RouteSteps.Should().BeEmpty();
        picked.NextStep.Should().BeNull();
        picked.RequiresApproval.Should().BeFalse();

        await PackAll(w, d);
        (await Detail(w, d)).Status.Should().Be("PACKED", "no route, no auto-stage");
        await w.GoodsIssue.StageAsync(d, Store);
        (await w.GoodsIssue.IssueAsync(d, Store))!.Status.Should().Be("GOODS_ISSUED", "no route, no approval gate");
        (await Detail(w, d)).NextActions.Should().Equal("CREATE_CONSIGNMENT");
    }

    [Fact]
    public async Task A_short_pick_of_nothing_on_a_route_without_pack_stays_picked_for_short_close()
    {
        var w = await NewWorld();
        var d = await RoutedDelivery(w, "PICK_ONLY");

        await ReleaseAndPick(w, d, picked: 0m);

        var detail = await Detail(w, d);
        detail.Status.Should().Be("PICKED", "an empty carton is not a package");
        (await w.Packages.GetForDeliveryAsync(d))!.Packages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_split_backorder_keeps_the_route()
    {
        var w  = await NewWorld();
        var so = await SeedOrder(w, "PICK_ONLY", qty: 150m);   // 100 on hand
        var created = await w.Creator.CreateForConfirmedOrderAsync(
            w.OrgId, so.UUID, [new SaleOrderLineRoute(so.Lines.Single().UUID, w.RouteIds["PICK_ONLY"])], Store);
        w.Fresh();

        await w.Release.ReleaseAsync(created.Created.Single().DeliveryUuid, new ReleaseDeliveryRequest { OnShortage = "SPLIT" }, Store);
        w.Fresh();

        var backorder = await w.Log.DeliveryOrders.AsNoTracking()
            .SingleAsync(x => x.SaleOrderUuid == so.UUID && x.Status == "DRAFT");
        backorder.FulfillmentRouteUuid.Should().Be(w.RouteIds["PICK_ONLY"]);
        backorder.FulfillmentRouteCode.Should().Be("PICK_ONLY");
        backorder.RouteSteps.Should().Be("PICK,GOODS_ISSUE");
    }
}
