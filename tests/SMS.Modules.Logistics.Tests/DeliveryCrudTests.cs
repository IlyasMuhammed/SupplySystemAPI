using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-10 — delivery CRUD.
public class DeliveryCrudTests
{
    private const int User = 42;

    private static DeliveryRepository Repo(LogisticsDbContext db, Guid organizationId)
    {
        var tenant = new StaticTenantContext { OrganizationId = organizationId };
        return new DeliveryRepository(
            db,
            new DocumentNumberGenerator(db, tenant),
            new AddressNormalizer(new FakeCityLookup()));
    }

    private static CreateDeliveryRequest NewRequest(int lines = 1) => new()
    {
        SourceType = "MANUAL",
        Direction  = "OUTBOUND",
        ShipToAddress = new AddressRequest
        {
            Line1          = "Plot 12, Korangi Industrial Area",
            CityName       = "Karachi",
            CountryName    = "Pakistan",
            CountryIsoCode = "PK",
            ContactPhone   = "0300-1234567"
        },
        Lines = [.. Enumerable.Range(1, lines).Select(i => new CreateDeliveryLineRequest
        {
            ItemDescription = $"Item {i}",
            UnitOfMeasure   = "PC",
            QtyOrdered      = 10m * i,
            VariantUuid     = Guid.NewGuid()
        })]
    };

    // ── TC-10.1 — create ─────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_delivery_numbers_it_and_orders_its_lines()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        var uuid = await repo.CreateAsync(NewRequest(lines: 3), User);

        var detail = await repo.GetByUuidAsync(uuid);

