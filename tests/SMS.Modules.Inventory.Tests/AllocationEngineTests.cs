using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// The shared allocation engine (A30 §14; A30-P1-06..10, P1-16). T-AL01..08 plus the rules,
/// availability and registration behaviour. The race (T-AL07) runs on SQL Server at the end.
/// </summary>
public class AllocationEngineTests
{
    private const int User = 7;
    private static readonly DateTime Day = new(2026, 9, 30);

    private sealed record Harness(
        InventoryDbContext Db, AllocationEngine Engine, StockReservationService Reservations,
        Guid VariantUuid, int VariantId, Guid MainWh, int MainWhId, Guid SecondWh, int SecondWhId);

    private static async Task<Harness> NewAsync(decimal mainOnHand = 0m, decimal secondOnHand = 0m)
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "TSHIRT-PLAIN", Name = "Plain T-Shirt",
            ProductType = SMS.Shared.Common.ProductType.RawMaterial, Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "TSHIRT-PLAIN-1", VariantName = "Default", IsDefault = true, IsActive = true,
            PurchasePrice = 3m, IsAvailableForProduction = true, CreatedBy = 1
        };
        product.Variants.Add(variant);
        db.Products.Add(product);

        var main   = new Domain.Warehouse { Uuid = Guid.NewGuid(), Code = "MAIN", Name = "Main",   IsActive = true, CreatedBy = 1 };
        var second = new Domain.Warehouse { Uuid = Guid.NewGuid(), Code = "SEC",  Name = "Second", IsActive = true, CreatedBy = 1 };
        db.Warehouses.AddRange(main, second);
        await db.SaveChangesAsync();

        if (mainOnHand > 0)
            db.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = main.Id, QtyOnHand = mainOnHand, UnitCost = 3m });
        if (secondOnHand > 0)
            db.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = second.Id, QtyOnHand = secondOnHand, UnitCost = 3m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reservations = new StockReservationService(db);
        return new Harness(db, new AllocationEngine(db, reservations), reservations,
            variant.Uuid, variant.Id, main.Uuid, main.Id, second.Uuid, second.Id);
    }

    private static Task<DemandAllocationSummary> DemandAsync(
        Harness h, decimal qty, string type = AllocationDemandType.SalesOrder, int priority = AllocationPriority.Normal,
        DateTime? requiredDate = null, Guid? warehouse = null, string? reference = null,
        Guid? demandUuid = null, Guid? line = null, DateTime? documentDate = null) =>
        h.Engine.RegisterDemandAsync(new AllocationDemandRegistration(
            type, demandUuid ?? Guid.NewGuid(), line, reference ?? $"{type}-{qty:0.#}", h.VariantUuid, warehouse,
            qty, requiredDate ?? Day, priority, documentDate), User);

    private static Task<Guid> SupplyAsync(Harness h, decimal qty, DateTime? expected = null, Guid? warehouse = null, Guid? supplyUuid = null) =>
        h.Engine.RegisterSupplyAsync(new AllocationSupplyRegistration(
            AllocationSupplyType.PurchaseOrder, supplyUuid ?? Guid.NewGuid(), null, "PO-2026-00001",
            h.VariantUuid, warehouse ?? h.MainWh, qty, expected));

    private static Task<AllocationRunResult> RunAsync(Harness h, Guid? warehouse = null) =>
        h.Engine.AllocateAsync(h.VariantUuid, warehouse, User);

    private static async Task<DemandAllocationSummary> Demand(Harness h, Guid uuid) =>
        (await h.Engine.GetDemandAsync(uuid))!;

    private static async Task<InventoryItem> Item(Harness h, int warehouseId) =>
        await h.Db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == h.VariantId && i.WarehouseId == warehouseId);

    private static async Task ReceiveAsync(Harness h, int warehouseId, decimal qty)
    {
        var item = await h.Db.InventoryItems.SingleAsync(i => i.VariantId == h.VariantId && i.WarehouseId == warehouseId);
        item.QtyOnHand += qty;
        await h.Db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<AllocationSummary>> Active(Harness h, Guid demandRegistryUuid) =>
        (await h.Engine.GetAllocationsAsync(new AllocationListFilter(DemandUuid: demandRegistryUuid, Status: AllocationStatus.Active))).Items;

    // ── T-AL01 — one demand, enough stock ────────────────────────────────────

    [Fact]
    public async Task A_single_demand_with_enough_stock_is_held_in_full()
    {
        var h = await NewAsync(mainOnHand: 10);
        var d = await DemandAsync(h, 6);

        var run = await RunAsync(h);

        run.DemandsEvaluated.Should().Be(1);
        run.QuantityReserved.Should().Be(6);
        run.Shortage.Should().Be(0);

        var demand = await Demand(h, d.Uuid);
        demand.ReservedQty.Should().Be(6);
        demand.Shortage.Should().Be(0);
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(6, "a reserved allocation is a real hold on the counter");

        var record = (await Active(h, d.Uuid)).Single();
        record.AllocationType.Should().Be(AllocationType.Reserved);
        record.SupplyType.Should().Be(AllocationSupplyType.OnHand);
        record.WarehouseUuid.Should().Be(h.MainWh);
        record.PriorityScore.Should().Be(1);

        (await h.Reservations.GetBySourceAsync(ReservationSourceType.Allocation, d.Uuid))
            .Should().ContainSingle(r => r.ReservedQty == 6 && r.Status == StockReservation.StatusActive && r.SourceLineUuid == record.Uuid,
                "the hold is keyed so the engine can find its own rows");
    }

    // ── T-AL02/03 — the priority rules ───────────────────────────────────────

    [Fact]
    public async Task When_stock_is_short_the_higher_business_priority_is_held_first()
    {
        var h = await NewAsync(mainOnHand: 5);
        var normal = await DemandAsync(h, 4, priority: AllocationPriority.Normal, reference: "SO-NORMAL");
        var urgent = await DemandAsync(h, 4, priority: AllocationPriority.Urgent, reference: "SO-URGENT");

        await RunAsync(h);

        (await Demand(h, urgent.Uuid)).ReservedQty.Should().Be(4, "urgent wins even though it was registered second");
        var loser = await Demand(h, normal.Uuid);
        loser.ReservedQty.Should().Be(1);
        loser.Shortage.Should().Be(3);
    }

    [Fact]
    public async Task With_equal_priority_the_earlier_required_date_is_held_first()
    {
        var h = await NewAsync(mainOnHand: 5);
        var later   = await DemandAsync(h, 4, requiredDate: new DateTime(2026, 10, 5));
        var earlier = await DemandAsync(h, 4, requiredDate: new DateTime(2026, 10, 1));

        await RunAsync(h);

        (await Demand(h, earlier.Uuid)).ReservedQty.Should().Be(4);
        (await Demand(h, later.Uuid)).ReservedQty.Should().Be(1);
    }

    [Fact]
    public async Task With_equal_priority_and_date_a_customer_order_beats_a_production_order()
    {
        var h = await NewAsync(mainOnHand: 5);
        var production = await DemandAsync(h, 4, type: AllocationDemandType.ProductionMaterial);
        var sale       = await DemandAsync(h, 4, type: AllocationDemandType.SalesOrder);

        await RunAsync(h);

        (await Demand(h, sale.Uuid)).ReservedQty.Should().Be(4);
        (await Demand(h, production.Uuid)).ReservedQty.Should().Be(1);
    }

    // ── §14.7 — the competing-demands example, and T-AL05 partial supply ─────

    [Fact]
    public async Task The_spec_example_three_demands_three_on_hand_two_on_order()
    {
        var h = await NewAsync(mainOnHand: 3);
        await SupplyAsync(h, 2, expected: new DateTime(2026, 9, 26));
        var a = await DemandAsync(h, 5,  type: AllocationDemandType.ProductionMaterial, requiredDate: new DateTime(2026, 9, 25), reference: "PROD-A");
        var b = await DemandAsync(h, 10, type: AllocationDemandType.SalesOrder, priority: AllocationPriority.High, requiredDate: new DateTime(2026, 9, 23), reference: "SO-B");
        var c = await DemandAsync(h, 5,  type: AllocationDemandType.ProductionMaterial, requiredDate: new DateTime(2026, 9, 24), reference: "PROD-C");

        var run = await RunAsync(h);

        run.QuantityReserved.Should().Be(3);
        run.QuantityPlanned.Should().Be(2);
        run.Shortage.Should().Be(15);

        var salesB = await Demand(h, b.Uuid);
        salesB.ReservedQty.Should().Be(3, "the on-hand units");
        salesB.PlannedQty.Should().Be(2, "the whole incoming purchase order");
        salesB.Shortage.Should().Be(5);

        (await Demand(h, a.Uuid)).Shortage.Should().Be(5);
        (await Demand(h, c.Uuid)).Shortage.Should().Be(5);

        var planned = (await Active(h, b.Uuid)).Single(r => r.AllocationType == AllocationType.Planned);
        planned.SupplyType.Should().Be(AllocationSupplyType.PurchaseOrder);
        planned.SupplyReference.Should().Be("PO-2026-00001");
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(3, "a plan against supply that has not arrived holds nothing");
    }

    [Fact]
    public async Task A_receipt_turns_the_plan_into_a_hold_by_the_rules_not_by_who_raised_the_order()
    {
        // AC-AE02: the purchase order exists because of production A's shortage, but when it lands
        // the higher-priority sales order takes it.
        var h  = await NewAsync(mainOnHand: 3);
        var po = Guid.NewGuid();
        await SupplyAsync(h, 2, expected: new DateTime(2026, 9, 26), supplyUuid: po);
        var a = await DemandAsync(h, 5,  type: AllocationDemandType.ProductionMaterial, requiredDate: new DateTime(2026, 9, 25), reference: "PROD-A");
        var b = await DemandAsync(h, 10, priority: AllocationPriority.High, requiredDate: new DateTime(2026, 9, 23), reference: "SO-B");
        await RunAsync(h);

        await ReceiveAsync(h, h.MainWhId, 2);
        (await h.Engine.SupplyReceivedAsync(AllocationSupplyType.PurchaseOrder, po, null, 2)).Should().BeTrue();
        await RunAsync(h);

        var salesB = await Demand(h, b.Uuid);
        salesB.ReservedQty.Should().Be(5);
        salesB.PlannedQty.Should().Be(0, "the supply arrived, so there is nothing left to plan against");
        salesB.Shortage.Should().Be(5);
        (await Demand(h, a.Uuid)).ReservedQty.Should().Be(0);
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(5);

        (await h.Db.AllocationSupplies.AsNoTracking().SingleAsync()).Status.Should().Be(AllocationSupplyStatus.Received);
        (await h.Engine.SupplyReceivedAsync(AllocationSupplyType.PurchaseOrder, Guid.NewGuid(), null, 1))
            .Should().BeFalse("stock that arrives unannounced is still stock, just not registered supply");
    }

    [Fact]
    public async Task Running_again_changes_nothing()
    {
        var h = await NewAsync(mainOnHand: 3);
        await SupplyAsync(h, 2);
        var d = await DemandAsync(h, 10);

        var first  = await RunAsync(h);
        var second = await RunAsync(h);

        second.QuantityReserved.Should().Be(0, "the hold from the first run is still there");
        (await Demand(h, d.Uuid)).Should().BeEquivalentTo(first.Demands.Single());
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(3);
        (await Active(h, d.Uuid)).Should().HaveCount(2, "one hold and one plan, not two of each");
    }

    // ── AC-AE03 — a hold is a decision; a run never moves it ─────────────────

    [Fact]
    public async Task A_hold_is_not_moved_by_a_run_even_for_a_more_urgent_demand_that_arrives_later()
    {
        var h = await NewAsync(mainOnHand: 4);
        var normal = await DemandAsync(h, 4, priority: AllocationPriority.Normal);
        await RunAsync(h);

        var urgent = await DemandAsync(h, 4, priority: AllocationPriority.Urgent);
        await RunAsync(h);

        (await Demand(h, normal.Uuid)).ReservedQty.Should().Be(4);
        (await Demand(h, urgent.Uuid)).Shortage.Should().Be(4);
    }

    [Fact]
    public async Task A_plan_is_re_made_by_every_run_so_a_more_urgent_demand_takes_the_incoming_supply()
    {
        var h = await NewAsync();
        await SupplyAsync(h, 4);
        var normal = await DemandAsync(h, 4, priority: AllocationPriority.Normal);
        await RunAsync(h);
        (await Demand(h, normal.Uuid)).PlannedQty.Should().Be(4);

        var urgent = await DemandAsync(h, 4, priority: AllocationPriority.Urgent);
        await RunAsync(h);

        (await Demand(h, urgent.Uuid)).PlannedQty.Should().Be(4);
        (await Demand(h, normal.Uuid)).PlannedQty.Should().Be(0);
        (await h.Db.AllocationRecords.CountAsync()).Should().Be(1, "the superseded plan is removed, not kept");
    }

    // ── T-AL06 — cancel, release, reallocate, consume ────────────────────────

    [Fact]
    public async Task Cancelling_a_demand_frees_its_hold_for_the_next_run()
    {
        var h = await NewAsync(mainOnHand: 4);
        var first  = await DemandAsync(h, 4, reference: "SO-FIRST");
        await RunAsync(h);
        var second = await DemandAsync(h, 4, reference: "SO-SECOND");
        await RunAsync(h);
        (await Demand(h, second.Uuid)).Shortage.Should().Be(4);

        await h.Engine.CancelDemandAsync(first.Uuid, "Customer cancelled.", User);

        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(0);
        var cancelled = await Demand(h, first.Uuid);
        cancelled.Status.Should().Be(AllocationDemandStatus.Cancelled);
        cancelled.ReservedQty.Should().Be(0);
        (await h.Engine.GetDemandsAsync(h.VariantUuid)).Should().ContainSingle(d => d.Uuid == second.Uuid);

        await RunAsync(h);
        (await Demand(h, second.Uuid)).ReservedQty.Should().Be(4);

        var act = () => h.Engine.CancelDemandAsync(first.Uuid, "Again.", User);
        await act.Should().NotThrowAsync("cancelling twice is nothing to complain about");
    }

    // ── Allocation dashboard listing — no variant required, product/variant names attached ────

    [Fact]
    public async Task GetDemandsAsync_with_no_filters_returns_every_open_demand_with_product_and_variant_names()
    {
        var h = await NewAsync();
        await DemandAsync(h, 5, warehouse: h.MainWh, reference: "SO-A");
        await DemandAsync(h, 3, warehouse: h.SecondWh, reference: "SO-B");

        var all = await h.Engine.GetDemandsAsync();

        all.Should().HaveCount(2);
        all.Should().OnlyContain(d => d.ProductUuid != null && d.ProductName == "Plain T-Shirt"
            && d.VariantName == "Default" && d.VariantSku == "TSHIRT-PLAIN-1");
    }

    [Fact]
    public async Task GetDemandsAsync_can_narrow_to_one_warehouse_without_naming_a_variant()
    {
        var h = await NewAsync();
        var mainDemand = await DemandAsync(h, 5, warehouse: h.MainWh, reference: "SO-A");
        await DemandAsync(h, 3, warehouse: h.SecondWh, reference: "SO-B");

        var mainOnly = await h.Engine.GetDemandsAsync(warehouseUuid: h.MainWh);

        mainOnly.Should().ContainSingle(d => d.Uuid == mainDemand.Uuid);
    }

    [Fact]
    public async Task Releasing_one_allocation_frees_exactly_that_much()
    {
        var h = await NewAsync(mainOnHand: 10);
        var d = await DemandAsync(h, 6);
        await RunAsync(h);
        var record = (await Active(h, d.Uuid)).Single();

        await h.Engine.ReleaseAsync(record.Uuid, "Held by mistake.", User);

        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(0);
        var demand = await Demand(h, d.Uuid);
        demand.ReservedQty.Should().Be(0);
        demand.Shortage.Should().Be(6);

        var released = (await h.Engine.GetAllocationAsync(record.Uuid))!;
        released.Status.Should().Be(AllocationStatus.Released);
        released.ReleaseReason.Should().Be("Held by mistake.");
    }

    [Fact]
    public async Task Reallocating_moves_the_hold_between_demands_and_the_counter_is_unchanged()
    {
        var h = await NewAsync(mainOnHand: 4);
        var a = await DemandAsync(h, 4, reference: "SO-A");
        await RunAsync(h);
        var b = await DemandAsync(h, 4, priority: AllocationPriority.Urgent, reference: "SO-B");
        await RunAsync(h);
        var held = (await Active(h, a.Uuid)).Single();

        var moved = await h.Engine.ReallocateAsync(held.Uuid, b.Uuid, 3, "Board decision.", User);

        moved.AllocationType.Should().Be(AllocationType.Reserved);
        moved.AllocatedQty.Should().Be(3);
        moved.PriorityScore.Should().Be(0, "a manual move is outside the priority order");
        (await Demand(h, a.Uuid)).ReservedQty.Should().Be(1);
        (await Demand(h, b.Uuid)).ReservedQty.Should().Be(3);
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(4, "stock changed hands, not state");
        (await h.Reservations.GetBySourceAsync(ReservationSourceType.Allocation, b.Uuid))
            .Should().ContainSingle(r => r.ReservedQty == 3 && r.Status == StockReservation.StatusActive);

        var tooMuch = () => h.Engine.ReallocateAsync(held.Uuid, b.Uuid, 5, "Greedy.", User);
        await tooMuch.Should().ThrowAsync<BadRequestException>().WithMessage("*is left to move*");

        var moreThanNeeded = async () =>
        {
            var c = await DemandAsync(h, 1, reference: "SO-C");
            await h.Engine.ReallocateAsync((await Active(h, b.Uuid)).Single().Uuid, c.Uuid, 2, "Too much.", User);
        };
        await moreThanNeeded.Should().ThrowAsync<BadRequestException>().WithMessage("*only needs 1 more*");
    }

    [Fact]
    public async Task Consuming_marks_the_hold_used_and_fulfils_the_demand()
    {
        var h = await NewAsync(mainOnHand: 10);
        var d = await DemandAsync(h, 6);
        await RunAsync(h);
        var record = (await Active(h, d.Uuid)).Single();

        (await h.Engine.ConsumeAsync(record.Uuid, 4, User)).Should().Be(4);

        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(2, "consumed stock is no longer held; the goods issue takes it off hand");
        var partly = await Demand(h, d.Uuid);
        partly.ConsumedQty.Should().Be(4);
        partly.ReservedQty.Should().Be(2);
        partly.Status.Should().Be(AllocationDemandStatus.Open);

        (await h.Engine.ConsumeAsync(record.Uuid, 2, User)).Should().Be(2);

        (await h.Engine.GetAllocationAsync(record.Uuid))!.Status.Should().Be(AllocationStatus.Consumed);
        var done = await Demand(h, d.Uuid);
        done.Status.Should().Be(AllocationDemandStatus.Fulfilled);
        done.Shortage.Should().Be(0);
        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(0);

        // Consuming took the hold off the counter, not the stock off the shelf (the goods issue does
        // that), so all 10 are free again: a demand for 15 gets 10 held and 5 planned.
        var planOnly = async () =>
        {
            await SupplyAsync(h, 5);
            var e = await DemandAsync(h, 15, reference: "SO-E");
            await RunAsync(h);
            var planned = (await Active(h, e.Uuid)).Single(r => r.AllocationType == AllocationType.Planned);
            await h.Engine.ConsumeAsync(planned.Uuid, 1, User);
        };
        await planOnly.Should().ThrowAsync<BadRequestException>().WithMessage("*has not arrived*");
    }

    // ── Warehouses ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unpinned_demand_draws_from_the_warehouse_with_the_most_free_stock_and_a_pinned_one_only_from_its_own()
    {
        var h = await NewAsync(mainOnHand: 2, secondOnHand: 8);
        var anywhere = await DemandAsync(h, 5, reference: "SO-ANY");
        var mainOnly = await DemandAsync(h, 5, warehouse: h.MainWh, reference: "SO-MAIN");

        await RunAsync(h);

        (await Active(h, anywhere.Uuid)).Single().WarehouseUuid.Should().Be(h.SecondWh);
        (await Item(h, h.SecondWhId)).QtyReserved.Should().Be(5);
        var pinned = await Demand(h, mainOnly.Uuid);
        pinned.ReservedQty.Should().Be(2);
        pinned.Shortage.Should().Be(3, "the second warehouse's stock is not this demand's to take");
    }

    [Fact]
    public async Task An_unpinned_demand_larger_than_any_one_warehouse_is_held_across_them()
    {
        var h = await NewAsync(mainOnHand: 2, secondOnHand: 8);
        var d = await DemandAsync(h, 9);

        await RunAsync(h);

        var records = await Active(h, d.Uuid);
        records.Should().HaveCount(2);
        records.Single(r => r.WarehouseUuid == h.SecondWh).AllocatedQty.Should().Be(8);
        records.Single(r => r.WarehouseUuid == h.MainWh).AllocatedQty.Should().Be(1);
        (await Demand(h, d.Uuid)).Shortage.Should().Be(0);
    }

    [Fact]
    public async Task A_run_scoped_to_one_warehouse_leaves_the_others_alone()
    {
        var h = await NewAsync(mainOnHand: 5, secondOnHand: 5);
        var d = await DemandAsync(h, 8);

        await RunAsync(h, warehouse: h.MainWh);

        (await Item(h, h.MainWhId)).QtyReserved.Should().Be(5);
        (await Item(h, h.SecondWhId)).QtyReserved.Should().Be(0);
        (await Demand(h, d.Uuid)).Shortage.Should().Be(3);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_the_same_demand_line_again_updates_it_rather_than_adding_a_second()
    {
        var h = await NewAsync(mainOnHand: 10);
        var doc  = Guid.NewGuid();
        var line = Guid.NewGuid();
        var first = await DemandAsync(h, 5, demandUuid: doc, line: line);
        await RunAsync(h);

        var again = await DemandAsync(h, 8, demandUuid: doc, line: line, priority: AllocationPriority.High);
        await RunAsync(h);

        again.Uuid.Should().Be(first.Uuid);
        var demand = await Demand(h, first.Uuid);
        demand.RequiredQty.Should().Be(8);
        demand.Priority.Should().Be(AllocationPriority.High);
        demand.ReservedQty.Should().Be(8, "the run topped the hold up from 5 to 8");
        (await h.Engine.GetDemandsAsync(h.VariantUuid)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Bad_registrations_are_refused()
    {
        var h = await NewAsync();

        var unknownType = () => DemandAsync(h, 1, type: "WISHLIST");
        await unknownType.Should().ThrowAsync<BadRequestException>().WithMessage("*not a demand type*");

        var nothing = () => DemandAsync(h, 0);
        await nothing.Should().ThrowAsync<BadRequestException>().WithMessage("*more than zero*");

        var badPriority = () => DemandAsync(h, 1, priority: 9);
        await badPriority.Should().ThrowAsync<BadRequestException>().WithMessage("*Priority*");

        var noSuchVariant = () => h.Engine.RegisterDemandAsync(new AllocationDemandRegistration(
            AllocationDemandType.SalesOrder, Guid.NewGuid(), null, "SO-X", Guid.NewGuid(), null, 1, Day), User);
        await noSuchVariant.Should().ThrowAsync<BadRequestException>().WithMessage("*Variant*");

        var onHandSupply = () => h.Engine.RegisterSupplyAsync(new AllocationSupplyRegistration(
            AllocationSupplyType.OnHand, Guid.NewGuid(), null, "X", h.VariantUuid, h.MainWh, 1, null));
        await onHandSupply.Should().ThrowAsync<BadRequestException>().WithMessage("*not an expected supply type*");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_organization_can_replace_the_priority_rules_and_go_back_to_the_defaults()
    {
        var h = await NewAsync(mainOnHand: 4);
        (await h.Engine.GetRulesAsync()).Should().HaveCount(5, "the built-in defaults");

        var rules = await h.Engine.SetRulesAsync(
            [new AllocationRuleDefinition("Date only", 1, null, AllocationSortField.RequiredDate, AllocationSortDirection.Ascending, true)]);
        rules.Should().ContainSingle(r => r.RuleName == "Date only");

        var urgentLater = await DemandAsync(h, 4, priority: AllocationPriority.Urgent, requiredDate: new DateTime(2026, 10, 5));
        var lowSooner   = await DemandAsync(h, 4, priority: AllocationPriority.Low,    requiredDate: new DateTime(2026, 10, 1));
        await RunAsync(h);

        (await Demand(h, lowSooner.Uuid)).ReservedQty.Should().Be(4, "under these rules the date is all that counts");
        (await Demand(h, urgentLater.Uuid)).ReservedQty.Should().Be(0);

        (await h.Engine.SetRulesAsync([])).Should().HaveCount(5);

        var badField = () => h.Engine.SetRulesAsync(
            [new AllocationRuleDefinition("Nope", 1, null, "MOOD", AllocationSortDirection.Ascending, true)]);
        await badField.Should().ThrowAsync<BadRequestException>().WithMessage("*not a sort field*");
    }

    // ── Availability ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Availability_reports_on_hand_reserved_available_incoming_and_what_nothing_covers()
    {
        var h = await NewAsync(mainOnHand: 10);
        await DemandAsync(h, 6);
        await SupplyAsync(h, 4);
        await DemandAsync(h, 8);
        await RunAsync(h);

        var before = await h.Engine.GetAvailabilityAsync(h.VariantUuid, null);
        before.OnHand.Should().Be(10);
        before.Reserved.Should().Be(10);
        before.Available.Should().Be(0);
        before.Incoming.Should().Be(4);
        before.OpenDemand.Should().Be(14);
        before.Unallocated.Should().Be(0, "6 + 4 held, 4 planned");

        await DemandAsync(h, 3);
        await RunAsync(h);

        (await h.Engine.GetAvailabilityAsync(h.VariantUuid, null)).Unallocated.Should().Be(3);
        (await h.Engine.GetAvailabilityAsync(h.VariantUuid, h.SecondWh)).OnHand.Should().Be(0);
    }

    // ── T-AL07 — the race, on a real database ────────────────────────────────

    [SqlServerFact]
    public async Task Two_writers_racing_for_the_last_unit_cannot_both_hold_it()
    {
        // The engine reads what is free, then holds it. Between those two steps somebody else takes
        // the unit. The engine's write fails the version check, it forgets what it read, runs again,
        // and this time finds nothing free — so the counter never exceeds what is on hand.
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 1);

        Guid demandUuid;
        await using (var setup = harness.NewContext(stock.OrganizationId))
        {
            var registered = await new AllocationEngine(setup, new StockReservationService(setup)).RegisterDemandAsync(
                new AllocationDemandRegistration(AllocationDemandType.SalesOrder, Guid.NewGuid(), null, "SO-RACE",
                    stock.VariantUuid, stock.WarehouseUuid, 1, Day), User);
            demandUuid = registered.Uuid;
        }

        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            var taken = await new StockReservationService(other).ReserveAsync(
                ReservationSourceType.Mir, Guid.NewGuid(), [new ReservationRequest(stock.VariantUuid, stock.WarehouseUuid, 1)], User);
            taken.Succeeded.Should().BeTrue();
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);
        var run = await new AllocationEngine(db, new StockReservationService(db)).AllocateAsync(stock.VariantUuid, null, User);

        competitor.Fired.Should().Be(1);
        run.QuantityReserved.Should().Be(0);
        run.Shortage.Should().Be(1);

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyReserved
            .Should().Be(1, "exactly one hold on one unit");
        (await check.StockReservations.Where(r => r.Status == StockReservation.StatusActive).ToListAsync())
            .Should().ContainSingle().Which.SourceType.Should().Be(ReservationSourceType.Mir);
        (await check.AllocationRecords.CountAsync(r => r.Status == AllocationStatus.Active)).Should().Be(0);
        (await new AllocationEngine(check, new StockReservationService(check)).GetDemandAsync(demandUuid))!.Shortage.Should().Be(1);
    }

    // ── A30-P5-08 §32.2 — "Concurrent Production Order planning for same BOM" ──
    //
    // The engine does not know or care whether a demand came from a sale order or a production
    // order's own material requirement — the RowVersion-and-retry protection T-AL07 already proves
    // lives entirely inside AllocateAsync, agnostic to the caller. This is the same race, with two
    // PRODUCTION_MATERIAL demands standing in for two production orders whose plans both need the
    // same last unit of a shared material, proving the claim rather than just extending it by
    // argument.

    [SqlServerFact]
    public async Task Two_production_orders_planning_against_the_same_material_cannot_both_hold_the_last_unit()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 1);

        Guid firstDemandUuid, secondDemandUuid;
        await using (var setup = harness.NewContext(stock.OrganizationId))
        {
            var engine = new AllocationEngine(setup, new StockReservationService(setup));
            firstDemandUuid = (await engine.RegisterDemandAsync(
                new AllocationDemandRegistration(AllocationDemandType.ProductionMaterial, Guid.NewGuid(), Guid.NewGuid(), "PROD-A",
                    stock.VariantUuid, stock.WarehouseUuid, 1, Day), User)).Uuid;
            secondDemandUuid = (await engine.RegisterDemandAsync(
                new AllocationDemandRegistration(AllocationDemandType.ProductionMaterial, Guid.NewGuid(), Guid.NewGuid(), "PROD-B",
                    stock.VariantUuid, stock.WarehouseUuid, 1, Day), User)).Uuid;
        }

        // The competitor is a second production order's own plan, racing the first order's run for
        // the same last unit — not another module's reservation this time.
        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            var taken = await new StockReservationService(other).ReserveAsync(
                ReservationSourceType.Allocation, Guid.NewGuid(), [new ReservationRequest(stock.VariantUuid, stock.WarehouseUuid, 1)], User);
            taken.Succeeded.Should().BeTrue();
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);
        var run = await new AllocationEngine(db, new StockReservationService(db)).AllocateAsync(stock.VariantUuid, null, User);

        competitor.Fired.Should().Be(1);
        // Both demands together want 2 units against 1 on hand: the run's own retry re-plans from a
        // fresh read that finds the unit already gone, so nothing it reserves ever exceeds stock.
        run.QuantityReserved.Should().Be(0);

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyReserved
            .Should().Be(1, "the competing plan's own hold is the only one that landed");
        (await check.AllocationRecords.CountAsync(r => r.Status == AllocationStatus.Active)).Should().Be(0,
            "neither production order's own demand got a real hold from this run");
    }
}
