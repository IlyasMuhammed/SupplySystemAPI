using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
/// A32 C1 — Sale Inquiry (PB-03 numbering, PB-04 state machine, PB-05 line evaluation, PB-06 CRUD, PB-12).
/// Covers every T-C1-* scenario of spec §12 at the service level, plus own-organization isolation (another
/// organization's inquiry is "not found", super admin included). T-C1-11's endpoint wiring is in
/// <see cref="SaleInquiriesControllerTests"/>; its inquiry side (REVIEW_COMPLETE → QUOTED, committed by the
/// caller's save) is <see cref="MarkQuoted_moves_a_reviewed_inquiry_to_QUOTED_but_leaves_the_save_to_the_caller"/>.
/// </summary>
public class SaleInquiryServiceTests
{
    private const int User = 7;
    private const int OtherUser = 9;

    private static readonly Guid Customer         = Guid.NewGuid();
    private static readonly Guid VendorOnly       = Guid.NewGuid();
    private static readonly Guid InactiveCustomer = Guid.NewGuid();

    private static readonly Guid Product     = Guid.NewGuid();
    private static readonly Guid Variant     = Guid.NewGuid();
    private static readonly Guid AltProduct  = Guid.NewGuid();
    private static readonly Guid AltVariant  = Guid.NewGuid();

    private static readonly DateTime Received = new(2026, 3, 10);
    private static readonly DateTime Delivery = new(2026, 4, 15);

    private sealed record Harness(
        DemandDbContext Db, SaleInquiryService Service, StaticTenantContext Tenant, string DbName,
        Mock<IDocumentNumberGenerator> Numbers, Mock<IPartnerRoleLookup> Partners,
        Mock<IProductVariantResolver> Variants, Mock<IUserQueryService> Users,
        int ActiveReasonId, Guid ActiveReason, Guid InactiveReason, Guid OtherOrgReason);

    private static Harness NewHarness()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = NewDb(dbName, tenant);

        var numbers = new Mock<IDocumentNumberGenerator>();
        var counter = 0;
        numbers.Setup(n => n.NextAsync("INQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DateTime? at, Guid? _, CancellationToken _) =>
                $"INQ-{(at ?? DateTime.UtcNow).Year}-{++counter:D5}");

        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", IsCustomer: true, IsVendor: false, IsActive: true));
        partners.Setup(p => p.GetAsync(VendorOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartnerRoleInfo(VendorOnly, "Steel Mill", IsCustomer: false, IsVendor: true, IsActive: true));
        partners.Setup(p => p.GetAsync(InactiveCustomer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartnerRoleInfo(InactiveCustomer, "Gone Ltd", IsCustomer: true, IsVendor: false, IsActive: false));

        var known = new Dictionary<Guid, VariantDescription>
        {
            [Variant]    = new(Variant, Product, "SKU-1", "Red", "Widget", false, "PCS"),
            [AltVariant] = new(AltVariant, AltProduct, "SKU-2", "Blue", "Gadget", false, "PCS")
        };
        var variants = new Mock<IProductVariantResolver>();
        variants.Setup(v => v.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(known.ContainsKey).Distinct().ToDictionary(i => i, i => known[i]));

        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) =>
                (IReadOnlyList<UserIdentity>)ids.Where(i => i is User or OtherUser).Distinct()
                    .Select(i => new UserIdentity(i, i == User ? "Sana Rep" : "Omar Lead")).ToList());

        var active = new RejectionReason { OrganizationId = tenant.OrganizationId, Code = "OOS", Description = "Out of stock", IsActive = true, IsSystem = true, DisplayOrder = 20 };
        var inactive = new RejectionReason { OrganizationId = tenant.OrganizationId, Code = "DIS", Description = "Discontinued", IsActive = false, IsSystem = true, DisplayOrder = 30 };
        var foreign = new RejectionReason { OrganizationId = Guid.NewGuid(), Code = "OTH", Description = "Other", IsActive = true, IsSystem = true, DisplayOrder = 100 };
        db.RejectionReasons.AddRange(active, inactive, foreign);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                (IReadOnlyDictionary<Guid, string>)ids.Where(i => i == Customer).Distinct().ToDictionary(i => i, _ => "Acme Retail"));