        detail.Should().NotBeNull();
        detail!.DeliveryNumber.Should().MatchRegex(@"^DLV-\d{4}-\d{5}$");
        detail.Status.Should().Be("DRAFT");
        detail.Direction.Should().Be("OUTBOUND");
        detail.Priority.Should().Be("NORMAL", "an unspecified priority is normal, not null");
        detail.Lines.Should().HaveCount(3);
        detail.Lines.Select(l => l.LineNo).Should().Equal(1, 2, 3);
        detail.TraceId.Should().NotBeEmpty("a manual delivery starts its own lineage");
    }

    [Fact]
    public async Task Creating_a_delivery_records_who_made_it_and_when()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var uuid = await Repo(db, tenant.OrganizationId).CreateAsync(NewRequest(), User);

        var stored = await db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);

        stored.CreatedBy.Should().Be(User);
        stored.CreatedDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        stored.OrganizationId.Should().Be(tenant.OrganizationId);
        stored.Lines.Should().OnlyContain(l => l.CreatedBy == User);
    }

    [Fact]
    public async Task The_ship_to_address_is_normalized_on_the_way_in()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        var uuid   = await repo.CreateAsync(NewRequest(), User);
        var detail = await repo.GetByUuidAsync(uuid);

        detail!.ShipToAddress.Should().NotBeNull();
        detail.ShipToAddress!.ContactPhoneE164.Should().Be("+923001234567");
        detail.ShipToAddress.ValidationStatus.Should().Be("VALID");
    }

    [Fact]
    public async Task Two_deliveries_get_consecutive_numbers()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        var first  = await repo.GetByUuidAsync(await repo.CreateAsync(NewRequest(), User));
        var second = await repo.GetByUuidAsync(await repo.CreateAsync(NewRequest(), User));

        first!.DeliveryNumber.Should().EndWith("00001");
        second!.DeliveryNumber.Should().EndWith("00002");
    }

    // ── TC-10.2 / TC-10.3 — line validation ──────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.001)]
    public async Task A_line_for_no_quantity_is_rejected(decimal qty)
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.Lines[0].QtyOrdered = qty;

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*Line 1*");
    }

    [Fact]
    public async Task A_line_with_no_description_is_rejected()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest(lines: 2);
        req.Lines[1].ItemDescription = "   ";

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*Line 2*", "the message must say which line is wrong");
    }

    [Fact]
    public async Task A_delivery_with_no_lines_is_rejected()
    {
        // The exact defect the rebuild exists to fix: the legacy Shipment had a weight and a PO
        // number and could never say what was in the box.
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.Lines.Clear();

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*at least one line*");
    }

    [Fact]
    public async Task Nothing_is_persisted_when_a_line_is_invalid()
    {
        // Validation happens before SaveChanges, so a rejected request must leave no delivery —
        // and must not burn a document number that then shows up as a gap nobody can explain.
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest(lines: 2);
        req.Lines[1].QtyOrdered = 0;

        try { await Repo(db, tenant.OrganizationId).CreateAsync(req, User); } catch (BadRequestException) { }

        (await db.DeliveryOrders.ToListAsync()).Should().BeEmpty();
    }

    // ── Source type and direction ────────────────────────────────────────────

    [Fact]
    public async Task A_manual_delivery_must_state_its_direction()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.Direction = null;

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*does not imply a direction*");
    }

    [Fact]
    public async Task A_po_delivery_takes_its_direction_from_the_source()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.SourceType = "PO";
        req.Direction  = null;

        var detail = await Repo(db, tenant.OrganizationId)
            .GetByUuidAsync(await Repo(db, tenant.OrganizationId).CreateAsync(req, User));

        detail!.Direction.Should().Be("INBOUND");
        detail.PostsGoodsIssue.Should().BeFalse("the GRN posts stock for a PO, not the delivery");
    }

    [Fact]
    public async Task Contradicting_the_source_document_is_an_error_not_an_override()
    {
        // A PO delivery claiming to be outbound would point the stock movement the wrong way.
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.SourceType = "PO";
        req.Direction  = "OUTBOUND";

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*always INBOUND*");
    }

    [Theory]
    [InlineData("NOT_A_SOURCE", "source type")]
    [InlineData("SIDEWAYS", "direction")]
    public async Task Unknown_codes_are_rejected_with_the_valid_values(string bad, string field)
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();

        if (field == "source type") req.SourceType = bad; else req.Direction = bad;

        var act = async () => await Repo(db, tenant.OrganizationId).CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{bad}*");
    }

    [Fact]
    public async Task A_transfer_delivery_posts_its_own_stock_movement()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var req = NewRequest();
        req.SourceType            = "TRANSFER";
        req.Direction             = null;
        // Both ends are required for a transfer — see DeliveryFromSroMivTransferTests.
        req.ShipFromWarehouseUuid = Guid.NewGuid();
        req.ShipToWarehouseUuid   = Guid.NewGuid();

        var repo   = Repo(db, tenant.OrganizationId);
        var detail = await repo.GetByUuidAsync(await repo.CreateAsync(req, User));

        detail!.Direction.Should().Be("TRANSFER");
        detail.PostsGoodsIssue.Should().BeTrue("nothing else posts a warehouse transfer");
    }

    // ── TC-10.4 — editing window ─────────────────────────────────────────────

    [Fact]
    public async Task A_draft_delivery_can_be_edited()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        var updated = await repo.PatchAsync(uuid, new PatchDeliveryRequest
        {
            Priority = "URGENT",
            Notes    = "Site needs it before Friday",
            Incoterm = "ddp"
        }, User);

        updated.Should().BeTrue();

        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Priority.Should().Be("URGENT");
        detail.Notes.Should().Be("Site needs it before Friday");
        detail.Incoterm.Should().Be("DDP", "incoterms are upper-cased on the way in");
        detail.ModifiedDate.Should().NotBeNull();
    }

    [Theory]
    [InlineData("PICKING")]
    [InlineData("PACKED")]
    [InlineData("GOODS_ISSUED")]
    [InlineData("DELIVERED")]
    public async Task A_delivery_past_planning_can_no_longer_be_edited(string status)
    {
        // Warehouse staff are working to a printed pick list; changing the document underneath
        // them is how the paper and the system stop agreeing.
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        await SetStatus(db, uuid, status);

        var act = async () => await repo.PatchAsync(uuid, new PatchDeliveryRequest { Notes = "late change" }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage($"*{status}*")
            .WithMessage("*DRAFT, RELEASED*");
    }

    [Fact]
    public async Task Patching_the_address_creates_a_new_snapshot_rather_than_editing_the_old_one()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        var original = (await repo.GetByUuidAsync(uuid))!.ShipToAddress!.UUID;

        await repo.PatchAsync(uuid, new PatchDeliveryRequest
        {
            ShipToAddress = new AddressRequest
            {
                Line1 = "Warehouse 7, Port Qasim", CityName = "Karachi",
                CountryName = "Pakistan", CountryIsoCode = "PK"
            }
        }, User);

        var updated = (await repo.GetByUuidAsync(uuid))!.ShipToAddress!;

        updated.UUID.Should().NotBe(original, "the old address may already be printed somewhere");
        updated.Line1.Should().Be("Warehouse 7, Port Qasim");
        (await db.Addresses.CountAsync()).Should().Be(2, "both snapshots are kept");
    }

    [Fact]
    public async Task Patching_an_unknown_delivery_reports_not_found_rather_than_throwing()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var updated = await Repo(db, tenant.OrganizationId)
            .PatchAsync(Guid.NewGuid(), new PatchDeliveryRequest { Notes = "x" }, User);

        updated.Should().BeFalse();
    }

    // ── TC-10.5 — delete ─────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_draft_is_soft_and_hides_it_everywhere()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        (await repo.DeleteAsync(uuid)).Should().BeTrue();

        (await repo.GetByUuidAsync(uuid)).Should().BeNull();
        (await repo.GetListAsync(new DeliveryFilter())).TotalRecords.Should().Be(0);

        var row = await db.DeliveryOrders.IgnoreQueryFilters().SingleAsync(d => d.UUID == uuid);
        row.IsDelete.Should().BeTrue("the row is kept — only hidden");
        row.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("GOODS_ISSUED")]
    public async Task A_delivery_that_has_left_draft_cannot_be_deleted(string status)
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        await SetStatus(db, uuid, status);

        var act = async () => await repo.DeleteAsync(uuid);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*Cancel it instead*");
    }

    [Fact]
    public async Task Deleting_an_unknown_delivery_reports_not_found()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        (await Repo(db, tenant.OrganizationId).DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    // ── TC-10.6 — listing ────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_status_direction_and_source_type()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        await repo.CreateAsync(NewRequest(), User);                                  // MANUAL / OUTBOUND
        var po = NewRequest(); po.SourceType = "PO"; po.Direction = null;
        var poUuid = await repo.CreateAsync(po, User);                               // PO / INBOUND
        await SetStatus(db, poUuid, "RELEASED");

        (await repo.GetListAsync(new DeliveryFilter { SourceType = "PO" })).TotalRecords.Should().Be(1);
        (await repo.GetListAsync(new DeliveryFilter { Direction = "INBOUND" })).TotalRecords.Should().Be(1);
        (await repo.GetListAsync(new DeliveryFilter { Status = "DRAFT" })).TotalRecords.Should().Be(1);
        (await repo.GetListAsync(new DeliveryFilter { Status = "RELEASED" })).TotalRecords.Should().Be(1);
        (await repo.GetListAsync(new DeliveryFilter())).TotalRecords.Should().Be(2);
    }

    [Fact]
    public async Task The_list_searches_on_delivery_and_source_number()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        var req = NewRequest();
        req.SourceNumber = "PO-2026-00042";
        await repo.CreateAsync(req, User);
        await repo.CreateAsync(NewRequest(), User);

        (await repo.GetListAsync(new DeliveryFilter { Search = "00042" })).TotalRecords.Should().Be(1);
        (await repo.GetListAsync(new DeliveryFilter { Search = "DLV-" })).TotalRecords.Should().Be(2);
        (await repo.GetListAsync(new DeliveryFilter { Search = "nothing" })).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task The_list_pages_correctly()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);

        for (var i = 0; i < 7; i++) await repo.CreateAsync(NewRequest(), User);

        var page1 = await repo.GetListAsync(new DeliveryFilter { Page = 1, PageSize = 3 });
        var page3 = await repo.GetListAsync(new DeliveryFilter { Page = 3, PageSize = 3 });

        page1.TotalRecords.Should().Be(7);
        page1.TotalPages.Should().Be(3);
        page1.Data.Should().HaveCount(3);
        page3.Data.Should().HaveCount(1, "the last page holds the remainder");

        page1.Data.Select(d => d.UUID).Should().NotIntersectWith(page3.Data.Select(d => d.UUID));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_nonsensical_page_number_falls_back_to_the_first_page(int page)
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        await repo.CreateAsync(NewRequest(), User);

        var result = await repo.GetListAsync(new DeliveryFilter { Page = page });

        result.Page.Should().Be(1);
        result.Data.Should().HaveCount(1, "a bad page must not silently return nothing");
    }

    [Fact]
    public async Task The_list_reports_line_counts()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        await repo.CreateAsync(NewRequest(lines: 4), User);

        var row = (await repo.GetListAsync(new DeliveryFilter())).Data.Single();

        row.LineCount.Should().Be(4);
        row.LinesUnknown.Should().BeFalse();
        row.ShipToCity.Should().Be("Karachi");
    }

    // ── TC-10.7 — tenant isolation ───────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_delivery_simply_does_not_exist()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (dbA, _, dbName) = LogisticsTestDb.New(orgA);
        var uuid = await Repo(dbA, orgA).CreateAsync(NewRequest(), User);

        await using var dbB = LogisticsTestDb.OpenAs(dbName, orgB);
        var repoB = Repo(dbB, orgB);

        // Null, not an exception — the controller turns this into 404 rather than 403, which
        // would confirm the id is real.
        (await repoB.GetByUuidAsync(uuid)).Should().BeNull();
        (await repoB.GetListAsync(new DeliveryFilter())).TotalRecords.Should().Be(0);
        (await repoB.PatchAsync(uuid, new PatchDeliveryRequest { Notes = "x" }, User)).Should().BeFalse();
        (await repoB.DeleteAsync(uuid)).Should().BeFalse();
    }

    // ── The detail tells the UI what it may do next ──────────────────────────

    [Fact]
    public async Task The_detail_lists_the_statuses_the_delivery_may_move_to()
    {
        // Driven by the state machine, so the UI cannot enable a button the server will refuse.
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = Repo(db, tenant.OrganizationId);
        var uuid = await repo.CreateAsync(NewRequest(), User);

        var detail = await repo.GetByUuidAsync(uuid);

        detail!.AllowedNextStatuses.Should().BeEquivalentTo(["RELEASED", "CANCELLED"]);
    }

    private static async Task SetStatus(LogisticsDbContext db, Guid uuid, string status)
    {
        var delivery = await db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
