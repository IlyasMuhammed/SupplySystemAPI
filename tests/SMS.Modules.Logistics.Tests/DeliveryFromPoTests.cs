using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-11 — POST /deliveries/from-source for a purchase order (an inbound ASN).
public class DeliveryFromPoTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DemandDbContext Demand,
        DeliveryFromSourceRepository Repo,
        DeliveryRepository Deliveries);

    private static Harness NewHarness(Guid? organizationId = null)
    {
        var orgId  = organizationId ?? Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = orgId };

        var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var db = LogisticsTestDb.Open(dbName, tenant);

        var numbers   = new DocumentNumberGenerator(db, tenant);
        var addresses = new AddressNormalizer(new FakeCityLookup());

        var warehouse = new WarehouseDbContext(
            new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var material = new MaterialDbContext(
            new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        return new Harness(db, demand,
            new DeliveryFromSourceRepository(
                db, demand, warehouse, material, numbers, addresses, new FakeVariantResolver(),
                new FakeStockReservationService()),
            new DeliveryRepository(db, numbers, addresses));
    }

    private static PurchaseOrder SeedPo(
        Harness h, string status = "APPROVED", params (string Desc, decimal Qty, decimal Received)[] lines)
    {
        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            TraceId      = Guid.NewGuid(),
            PoNumber     = "PO-2026-00042",
            Title        = "Cable and fittings",
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Acme Supplies",
            Status       = status,
            DeliveryWarehouseId   = Guid.NewGuid(),
            DeliveryWarehouseName = "Central Warehouse",
            DeliveryDate = new DateTime(2026, 10, 1),
            IsActive     = true,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow
        };

        var items = lines.Length > 0 ? lines : [("4mm cable", 100m, 0m)];
        var lineNo = 1;

        foreach (var (desc, qty, received) in items)
        {
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                VariantUuid     = Guid.NewGuid(),
                ItemDescription = desc,
                UnitOfMeasure   = "M",
                Quantity        = qty,
                QtyReceived     = received,
                UnitPrice       = 250m,
                LineTotal       = qty * 250m
            });
        }

        h.Demand.PurchaseOrders.Add(po);
        h.Demand.SaveChanges();
        h.Demand.ChangeTracker.Clear();
        return po;
    }

    private static CreateDeliveryFromSourceRequest Request(Guid poUuid) =>
        new() { SourceType = "PO", SourceUuid = poUuid };

    // ── TC-11.1 / TC-11.6 / TC-11.7 / TC-11.8 ────────────────────────────────

    [Fact]
    public async Task An_approved_po_becomes_an_inbound_delivery_carrying_every_line()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED",
            ("4mm cable", 100m, 0m), ("Junction box", 40m, 0m), ("Gland 20mm", 200m, 0m));

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Lines.Should().HaveCount(3);
        detail.Lines.Select(l => l.QtyOrdered).Should().Equal(100m, 40m, 200m);
        detail.Lines.Select(l => l.LineNo).Should().Equal(1, 2, 3);

        detail.Direction.Should().Be("INBOUND");
        detail.PostsGoodsIssue.Should().BeFalse("the GRN posts stock on receipt, not the ASN");
        detail.Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task The_delivery_records_where_it_came_from()
    {
        var h  = NewHarness();
        var po = SeedPo(h);

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.SourceType.Should().Be("PO");
        detail.SourceUuid.Should().Be(po.UUID);
        detail.SourceNumber.Should().Be("PO-2026-00042");
        detail.TraceId.Should().Be(po.TraceId,
            "inheriting the trace id is what lets a parcel trace back through GRN → PO → RFQ → PR");
    }

    [Fact]
    public async Task Each_line_points_back_at_the_po_line_it_came_from()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED", ("4mm cable", 100m, 0m), ("Junction box", 40m, 0m));

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        var poLines = po.Lines.OrderBy(l => l.LineNo).ToList();

        detail!.Lines[0].SourceLineUuid.Should().Be(poLines[0].UUID);
        detail.Lines[0].VariantUuid.Should().Be(poLines[0].VariantUuid);
        detail.Lines[0].ItemDescription.Should().Be("4mm cable");
        detail.Lines[0].UnitOfMeasure.Should().Be("M");
        detail.Lines[0].UnitValue.Should().Be(250m);
    }

    [Fact]
    public async Task The_delivery_heads_for_the_pos_delivery_warehouse()
    {
        var h  = NewHarness();
        var po = SeedPo(h);

        var uuid = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        var stored = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        stored.ShipToWarehouseUuid.Should().Be(po.DeliveryWarehouseId);
        stored.RequestedDate.Should().Be(po.DeliveryDate, "the PO's delivery date is the default ask");
    }

    // ── TC-11.2 / TC-11.3 — which POs may be advised ─────────────────────────

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("SENT")]
    [InlineData("PARTIALLY_RECEIVED")]
    public async Task A_po_still_awaiting_goods_can_be_advised(string status)
    {
        var h  = NewHarness();
        var po = SeedPo(h, status);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RECEIVED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task A_po_that_cannot_receive_goods_is_rejected(string status)
    {
        var h  = NewHarness();
        var po = SeedPo(h, status);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"*{status}*")
            .WithMessage("*APPROVED, SENT, PARTIALLY_RECEIVED*");
    }

    // ── TC-11.9 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_po_is_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(Guid.NewGuid()), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── TC-11.4 — already received ───────────────────────────────────────────

    [Fact]
    public async Task Quantities_already_received_are_not_advised_again()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "PARTIALLY_RECEIVED", ("4mm cable", 100m, 40m));

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Lines.Single().QtyOrdered.Should().Be(60m);
    }

    [Fact]
    public async Task A_fully_received_line_is_left_out_entirely()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "PARTIALLY_RECEIVED", ("4mm cable", 100m, 100m), ("Junction box", 40m, 0m));

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Lines.Should().HaveCount(1);
        detail.Lines.Single().ItemDescription.Should().Be("Junction box");
    }

    // ── TC-11.5 — the over-advising defect (F7) ──────────────────────────────

    [Fact]
    public async Task A_second_asn_cannot_claim_quantity_the_first_one_already_advised()
    {
        // The defect this task exists to prevent. A PO line records Quantity and QtyReceived and
        // nothing else — there is no "advised but not yet received" anywhere in the system. Left
        // alone, both ASNs would see the full 60 outstanding and the warehouse would expect 120
        // units of goods when only 60 are coming.
        var h  = NewHarness();
        var po = SeedPo(h, "PARTIALLY_RECEIVED", ("4mm cable", 100m, 40m));

        var first = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);
        (await h.Deliveries.GetByUuidAsync(first))!.Lines.Single().QtyOrdered.Should().Be(60m);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*nothing left to advise*");
    }

    [Fact]
    public async Task A_partial_first_asn_leaves_the_remainder_for_a_second()
    {
        // Goods shipping in waves — the normal case, and it must still work.
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED", ("4mm cable", 100m, 0m));
        var poLine = po.Lines.Single();

        var first = Request(po.UUID);
        first.Lines = [new SourceLineSelection { SourceLineUuid = poLine.UUID, Qty = 30m }];
        await h.Repo.CreateFromSourceAsync(first, User);

        var second = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await h.Deliveries.GetByUuidAsync(second))!.Lines.Single().QtyOrdered.Should().Be(70m);
    }

    [Fact]
    public async Task Cancelling_an_asn_returns_its_quantity_to_the_pool()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED", ("4mm cable", 100m, 0m));

        var first = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == first);
        delivery.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var second = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await h.Deliveries.GetByUuidAsync(second))!.Lines.Single().QtyOrdered.Should().Be(100m,
            "goods on a cancelled advice are not coming, so the quantity is free again");
    }

    [Fact]
    public async Task Receipt_of_advised_goods_does_not_subtract_the_same_units_twice()
    {
        // Why "in flight" qualifies the advised figure. Once an ASN arrives, its goods are booked
        // in by a GRN and land in QtyReceived — counting them as still-advised as well would
        // subtract the same 60 units twice and wrongly block the remaining 40.
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED", ("4mm cable", 100m, 0m));
        var poLine = po.Lines.Single();

        var first = Request(po.UUID);
        first.Lines = [new SourceLineSelection { SourceLineUuid = poLine.UUID, Qty = 60m }];
        var firstUuid = await h.Repo.CreateFromSourceAsync(first, User);

        // The consignment arrives: the ASN is delivered and a GRN books the goods in.
        var delivered = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == firstUuid);
        delivered.Status = "DELIVERED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var tracked = await h.Demand.PurchaseOrderLines.SingleAsync(l => l.UUID == poLine.UUID);
        tracked.QtyReceived = 60m;
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();

        var second = await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await h.Deliveries.GetByUuidAsync(second))!.Lines.Single().QtyOrdered.Should().Be(40m,
            "the 60 that arrived are accounted for once, not twice");
    }

    // ── Line selection ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_caller_can_advise_only_some_lines()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "APPROVED", ("4mm cable", 100m, 0m), ("Junction box", 40m, 0m));
        var second = po.Lines.OrderBy(l => l.LineNo).Last();

        var req = Request(po.UUID);
        req.Lines = [new SourceLineSelection { SourceLineUuid = second.UUID }];

        var detail = await h.Deliveries.GetByUuidAsync(
            await h.Repo.CreateFromSourceAsync(req, User));

        detail!.Lines.Should().HaveCount(1);
        detail.Lines.Single().ItemDescription.Should().Be("Junction box");
        detail.Lines.Single().QtyOrdered.Should().Be(40m, "an omitted quantity means the whole balance");
    }

    [Fact]
    public async Task Advising_more_than_is_outstanding_is_rejected_with_the_real_figure()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "PARTIALLY_RECEIVED", ("4mm cable", 100m, 40m));

        var req = Request(po.UUID);
        req.Lines = [new SourceLineSelection { SourceLineUuid = po.Lines.Single().UUID, Qty = 75m }];

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*only 60*")
            .WithMessage("*75*");
    }

    [Fact]
    public async Task A_line_from_a_different_po_is_rejected()
    {
        var h     = NewHarness();
        var po    = SeedPo(h);
        var other = Guid.NewGuid();

        var req = Request(po.UUID);
        req.Lines = [new SourceLineSelection { SourceLineUuid = other }];

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*does not belong to this purchase order*");
    }

    [Fact]
    public async Task A_fully_received_po_has_nothing_to_advise()
    {
        var h  = NewHarness();
        var po = SeedPo(h, "PARTIALLY_RECEIVED", ("4mm cable", 100m, 100m));

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(po.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*nothing left to advise*");
    }

    // ── Source-type routing ──────────────────────────────────────────────────

    // SRO, MIV and TRANSFER routing is covered by DeliveryFromSroMivTransferTests (T-12).

    [Fact]
    public async Task An_unknown_source_type_lists_the_valid_ones()
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(
            new CreateDeliveryFromSourceRequest { SourceType = "INVOICE", SourceUuid = Guid.NewGuid() }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*PO*");
    }
}