        var service = new SaleInquiryService(db, tenant, numbers.Object, partners.Object, variants.Object, users.Object, names.Object);
        return new Harness(db, service, tenant, dbName, numbers, partners, variants, users,
            active.Id, active.UUID, inactive.UUID, foreign.UUID);
    }

    private static DemandDbContext NewDb(string dbName, ITenantContext tenant) =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

    /// <summary>A service for another organization over the same store — a super admin, who bypasses the EF filter.</summary>
    private static (SaleInquiryService Service, DemandDbContext Db) OtherOrgSuperAdmin(Harness h)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = true };
        var db = NewDb(h.DbName, tenant);
        return (new SaleInquiryService(db, tenant, h.Numbers.Object, h.Partners.Object, h.Variants.Object, h.Users.Object), db);
    }

    private static CreateSaleInquiryRequest NewInquiry(params SaleInquiryLineRequest[] lines) => new()
    {
        PartnerId = Customer, CustomerReference = "RFQ-77", ReceivedDate = Received,
        ResponseDeadline = Received.AddDays(7), Lines = [.. lines]
    };

    private static SaleInquiryLineRequest FreeText(decimal qty = 1000m, string description = "Blue steel pipes, 2 inch") =>
        new() { ProductDescription = description, RequestedQuantity = qty, RequestedUomCode = "PCS" };

    private static SaleInquiryLineRequest Catalog(decimal qty = 1000m) =>
        new() { VariantUuid = Variant, ProductDescription = "Red widget as per drawing", RequestedQuantity = qty };

    private static UpdateSaleInquiryLineRequest Evaluate(SaleInquiryLineRequest line, string status,
        decimal? canSupply = null, DateTime? delivery = null, Guid? reason = null, Guid? altVariant = null,
        Guid? altProduct = null, string? altNotes = null, string? rejectionNotes = null) => new()
    {
        ProductUuid = line.ProductUuid, VariantUuid = line.VariantUuid, ProductDescription = line.ProductDescription,
        RequestedQuantity = line.RequestedQuantity, RequestedUomCode = line.RequestedUomCode,
        RequestedDeliveryDate = line.RequestedDeliveryDate, Notes = line.Notes,
        LineStatus = status, CanSupplyQuantity = canSupply, EstimatedDeliveryDate = delivery,
        RejectionReasonUuid = reason, RejectionNotes = rejectionNotes, AlternativeVariantUuid = altVariant,
        AlternativeProductUuid = altProduct, AlternativeNotes = altNotes
    };

    private static async Task<SaleInquiry> Reload(Harness h, Guid uuid)
    {
        await using var db = NewDb(h.DbName, h.Tenant);
        return await db.SaleInquiries.Include(x => x.Lines).SingleAsync(x => x.UUID == uuid);
    }

    private static async Task<(Guid Inquiry, Guid Line)> InquiryWithOneLine(Harness h, SaleInquiryLineRequest? line = null)
    {
        var uuid = await h.Service.CreateAsync(NewInquiry(line ?? FreeText()), User);
        var model = await h.Service.GetByIdAsync(uuid);
        return (uuid, model!.Lines.Single().Uuid);
    }

    private static async Task<Guid> ReviewedInquiry(Harness h)
    {
        var (uuid, line) = await InquiryWithOneLine(h, Catalog());
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(Catalog(), "CAN_SUPPLY", delivery: Delivery), User);
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "REVIEW_COMPLETE" }, User);
        return uuid;
    }

    private static async Task ForceStatus(Harness h, Guid uuid, string status)
    {
        await using var db = NewDb(h.DbName, h.Tenant);
        var inquiry = await db.SaleInquiries.SingleAsync(x => x.UUID == uuid);
        inquiry.Status = status;
        await db.SaveChangesAsync();
        // The harness context still tracks the old copy; a request scope would start clean.
        h.Db.ChangeTracker.Clear();
    }

    // ── PB-03 numbering / T-C1-01 / BR-C1-01 ─────────────────────────────────────

    [Fact]
    public async Task T_C1_01_Create_for_a_customer_is_RECEIVED_and_numbered_INQ_per_org_from_the_received_date()
    {
        var h = NewHarness();

        var uuid = await h.Service.CreateAsync(NewInquiry(), User);

        var model = await h.Service.GetByIdAsync(uuid);
        model!.InquiryNumber.Should().Be("INQ-2026-00001");
        model.Status.Should().Be("RECEIVED");
        model.PartnerName.Should().Be("Acme Retail");
        model.CustomerReference.Should().Be("RFQ-77");
        model.ReceivedDate.Should().Be(Received);
        model.CreatedBy.Should().Be(User);
        model.IsEditable.Should().BeTrue();
        // The year of the number is the received date's year, drawn from this organization's counter.
        h.Numbers.Verify(n => n.NextAsync("INQ", Received, h.Tenant.OrganizationId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_numbers_each_inquiry_in_sequence_and_defaults_the_received_date_to_today()
    {
        var h = NewHarness();

        var first  = await h.Service.CreateAsync(NewInquiry(), User);
        var second = await h.Service.CreateAsync(new CreateSaleInquiryRequest { PartnerId = Customer }, User);

        (await h.Service.GetByIdAsync(first))!.InquiryNumber.Should().Be("INQ-2026-00001");
        var model = await h.Service.GetByIdAsync(second);
        model!.InquiryNumber.Should().Be($"INQ-{DateTime.UtcNow.Year}-00002");
        model.ReceivedDate.Should().Be(DateTime.UtcNow.Date);
    }

    [Fact]
    public void The_inquiry_number_is_unique_per_organization_in_the_model()
    {
        // PB-03 race safety: IDocumentNumberGenerator serializes the counter (row version + retry) and this
        // unique index is the backstop — a duplicate can never be committed.
        var h = NewHarness();
        var entity = h.Db.Model.FindEntityType(typeof(SaleInquiry))!;

        entity.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "OrganizationId", "InquiryNumber" }));
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("inactive")]
    [InlineData("unknown")]
    public async Task T_C1_02_Create_for_a_partner_that_is_not_an_active_customer_is_refused_before_a_number_is_drawn(string kind)
    {
        var h = NewHarness();
        var partner = kind switch { "vendor" => VendorOnly, "inactive" => InactiveCustomer, _ => Guid.NewGuid() };

        var act = () => h.Service.CreateAsync(new CreateSaleInquiryRequest { PartnerId = partner }, User);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*customer*");
        h.Numbers.Verify(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.Db.SaleInquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_refuses_an_assigned_user_that_does_not_exist()
    {
        var h = NewHarness();
        var req = NewInquiry();
        req.AssignedToUserId = 4242;

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*assigned*");
    }

    // ── T-C1-03 / T-C1-04 lines ─────────────────────────────────────────────────

    [Fact]
    public async Task T_C1_03_A_catalog_line_is_PENDING_with_its_product_taken_from_the_variant()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(), User);

        var lineUuid = await h.Service.AddLineAsync(uuid, Catalog(), User);

        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        line.Uuid.Should().Be(lineUuid!.Value);
        line.LineNumber.Should().Be(1);
        line.VariantUuid.Should().Be(Variant);
        line.ProductUuid.Should().Be(Product);
        line.VariantSku.Should().Be("SKU-1");
        line.LineStatus.Should().Be("PENDING");
        line.ReviewedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task T_C1_04_A_free_text_line_has_no_product_and_keeps_the_description()
    {
        var h = NewHarness();

        var uuid = await h.Service.CreateAsync(NewInquiry(FreeText(description: "Something like the old model")), User);

        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        line.ProductUuid.Should().BeNull();
        line.VariantUuid.Should().BeNull();
        line.ProductDescription.Should().Be("Something like the old model");
        line.LineStatus.Should().Be("PENDING");
    }

    [Fact]
    public async Task A_line_needs_a_description_a_positive_quantity_and_a_known_variant()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(), User);

        await FluentActions.Awaiting(() => h.Service.AddLineAsync(uuid, FreeText(description: "  "), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*description*");
        await FluentActions.Awaiting(() => h.Service.AddLineAsync(uuid, FreeText(qty: 0), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*quantity*");
        await FluentActions.Awaiting(() => h.Service.AddLineAsync(uuid,
                new SaleInquiryLineRequest { VariantUuid = Guid.NewGuid(), ProductDescription = "x", RequestedQuantity = 1 }, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*variant*");
        await FluentActions.Awaiting(() => h.Service.AddLineAsync(uuid,
                new SaleInquiryLineRequest { VariantUuid = Variant, ProductUuid = AltProduct, ProductDescription = "x", RequestedQuantity = 1 }, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*product*");
    }

    [Fact]
    public async Task New_lines_take_the_next_number_after_the_highest_even_after_a_delete()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(FreeText(), FreeText(), FreeText()), User);
        var second = (await h.Service.GetByIdAsync(uuid))!.Lines.Single(l => l.LineNumber == 2).Uuid;

        (await h.Service.DeleteLineAsync(uuid, second, User)).Should().BeTrue();
        await h.Service.AddLineAsync(uuid, FreeText(), User);

        (await h.Service.GetByIdAsync(uuid))!.Lines.Select(l => l.LineNumber).Should().Equal(1, 3, 4);
    }

    // ── PB-05 line evaluation / T-C1-05..08 ─────────────────────────────────────

    [Fact]
    public async Task T_C1_05_CAN_SUPPLY_needs_an_estimated_delivery_date_and_stamps_the_reviewer()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);

        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY"), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*delivery date*");

        (await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), OtherUser))
            .Should().BeTrue();

        var model = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        model.LineStatus.Should().Be("CAN_SUPPLY");
        model.EstimatedDeliveryDate.Should().Be(Delivery);
        model.ReviewedByUserId.Should().Be(OtherUser);
        model.ReviewedByUserName.Should().Be("Omar Lead");
        model.ReviewedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task T_C1_06_PARTIAL_600_of_1000_is_saved_with_its_date()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h, FreeText(qty: 1000));

        (await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(qty: 1000), "PARTIAL", canSupply: 600, delivery: Delivery), User))
            .Should().BeTrue();

        var model = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        model.LineStatus.Should().Be("PARTIAL");
        model.CanSupplyQuantity.Should().Be(600);
        model.EstimatedDeliveryDate.Should().Be(Delivery);
    }

    [Theory]
    [InlineData(null, true)]   // no quantity
    [InlineData(0, true)]      // not > 0
    [InlineData(1000, true)]   // not < requested
    [InlineData(1200, true)]
    [InlineData(600, false)]   // no date
    public async Task PARTIAL_needs_a_quantity_strictly_between_zero_and_the_request_and_a_date(int? canSupply, bool withDate)
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h, FreeText(qty: 1000));

        var act = () => h.Service.UpdateLineAsync(uuid, line,
            Evaluate(FreeText(qty: 1000), "PARTIAL", canSupply: canSupply, delivery: withDate ? Delivery : null), User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await Reload(h, uuid)).Lines.Single().LineStatus.Should().Be("PENDING");
    }

    [Fact]
    public async Task T_C1_07_CANNOT_SUPPLY_without_a_rejection_reason_is_refused()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);

        var act = () => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY"), User);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*rejection reason*");
    }

    [Fact]
    public async Task T_C1_08_CANNOT_SUPPLY_with_a_reason_and_an_alternative_is_saved()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h, Catalog());

        (await h.Service.UpdateLineAsync(uuid, line, Evaluate(Catalog(), "CANNOT_SUPPLY", reason: h.ActiveReason,
                rejectionNotes: "Mill shut", altVariant: AltVariant, altNotes: "Same spec, other brand"), User))
            .Should().BeTrue();

        var stored = (await Reload(h, uuid)).Lines.Single();
        stored.RejectionReasonId.Should().Be(h.ActiveReasonId);
        stored.AlternativeVariantUuid.Should().Be(AltVariant);
        stored.AlternativeProductUuid.Should().Be(AltProduct);

        var model = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        model.LineStatus.Should().Be("CANNOT_SUPPLY");
        model.RejectionReasonUuid.Should().Be(h.ActiveReason);
        model.RejectionReasonCode.Should().Be("OOS");
        model.RejectionReasonDescription.Should().Be("Out of stock");
        model.RejectionNotes.Should().Be("Mill shut");
        model.AlternativeVariantSku.Should().Be("SKU-2");
        model.AlternativeNotes.Should().Be("Same spec, other brand");
    }

    [Fact]
    public async Task CANNOT_SUPPLY_refuses_an_inactive_reason_and_another_organizations_reason()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);

        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.InactiveReason), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*rejection reason*");
        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.OtherOrgReason), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*rejection reason*");
        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.ActiveReason, altVariant: Guid.NewGuid()), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*variant*");
    }

    [Fact]
    public async Task A_line_already_on_a_reason_that_was_deactivated_keeps_it_while_other_fields_change()
    {
        // BR-C5-03 — deactivated reasons stay on historical lines; only *setting* one needs it active.
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.ActiveReason), User);
        var reason = await h.Db.RejectionReasons.SingleAsync(r => r.UUID == h.ActiveReason);
        reason.IsActive = false;
        await h.Db.SaveChangesAsync();

        (await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.ActiveReason, rejectionNotes: "more"), User))
            .Should().BeTrue();
    }

    [Fact]
    public async Task UNDER_REVIEW_is_a_valid_line_evaluation_and_an_unknown_status_is_refused()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);

        (await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "UNDER_REVIEW"), User)).Should().BeTrue();
        (await h.Service.GetByIdAsync(uuid))!.Lines.Single().LineStatus.Should().Be("UNDER_REVIEW");

        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "MAYBE"), User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Fields_that_do_not_apply_to_the_new_status_are_cleared()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h, FreeText(qty: 1000));

        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(qty: 1000), "CANNOT_SUPPLY", reason: h.ActiveReason,
            rejectionNotes: "no", altVariant: AltVariant, altNotes: "alt"), User);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(qty: 1000), "CAN_SUPPLY", canSupply: 500, delivery: Delivery,
            reason: h.ActiveReason, rejectionNotes: "no", altVariant: AltVariant, altNotes: "alt"), User);

        var stored = (await Reload(h, uuid)).Lines.Single();
        stored.LineStatus.Should().Be("CAN_SUPPLY");
        stored.CanSupplyQuantity.Should().BeNull();
        stored.RejectionReasonId.Should().BeNull();
        stored.RejectionNotes.Should().BeNull();
        stored.AlternativeVariantUuid.Should().BeNull();
        stored.AlternativeProductUuid.Should().BeNull();
        stored.AlternativeNotes.Should().BeNull();
        stored.EstimatedDeliveryDate.Should().Be(Delivery);
    }

    [Fact]
    public async Task Going_back_to_PENDING_clears_the_evaluation_and_the_reviewer()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);

        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "PENDING", delivery: Delivery), User);

        var stored = (await Reload(h, uuid)).Lines.Single();
        stored.LineStatus.Should().Be("PENDING");
        stored.EstimatedDeliveryDate.Should().BeNull();
        stored.ReviewedByUserId.Should().BeNull();
        stored.ReviewedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_line_of_another_inquiry_is_not_found()
    {
        var h = NewHarness();
        var (first, _) = await InquiryWithOneLine(h);
        var (_, otherLine) = await InquiryWithOneLine(h);

        (await h.Service.UpdateLineAsync(first, otherLine, Evaluate(FreeText(), "UNDER_REVIEW"), User)).Should().BeFalse();
        (await h.Service.DeleteLineAsync(first, otherLine, User)).Should().BeFalse();
    }

    // ── PB-04 state machine / T-C1-09 / T-C1-10 ─────────────────────────────────

    [Fact]
    public async Task Review_cannot_begin_without_a_line()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(), User);

        (await h.Service.GetByIdAsync(uuid))!.AllowedNextStatuses.Should().BeEmpty();
        await FluentActions.Awaiting(() => h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*line*");
    }

    [Fact]
    public async Task RECEIVED_moves_to_UNDER_REVIEW_once_it_has_a_line()
    {
        var h = NewHarness();
        var (uuid, _) = await InquiryWithOneLine(h);
        (await h.Service.GetByIdAsync(uuid))!.AllowedNextStatuses.Should().Equal("UNDER_REVIEW");

        var model = await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, OtherUser);

        model!.Status.Should().Be("UNDER_REVIEW");
        model.AllowedNextStatuses.Should().Equal("DECLINED");
        (await Reload(h, uuid)).ModifiedBy.Should().Be(OtherUser);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("UNDER_REVIEW")]
    public async Task T_C1_09_REVIEW_COMPLETE_is_refused_while_a_line_is_undecided(string undecided)
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(FreeText(), FreeText()), User);
        var lines = (await h.Service.GetByIdAsync(uuid))!.Lines;
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User);
        await h.Service.UpdateLineAsync(uuid, lines[0].Uuid, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);
        await h.Service.UpdateLineAsync(uuid, lines[1].Uuid, Evaluate(FreeText(), undecided), User);

        var act = () => h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "REVIEW_COMPLETE" }, User);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*evaluated*");
        (await Reload(h, uuid)).Status.Should().Be("UNDER_REVIEW");
        (await h.Service.GetByIdAsync(uuid))!.AllowedNextStatuses.Should().Equal("DECLINED");
    }

    [Fact]
    public async Task T_C1_10_REVIEW_COMPLETE_once_every_line_is_evaluated()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(FreeText(qty: 1000), FreeText(), FreeText()), User);
        var lines = (await h.Service.GetByIdAsync(uuid))!.Lines;
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User);
        await h.Service.UpdateLineAsync(uuid, lines[0].Uuid, Evaluate(FreeText(qty: 1000), "PARTIAL", canSupply: 600, delivery: Delivery), User);
        await h.Service.UpdateLineAsync(uuid, lines[1].Uuid, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);
        await h.Service.UpdateLineAsync(uuid, lines[2].Uuid, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.ActiveReason), User);
        (await h.Service.GetByIdAsync(uuid))!.AllowedNextStatuses.Should().BeEquivalentTo("REVIEW_COMPLETE", "DECLINED");

        var model = await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "REVIEW_COMPLETE" }, User);

        model!.Status.Should().Be("REVIEW_COMPLETE");
        model.IsEditable.Should().BeTrue();
        model.AllowedNextStatuses.Should().Equal("DECLINED");
    }

    [Theory]
    [InlineData("RECEIVED", "REVIEW_COMPLETE")]
    [InlineData("RECEIVED", "DECLINED")]
    [InlineData("UNDER_REVIEW", "RECEIVED")]
    [InlineData("REVIEW_COMPLETE", "UNDER_REVIEW")]
    [InlineData("UNDER_REVIEW", "QUOTED")]
    [InlineData("REVIEW_COMPLETE", "QUOTED")]
    [InlineData("UNDER_REVIEW", "NONSENSE")]
    public async Task Transitions_outside_the_state_machine_are_refused(string from, string to)
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);
        await ForceStatus(h, uuid, from);

        var act = () => h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = to, Reason = "r" }, User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await Reload(h, uuid)).Status.Should().Be(from);
    }

    [Theory]
    [InlineData("UNDER_REVIEW")]
    [InlineData("REVIEW_COMPLETE")]
    public async Task DECLINED_needs_a_reason_and_keeps_it(string from)
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        await h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);
        await ForceStatus(h, uuid, from);

        await FluentActions.Awaiting(() => h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "DECLINED", Reason = "  " }, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*reason*");

        var model = await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "DECLINED", Reason = " Not our market " }, User);

        model!.Status.Should().Be("DECLINED");
        model.DeclineReason.Should().Be("Not our market");
        model.IsEditable.Should().BeFalse();
        model.AllowedNextStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_line_to_a_REVIEW_COMPLETE_inquiry_reopens_the_review()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);

        await h.Service.AddLineAsync(uuid, FreeText(), User);

        (await Reload(h, uuid)).Status.Should().Be("UNDER_REVIEW");
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("UNDER_REVIEW")]
    public async Task Setting_a_line_of_a_REVIEW_COMPLETE_inquiry_back_to_undecided_reopens_the_review(string undecided)
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);
        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single().Uuid;

        await h.Service.UpdateLineAsync(uuid, line, Evaluate(Catalog(), undecided), User);

        (await Reload(h, uuid)).Status.Should().Be("UNDER_REVIEW");
    }

    [Fact]
    public async Task Changing_a_decided_line_of_a_REVIEW_COMPLETE_inquiry_keeps_it_complete()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);
        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single().Uuid;

        await h.Service.UpdateLineAsync(uuid, line, Evaluate(Catalog(), "CANNOT_SUPPLY", reason: h.ActiveReason), User);

        (await Reload(h, uuid)).Status.Should().Be("REVIEW_COMPLETE");
    }

    [Fact]
    public async Task Deleting_the_last_line_of_a_REVIEW_COMPLETE_inquiry_reopens_the_review()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);
        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single().Uuid;

        (await h.Service.DeleteLineAsync(uuid, line, User)).Should().BeTrue();

        var stored = await Reload(h, uuid);
        stored.Status.Should().Be("UNDER_REVIEW");
        stored.Lines.Should().BeEmpty();
    }

    // ── T-C1-11 (inquiry side) — MarkQuotedAsync ────────────────────────────────

    [Fact]
    public async Task MarkQuoted_moves_a_reviewed_inquiry_to_QUOTED_but_leaves_the_save_to_the_caller()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);

        await h.Service.MarkQuotedAsync(uuid, OtherUser);

        // Not saved: the quotation service commits the quotation and this transition together.
        (await Reload(h, uuid)).Status.Should().Be("REVIEW_COMPLETE");

        await h.Db.SaveChangesAsync();
        var stored = await Reload(h, uuid);
        stored.Status.Should().Be("QUOTED");
        stored.ModifiedBy.Should().Be(OtherUser);
        var model = await h.Service.GetByIdAsync(uuid);
        model!.IsEditable.Should().BeFalse();
        model.AllowedNextStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task T_C1_11_Creating_a_quotation_from_a_reviewed_inquiry_commits_the_quotation_and_QUOTED_together()
    {
        // The real chain: this service + the quotation service (QUO) on one scoped context, as DI wires them.
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(Catalog(qty: 1000), FreeText()), User);
        var lines = (await h.Service.GetByIdAsync(uuid))!.Lines;
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User);
        await h.Service.UpdateLineAsync(uuid, lines[0].Uuid, Evaluate(Catalog(qty: 1000), "PARTIAL", canSupply: 600, delivery: Delivery), User);
        await h.Service.UpdateLineAsync(uuid, lines[1].Uuid, Evaluate(FreeText(), "CANNOT_SUPPLY", reason: h.ActiveReason, altVariant: AltVariant), User);
        await h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "REVIEW_COMPLETE" }, User);

        var currency = Guid.NewGuid();
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(currency);
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 50m, currency, PriceResolutionTier.DefaultSelling, null));
        h.Numbers.Setup(n => n.NextAsync("SQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SQ-2026-00001");
        var quotations = new SaleQuotationService(h.Db, h.Tenant, h.Numbers.Object, h.Partners.Object, orgCurrency.Object,
            pricing.Object, h.Service, new Mock<ISaleOrderService>().Object, variants: h.Variants.Object);
        var request = new CreateSaleQuotationFromInquiryRequest { ValidFrom = Received, ValidTo = Received.AddDays(30) };

        var quotationUuid = await quotations.CreateFromInquiryAsync(uuid, request, OtherUser);

        var stored = await Reload(h, uuid);
        stored.Status.Should().Be("QUOTED");
        stored.ModifiedBy.Should().Be(OtherUser);
        var model = await h.Service.GetByIdAsync(uuid);
        model!.IsEditable.Should().BeFalse();
        model.Quotations.Should().ContainSingle().Which.Uuid.Should().Be(quotationUuid);

        await using var check = NewDb(h.DbName, h.Tenant);
        var quotation = await check.SaleQuotations.Include(q => q.Lines).SingleAsync(q => q.UUID == quotationUuid);
        quotation.SourceInquiryId.Should().Be(stored.Id);
        quotation.Lines.Select(l => (l.LineType, l.Quantity)).Should().BeEquivalentTo(new[]
        {
            ("NORMAL", 600m), ("REJECTED", 1000m), ("ALTERNATIVE", 1000m)
        });

        // A second create-quotation finds the inquiry QUOTED.
        await FluentActions.Awaiting(() => quotations.CreateFromInquiryAsync(uuid, request, OtherUser))
            .Should().ThrowAsync<ConflictException>();
    }

    [Theory]
    [InlineData("RECEIVED")]
    [InlineData("UNDER_REVIEW")]
    [InlineData("QUOTED")]
    [InlineData("DECLINED")]
    public async Task MarkQuoted_refuses_an_inquiry_that_is_not_REVIEW_COMPLETE(string status)
    {
        var h = NewHarness();
        var (uuid, _) = await InquiryWithOneLine(h);
        await ForceStatus(h, uuid, status);

        await FluentActions.Awaiting(() => h.Service.MarkQuotedAsync(uuid, User)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task MarkQuoted_does_not_find_another_organizations_inquiry_even_for_a_super_admin()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);
        var (other, otherDb) = OtherOrgSuperAdmin(h);
        await using var _ = otherDb;

        await FluentActions.Awaiting(() => other.MarkQuotedAsync(uuid, User)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => h.Service.MarkQuotedAsync(Guid.NewGuid(), User)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetById_lists_the_quotations_made_from_the_inquiry()
    {
        var h = NewHarness();
        var uuid = await ReviewedInquiry(h);
        var inquiry = await h.Db.SaleInquiries.SingleAsync(x => x.UUID == uuid);
        var quotation = new SaleQuotation
        {
            OrganizationId = h.Tenant.OrganizationId, QuotationNumber = "SQ-2026-00001", PartnerId = Customer,
            SourceInquiryId = inquiry.Id, CurrencyId = Guid.NewGuid(), ValidFrom = Received, ValidTo = Received.AddDays(30),
            CreatedBy = User
        };
        h.Db.SaleQuotations.Add(quotation);
        await h.Db.SaveChangesAsync();

        var model = await h.Service.GetByIdAsync(uuid);

        model!.Quotations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new SalesDocumentLinkModel { Uuid = quotation.UUID, Number = "SQ-2026-00001", Status = "DRAFT" });
    }

    // ── T-C1-12 / BR-C1-06 — QUOTED and DECLINED are read-only ──────────────────

    [Theory]
    [InlineData("QUOTED")]
    [InlineData("DECLINED")]
    public async Task T_C1_12_A_QUOTED_or_DECLINED_inquiry_is_read_only(string status)
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        await ForceStatus(h, uuid, status);

        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*read-only*");
        await FluentActions.Awaiting(() => h.Service.AddLineAsync(uuid, FreeText(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*read-only*");
        await FluentActions.Awaiting(() => h.Service.DeleteLineAsync(uuid, line, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*read-only*");
        await FluentActions.Awaiting(() => h.Service.UpdateAsync(uuid, new UpdateSaleInquiryRequest { ReceivedDate = Received }, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*read-only*");
        await FluentActions.Awaiting(() => h.Service.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "DECLINED", Reason = "x" }, User))
            .Should().ThrowAsync<BadRequestException>();

        var stored = await Reload(h, uuid);
        stored.Status.Should().Be(status);
        stored.Lines.Should().ContainSingle().Which.LineStatus.Should().Be("PENDING");
    }

    // ── PB-06 header update ─────────────────────────────────────────────────────

    [Fact]
    public async Task Update_replaces_the_header_but_never_the_customer()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(NewInquiry(), User);

        (await h.Service.UpdateAsync(uuid, new UpdateSaleInquiryRequest
        {
            CustomerReference = " RFQ-78 ", CustomerReferenceDate = Received.AddDays(-1), ReceivedDate = Received.AddDays(1),
            ResponseDeadline = Received.AddDays(10), AssignedToUserId = OtherUser, Notes = "call back"
        }, OtherUser)).Should().BeTrue();

        var model = await h.Service.GetByIdAsync(uuid);
        model!.CustomerReference.Should().Be("RFQ-78");
        model.CustomerReferenceDate.Should().Be(Received.AddDays(-1));
        model.ReceivedDate.Should().Be(Received.AddDays(1));
        model.ResponseDeadline.Should().Be(Received.AddDays(10));
        model.AssignedToUserId.Should().Be(OtherUser);
        model.AssignedToUserName.Should().Be("Omar Lead");
        model.Notes.Should().Be("call back");
        model.PartnerId.Should().Be(Customer);
        model.InquiryNumber.Should().Be("INQ-2026-00001");
        (await Reload(h, uuid)).ModifiedBy.Should().Be(OtherUser);
    }

    // ── Own organization (super admin included) ─────────────────────────────────

    [Fact]
    public async Task Another_organizations_inquiry_is_not_found_for_any_operation_even_for_a_super_admin()
    {
        var h = NewHarness();
        var (uuid, line) = await InquiryWithOneLine(h);
        var (other, otherDb) = OtherOrgSuperAdmin(h);
        await using var _ = otherDb;

        (await other.GetByIdAsync(uuid)).Should().BeNull();
        (await other.UpdateAsync(uuid, new UpdateSaleInquiryRequest { ReceivedDate = Received }, User)).Should().BeFalse();
        (await other.AddLineAsync(uuid, FreeText(), User)).Should().BeNull();
        (await other.UpdateLineAsync(uuid, line, Evaluate(FreeText(), "UNDER_REVIEW"), User)).Should().BeFalse();
        (await other.DeleteLineAsync(uuid, line, User)).Should().BeFalse();
        (await other.ChangeStatusAsync(uuid, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User)).Should().BeNull();
        (await other.GetListAsync(new SaleInquiryListFilter())).TotalRecords.Should().Be(0);

        var stored = await Reload(h, uuid);
        stored.Status.Should().Be("RECEIVED");
        stored.Lines.Should().ContainSingle();
    }

    // ── PB-06 list ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_filters_counts_and_pages_newest_received_first()
    {
        var h = NewHarness();
        var older = await h.Service.CreateAsync(new CreateSaleInquiryRequest
        {
            PartnerId = Customer, ReceivedDate = Received.AddDays(-5), CustomerReference = "OLD-1",
            Lines = [FreeText(), FreeText()]
        }, User);
        var newer = await h.Service.CreateAsync(new CreateSaleInquiryRequest
        {
            PartnerId = Customer, ReceivedDate = Received, AssignedToUserId = OtherUser, Lines = [FreeText()]
        }, User);
        var line = (await h.Service.GetByIdAsync(older))!.Lines[0].Uuid;
        await h.Service.UpdateLineAsync(older, line, Evaluate(FreeText(), "CAN_SUPPLY", delivery: Delivery), User);
        await h.Service.ChangeStatusAsync(newer, new ChangeSaleInquiryStatusRequest { Status = "UNDER_REVIEW" }, User);

        var all = await h.Service.GetListAsync(new SaleInquiryListFilter());
        all.TotalRecords.Should().Be(2);
        all.Data.Select(x => x.Uuid).Should().Equal(newer, older);
        var olderRow = all.Data[1];
        olderRow.LineCount.Should().Be(2);
        olderRow.PendingLineCount.Should().Be(1);
        olderRow.PartnerName.Should().Be("Acme Retail");
        all.Data[0].AssignedToUserName.Should().Be("Omar Lead");

        (await h.Service.GetListAsync(new SaleInquiryListFilter { Status = "UNDER_REVIEW" })).Data.Select(x => x.Uuid).Should().Equal(newer);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { PartnerId = Guid.NewGuid() })).TotalRecords.Should().Be(0);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { PartnerId = Customer })).TotalRecords.Should().Be(2);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { ReceivedFrom = Received.AddDays(-1) })).Data.Select(x => x.Uuid).Should().Equal(newer);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { ReceivedTo = Received.AddDays(-5) })).Data.Select(x => x.Uuid).Should().Equal(older);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { AssignedToUserId = OtherUser })).Data.Select(x => x.Uuid).Should().Equal(newer);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { Search = "OLD" })).Data.Select(x => x.Uuid).Should().Equal(older);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { Search = "00002" })).Data.Select(x => x.Uuid).Should().Equal(newer);

        var paged = await h.Service.GetListAsync(new SaleInquiryListFilter { Page = 2, PageSize = 1 });
        paged.Data.Select(x => x.Uuid).Should().Equal(older);
        paged.TotalPages.Should().Be(2);
        (await h.Service.GetListAsync(new SaleInquiryListFilter { PageSize = 1000 })).PageSize.Should().Be(100);
    }
}
