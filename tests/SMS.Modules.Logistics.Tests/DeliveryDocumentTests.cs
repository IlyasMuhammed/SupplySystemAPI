using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using SMS.Modules.Demand.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-28 — the packing list and gate pass PDFs.
public class DeliveryDocumentTests
{
    private const int User   = 42;
    private const int Picker = 99;

    private static readonly Guid CentralUuid = Guid.NewGuid();

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
        PickListRepository PickLists,
        PackageRepository Packages,
        GoodsIssueRepository GoodsIssue,
        DeliveryDocumentService Documents,
        FakeStockReservationService Reservations);

    private static Harness NewHarness(PoDocumentTemplateModel? template = null)
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var reservations = new FakeStockReservationService();
        var numbers      = new DocumentNumberGenerator(db, tenant);

        var deliveries = new DeliveryRepository(
            db, numbers, new AddressNormalizer(new FakeCityLookup()));
        var packages   = new PackageRepository(db, numbers);
        var release    = new DeliveryReleaseRepository(db, reservations, numbers);
        var status     = new DeliveryStatusRepository(db, reservations);
        var pickLists  = new PickListRepository(db, reservations, numbers);
        var goodsIssue = new GoodsIssueRepository(
            db, LogisticsTestDb.Demand(dbName, tenant), reservations, new FakeGoodsIssuePoster(reservations),
            new Mock<ISaleOrderFulfillmentService>().Object, NullLogger<GoodsIssueRepository>.Instance);

        var deliveryService = new DeliveryService(deliveries, null!, status, release, goodsIssue);

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(template);

        var documents = new DeliveryDocumentService(
            deliveryService, packages, templates.Object, new StubEnvironment());

        return new Harness(db, deliveries, release, pickLists, packages, goodsIssue,
                           documents, reservations);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>A delivery picked and packed into one carton, ready for paper.</summary>
    private static async Task<Guid> Packed(Harness h, decimal qty = 100m, bool withAddress = true)
    {
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 1000m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType   = "SRO",
            SourceUuid   = Guid.NewGuid(),
            SourceNumber = "SRO-2026-00042",
            ShipFromWarehouseUuid = CentralUuid,
            ShipFromAddress = withAddress ? new AddressRequest
            {
                ContactName = "Central Warehouse", Line1 = "12 Dock Road",
                CityName = "Karachi", CountryName = "Pakistan"
            } : null,
            ShipToAddress = withAddress ? new AddressRequest
            {
                ContactName = "Acme Supplies Ltd", Line1 = "4 Industrial Estate",
                CityName = "Lahore", CountryName = "Pakistan"
            } : null,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm armoured cable", UnitOfMeasure = "M",
                QtyOrdered = qty, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = qty }]
        }, Picker);

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();

        await h.Packages.PackAsync(uuid, new PackRequest
        {
            PackageType   = "CRATE",
            LengthCm      = 120m, WidthCm = 80m, HeightCm = 60m,
            GrossWeightKg = 44.5m, SealNumber = "SEAL-771",
            Contents = [new PackContentRequest
            {
                DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = qty, BatchNumber = "B-2026-07"
            }]
        }, User);

        h.Db.ChangeTracker.Clear();
        return uuid;
    }

    private static async Task<Guid> Staged(Harness h, decimal qty = 100m)
    {
        var uuid = await Packed(h, qty);
        await h.GoodsIssue.StageAsync(uuid, User);
        h.Db.ChangeTracker.Clear();
        return uuid;
    }

    /// <summary>A PDF starts with %PDF- and ends with %%EOF. Anything else is not a document.</summary>
    private static void ShouldBeAPdf(byte[] content)
    {
        content.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(content, 0, 5).Should().Be("%PDF-");
        System.Text.Encoding.ASCII.GetString(content[^6..]).Should().Contain("%%EOF");
        content.Length.Should().BeGreaterThan(1000, "a rendered page is never a few bytes");
    }

    // ── Packing list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_packed_delivery_produces_a_packing_list()
    {
        var h = NewHarness();
        var uuid = await Packed(h);

        var (content, fileName) = await h.Documents.GeneratePackingListAsync(uuid);

        ShouldBeAPdf(content);

        var number = (await h.Deliveries.GetByUuidAsync(uuid))!.DeliveryNumber;
        fileName.Should().Be($"PackingList-{number}.pdf");
    }

    [Fact]
    public async Task A_packing_list_renders_without_a_letterhead_configured()
    {
        // A fresh deployment has no template row and no logo file. The document still has to come
        // out — falling over here would mean nothing can be printed until someone visits settings.
        var h = NewHarness(template: null);
        var uuid = await Packed(h);

        var (content, _) = await h.Documents.GeneratePackingListAsync(uuid);

        ShouldBeAPdf(content);
    }

    [Fact]
    public async Task A_packing_list_renders_with_a_full_letterhead()
    {
        var h = NewHarness(new PoDocumentTemplateModel
        {
            CompanyName    = "Supply System (Pvt) Ltd",
            CompanyAddress = "Plot 7, Korangi Industrial Area, Karachi",
            CompanyPhone   = "+92 21 111 222 333",
            CompanyEmail   = "dispatch@example.com",
            CompanyTaxId   = "1234567-8",
            FooterText     = "Goods remain the property of the seller until paid for in full.",
            PreparedByLabel = "Packed By"
        });

        var uuid = await Packed(h);

        var (content, _) = await h.Documents.GeneratePackingListAsync(uuid);

        ShouldBeAPdf(content);
    }

    [Fact]
    public async Task A_packing_list_renders_when_no_addresses_were_recorded()
    {
        // Addresses are optional on a delivery, and a blank space on printed paper reads as a bug.
        var h = NewHarness();
        var uuid = await Packed(h, withAddress: false);

        var (content, _) = await h.Documents.GeneratePackingListAsync(uuid);

        ShouldBeAPdf(content);
    }

    [Fact]
    public async Task A_delivery_with_no_cartons_has_no_packing_list()
    {
        // Paper that implies the goods are ready while they are still on the floor.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 500m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 10m, VariantUuid = variantUuid
            }]
        }, User);

        var act = async () => await h.Documents.GeneratePackingListAsync(uuid);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*no packages*")
            .WithMessage("*Pack it first*");
    }

    [Fact]
    public async Task A_delivery_whose_only_carton_was_voided_has_no_packing_list()
    {
        var h = NewHarness();
        var uuid = await Packed(h);

        var packageUuid = (await h.Packages.GetForDeliveryAsync(uuid))!.Packages.Single().UUID;
        await h.Packages.VoidAsync(packageUuid, new DeliveryReasonRequest { Reason = "Crushed" }, User);

        var act = async () => await h.Documents.GeneratePackingListAsync(uuid);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_multi_carton_delivery_produces_a_longer_list()
    {
        // Weak on its own, but it proves every carton is composed rather than only the first.
        var h = NewHarness();

        var one = await Packed(h, qty: 100m);
        var (small, _) = await h.Documents.GeneratePackingListAsync(one);

        var many = await Packed(h, qty: 100m);
        var line = (await h.Packages.GetForDeliveryAsync(many))!.Lines.Single();

        // Void the single carton and repack the same units across three.
        var original = (await h.Packages.GetForDeliveryAsync(many))!.Packages.Single().UUID;
        await h.Packages.VoidAsync(original, new DeliveryReasonRequest { Reason = "Repacking" }, User);

        foreach (var qty in new[] { 40m, 30m, 30m })
            await h.Packages.PackAsync(many, new PackRequest
            {
                GrossWeightKg = 15m,
                Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = qty }]
            }, User);

        var (large, _) = await h.Documents.GeneratePackingListAsync(many);

        ShouldBeAPdf(large);
        large.Length.Should().BeGreaterThan(small.Length, "three cartons take more paper than one");
    }

    // ── Gate pass ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_staged_delivery_produces_a_gate_pass()
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        var (content, fileName) = await h.Documents.GenerateGatePassAsync(uuid);

        ShouldBeAPdf(content);

        var number = (await h.Deliveries.GetByUuidAsync(uuid))!.DeliveryNumber;
        fileName.Should().Be($"GatePass-{number}.pdf");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    public async Task A_gate_pass_cannot_be_printed_before_the_goods_reach_the_dock(string status)
    {
        // A pass printed early is a pass someone can walk out with while the goods are still
        // being packed.
        var h = NewHarness();
        var uuid = await Staged(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Documents.GenerateGatePassAsync(uuid);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage($"*{status}*")
            .WithMessage("*staged at the dock*");
    }

    [Theory]
    [InlineData("STAGED")]
    [InlineData("PENDING_APPROVAL")]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERED")]
    public async Task A_gate_pass_stays_available_once_the_goods_are_at_the_dock_or_past_it(string status)
    {
        // It is also the proof of what left, so it has to remain printable after dispatch.
        var h = NewHarness();
        var uuid = await Staged(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var (content, _) = await h.Documents.GenerateGatePassAsync(uuid);

        ShouldBeAPdf(content);
    }

    [Fact]
    public async Task A_gate_pass_survives_a_carton_with_no_weight_or_seal()
    {
        // Weight and seal are optional on a package, and the gate pass prints a dash rather than
        // failing — a document that refuses to render because a field is blank is worse than one
        // that says the field is blank.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 500m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 10m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);
        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();
        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = 10m }]
        }, Picker);

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = 10m }]
        }, User);

        await h.GoodsIssue.StageAsync(uuid, User);
        h.Db.ChangeTracker.Clear();

        var (content, _) = await h.Documents.GenerateGatePassAsync(uuid);

        ShouldBeAPdf(content);
    }

    [Fact]
    public async Task A_gate_pass_is_not_a_packing_list()
    {
        // They are different documents for different readers: one itemises contents, the other
        // counts pieces. If they rendered identically, one of them would be wrong.
        var h = NewHarness();
        var uuid = await Staged(h);

        var (packingList, packingName) = await h.Documents.GeneratePackingListAsync(uuid);
        var (gatePass,    gateName)    = await h.Documents.GenerateGatePassAsync(uuid);

        packingName.Should().StartWith("PackingList-");
        gateName.Should().StartWith("GatePass-");
        gatePass.Should().NotEqual(packingList);
    }

    // ── Collection gate pass (A29-P6-04 §8.3) ─────────────────────────────────

    /// <summary>A self-pickup sale-order delivery, staged at the counter.</summary>
    private static async Task<Guid> SelfPickupStaged(Harness h)
    {
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 1000m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "SALE_ORDER", SourceUuid = Guid.NewGuid(), SourceNumber = "SO-2026-00042",
            ShipFromWarehouseUuid = CentralUuid,
            ShipFromAddress = new AddressRequest
            {
                ContactName = "Central Warehouse", Line1 = "12 Dock Road", CityName = "Karachi", CountryName = "Pakistan"
            },
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Dell Latitude 5450 (DELL-5450-I7)", UnitOfMeasure = "Piece",
                QtyOrdered = 3m, VariantUuid = variantUuid
            }]
        }, User);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.DeliveryMode = "SELF_PICKUP";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Release.ReleaseAsync(uuid, null, User);
        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();
        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = 3m }]
        }, Picker);
        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = 3m }]
        }, User);
        await h.GoodsIssue.StageAsync(uuid, User);
        h.Db.ChangeTracker.Clear();

        return uuid;
    }

    [Fact]
    public async Task A_self_pickup_delivery_gets_a_collection_pass_before_and_after_the_customer_collects()
    {
        // Printed before collection it carries ruled blanks for the gate; printed after, it names
        // the collector. Both are valid documents, and both must render.
        var h = NewHarness();
        var uuid = await SelfPickupStaged(h);

        var (before, beforeName) = await h.Documents.GenerateGatePassAsync(uuid);
        ShouldBeAPdf(before);
        beforeName.Should().StartWith("GatePass-");

        await h.GoodsIssue.RecordPickupAsync(uuid, new RecordPickupRequest
        {
            PickupPersonName = "Ahmed Raza", PickupPersonIdType = "CNIC",
            PickupPersonIdNumber = "35202-1234567-1", PickupAuthorization = "Letter AL-2026-114"
        }, User);

        var (after, _) = await h.Documents.GenerateGatePassAsync(uuid);
        ShouldBeAPdf(after);
        after.Should().NotEqual(before, "the collector's details are now on it");
    }

    [Fact]
    public async Task A_collection_pass_itemises_where_a_shipping_pass_does_not()
    {
        // Security checks what the customer carries out against the pass, so this is the one gate
        // pass that lists items — the packing list's body under the gate pass heading.
        var h = NewHarness();
        var collection = await SelfPickupStaged(h);
        var shipping   = await Staged(h);

        var (collectionPass, _) = await h.Documents.GenerateGatePassAsync(collection);
        var (shippingPass, _)   = await h.Documents.GenerateGatePassAsync(shipping);
        var (packingList, _)    = await h.Documents.GeneratePackingListAsync(collection);

        ShouldBeAPdf(collectionPass);
        collectionPass.Should().NotEqual(shippingPass);
        collectionPass.Should().NotEqual(packingList, "it is a gate pass, not the packing list itself");
    }

    // ── Not found ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_delivery_has_no_documents()
    {
        var h = NewHarness();

        var packingList = async () => await h.Documents.GeneratePackingListAsync(Guid.NewGuid());
        var gatePass    = async () => await h.Documents.GenerateGatePassAsync(Guid.NewGuid());

        await packingList.Should().ThrowAsync<NotFoundException>();
        await gatePass.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organizations_delivery_has_no_documents()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var reservations = new FakeStockReservationService();
        var numbers      = new DocumentNumberGenerator(db, tenant);

        var deliveries = new DeliveryRepository(db, numbers, new AddressNormalizer(new FakeCityLookup()));
        var packages   = new PackageRepository(db, numbers);
        var release    = new DeliveryReleaseRepository(db, reservations, numbers);
        var status     = new DeliveryStatusRepository(db, reservations);
        var pickLists  = new PickListRepository(db, reservations, numbers);
        var goodsIssue = new GoodsIssueRepository(
            db, LogisticsTestDb.Demand(dbName, tenant), reservations, new FakeGoodsIssuePoster(reservations),
            new Mock<ISaleOrderFulfillmentService>().Object, NullLogger<GoodsIssueRepository>.Instance);

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        var h = new Harness(db, deliveries, release, pickLists, packages, goodsIssue,
            new DeliveryDocumentService(
                new DeliveryService(deliveries, null!, status, release, goodsIssue),
                packages, templates.Object, new StubEnvironment()),
            reservations);

        var uuid = await Packed(h);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var otherNumbers = new DocumentNumberGenerator(otherDb, tenant);
        var otherDeliveries = new DeliveryRepository(
            otherDb, otherNumbers, new AddressNormalizer(new FakeCityLookup()));
        var otherPackages = new PackageRepository(otherDb, otherNumbers);

        var other = new DeliveryDocumentService(
            new DeliveryService(
                otherDeliveries, null!,
                new DeliveryStatusRepository(otherDb, reservations),
                new DeliveryReleaseRepository(otherDb, reservations, otherNumbers),
                new GoodsIssueRepository(
                    otherDb, LogisticsTestDb.Demand(dbName, tenant), reservations, new FakeGoodsIssuePoster(reservations),
                    new Mock<ISaleOrderFulfillmentService>().Object, NullLogger<GoodsIssueRepository>.Instance)),
            otherPackages, templates.Object, new StubEnvironment());

        var act = async () => await other.GeneratePackingListAsync(uuid);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
