using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PD-03/PD-04/PD-05/PD-08 (T-C3-01..05, T-C2-12) — a sale order's source (MANUAL / FROM_QUOTATION), the
/// customer's PO reference, date and document, the duplicate-PO warning, and the quotation → order conversion path.
/// </summary>
public class SaleOrderSourceAndCustomerPoTests
{
    private const int User = 5;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(
        DemandDbContext Db, SaleOrderService Service, StaticTenantContext Tenant, Mock<IPricingService> Pricing,
        Mock<IAttachmentService> Attachments, string DbName);

    private static Harness NewHarness(string? dbName = null, Guid? orgId = null, bool superAdmin = false, int[]? numbers = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = orgId ?? Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var counter = 0;
        var numberGen = new Mock<IDocumentNumberGenerator>();
        numberGen.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"SO-2026-{++counter:00000}");
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 100m, Currency, PriceResolutionTier.DefaultSelling, null));
        var attachments = new Mock<IAttachmentService>();

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numberGen.Object, pricing.Object, Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(),
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(), attachments: attachments.Object);
        return new Harness(db, service, tenant, pricing, attachments, dbName);
    }

    private static CreateSaleOrderRequest Direct(string? poRef = null, DateTime? poDate = null, string? sourceType = null) => new()
    {
        PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
        CustomerPoReference = poRef, CustomerPoDate = poDate, SourceType = sourceType,
        Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 2 }]
    };

    // ── PD-03 direct orders ───────────────────────────────────────────────────

    [Fact]
    public async Task A_direct_order_is_manual_with_no_source_links()
    {
        var h = NewHarness();

        var uuid = await h.Service.CreateAsync(Direct(), User);
        var model = (await h.Service.GetByIdAsync(uuid))!;

        model.SourceType.Should().Be("MANUAL");
        model.SourceQuotation.Should().BeNull();
        model.SourceInquiry.Should().BeNull();
    }

    [Fact]
    public async Task The_customers_po_reference_and_date_are_kept_and_shown()
    {
        var h = NewHarness();

        var uuid = await h.Service.CreateAsync(Direct("  GT-PO-2026-4521 ", new DateTime(2026, 10, 5)), User);
        var model = (await h.Service.GetByIdAsync(uuid))!;

        model.CustomerPoReference.Should().Be("GT-PO-2026-4521");
        model.CustomerPoDate.Should().Be(new DateTime(2026, 10, 5));
    }

    [Theory]
    [InlineData("FROM_QUOTATION")]
    [InlineData("PORTAL")]
    [InlineData("INTER_TENANT")]
    [InlineData("SOMETHING")]
    public async Task Only_a_manual_order_can_be_created_directly(string sourceType)
    {
        var h = NewHarness();

        var act = () => h.Service.CreateAsync(Direct(sourceType: sourceType), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Manual_may_be_named_explicitly()
    {
        var h = NewHarness();

        var uuid = await h.Service.CreateAsync(Direct(sourceType: "MANUAL"), User);

        (await h.Service.GetByIdAsync(uuid))!.SourceType.Should().Be("MANUAL");
    }

    [Fact]
    public async Task A_po_reference_over_50_characters_is_refused()
    {
        var h = NewHarness();

        var act = () => h.Service.CreateAsync(Direct(new string('P', 51)), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_duplicate_po_reference_is_allowed_and_reported_as_a_warning()
    {
        var h = NewHarness();
        var first = await h.Service.CreateAsync(Direct("PO-77"), User);

        var second = await h.Service.CreateAsync(Direct("po-77 "), User);
        var duplicates = await h.Service.FindCustomerPoDuplicatesAsync("PO-77", excludeUuid: second);

        second.Should().NotBe(first, "BR-C3-05 — a duplicate is a warning, never a refusal");
        duplicates.Should().ContainSingle().Which.Uuid.Should().Be(first);
        duplicates.Single().SoNumber.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Another_organizations_po_reference_is_no_duplicate()
    {
        var dbName = Guid.NewGuid().ToString();
        var other = NewHarness(dbName);
        await other.Service.CreateAsync(Direct("PO-SHARED"), User);
        var mine = NewHarness(dbName, superAdmin: true);

        (await mine.Service.FindCustomerPoDuplicatesAsync("PO-SHARED", null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_reference_has_no_duplicates()
    {
        var h = NewHarness();
        await h.Service.CreateAsync(Direct(), User);

        (await h.Service.FindCustomerPoDuplicatesAsync("  ", null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_edit_replaces_the_po_reference_and_date()
    {
        var h = NewHarness();
        var uuid = await h.Service.CreateAsync(Direct("OLD"), User);
        var current = (await h.Service.GetByIdAsync(uuid))!;
        h.Db.ChangeTracker.Clear();

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SELF_PICKUP", CustomerPoReference = "NEW", CustomerPoDate = new DateTime(2026, 10, 9),
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = current.Lines.Single().VariantUuid, Quantity = 2 }]
        }, User);

        var model = (await h.Service.GetByIdAsync(uuid))!;
        model.CustomerPoReference.Should().Be("NEW");
        model.CustomerPoDate.Should().Be(new DateTime(2026, 10, 9));
    }

    [Fact]
    public async Task Search_finds_an_order_by_its_customer_po_and_the_list_filters_by_source()
    {
        var h = NewHarness();
        var withPo = await h.Service.CreateAsync(Direct("GT-PO-4521"), User);
        await h.Service.CreateAsync(Direct("OTHER"), User);

        var found = await h.Service.GetListAsync(new SaleOrderListFilter { Search = "4521" });
        var manual = await h.Service.GetListAsync(new SaleOrderListFilter { SourceType = "MANUAL" });
        var quoted = await h.Service.GetListAsync(new SaleOrderListFilter { SourceType = "FROM_QUOTATION" });

        found.Data.Should().ContainSingle().Which.Uuid.Should().Be(withPo);
        found.Data.Single().CustomerPoReference.Should().Be("GT-PO-4521");
        manual.TotalRecords.Should().Be(2);
        quoted.TotalRecords.Should().Be(0);
    }

    // ── PD-05 customer PO after the fact ─────────────────────────────────────

    private async Task<Guid> ConfirmedOrderAsync(Harness h)
    {
        var uuid = await h.Service.CreateAsync(Direct(), User);
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == uuid);
        order.Status = "CONFIRMED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return uuid;
    }

    private static void FileExists(Harness h, Guid attachmentUuid, string interfaceCode, Guid documentId) =>
        h.Attachments.Setup(a => a.FindAsync(attachmentUuid))
            .ReturnsAsync(new AttachmentModel { UUID = attachmentUuid, InterfaceCode = interfaceCode, DocumentId = documentId, FileName = "po.pdf" });

    [Fact]
    public async Task The_customer_po_can_be_set_on_a_confirmed_order_with_its_document()
    {
        var h = NewHarness();
        var uuid = await ConfirmedOrderAsync(h);
        var file = Guid.NewGuid();
        FileExists(h, file, "CUSTOMER_PO", uuid);

        var updated = await h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest
        {
            CustomerPoReference = "GT-PO-1", CustomerPoDate = new DateTime(2026, 10, 5), CustomerPoAttachmentUuid = file
        }, User);

        updated.Should().BeTrue();
        var model = (await h.Service.GetByIdAsync(uuid))!;
        model.CustomerPoReference.Should().Be("GT-PO-1");
        model.CustomerPoAttachmentUuid.Should().Be(file);
    }

    [Fact]
    public async Task Clearing_the_document_link_keeps_the_reference()
    {
        var h = NewHarness();
        var uuid = await ConfirmedOrderAsync(h);
        var file = Guid.NewGuid();
        FileExists(h, file, "CUSTOMER_PO", uuid);
        await h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoReference = "R", CustomerPoAttachmentUuid = file }, User);
        h.Db.ChangeTracker.Clear();

        await h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoReference = "R" }, User);

        var model = (await h.Service.GetByIdAsync(uuid))!;
        model.CustomerPoAttachmentUuid.Should().BeNull();
        model.CustomerPoReference.Should().Be("R");
    }

    [Theory]
    [InlineData("SALE_ORDER", true)]   // a general file of this order, not its PO
    [InlineData("CUSTOMER_PO", false)] // the PO of another order
    public async Task Only_a_customer_po_file_of_this_order_can_be_linked(string interfaceCode, bool sameOrder)
    {
        var h = NewHarness();
        var uuid = await ConfirmedOrderAsync(h);
        var file = Guid.NewGuid();
        FileExists(h, file, interfaceCode, sameOrder ? uuid : Guid.NewGuid());

        var act = () => h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoAttachmentUuid = file }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task An_unknown_file_cannot_be_linked()
    {
        var h = NewHarness();
        var uuid = await ConfirmedOrderAsync(h);

        var act = () => h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoAttachmentUuid = Guid.NewGuid() }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("CLOSED")]
    public async Task A_cancelled_or_closed_orders_po_cannot_change(string status)
    {
        var h = NewHarness();
        var uuid = await ConfirmedOrderAsync(h);
        (await h.Db.SaleOrders.SingleAsync(o => o.UUID == uuid)).Status = status;
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoReference = "X" }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Another_organizations_order_is_not_found_for_a_po_update_even_for_a_super_admin()
    {
        var dbName = Guid.NewGuid().ToString();
        var theirs = NewHarness(dbName);
        var uuid = await ConfirmedOrderAsync(theirs);
        var admin = NewHarness(dbName, superAdmin: true);

        (await admin.Service.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoReference = "HIJACK" }, User))
            .Should().BeFalse();
    }

    // ── PD-04 conversion path ────────────────────────────────────────────────

    private static async Task<SaleQuotation> SeedAcceptedQuotationAsync(Harness h, bool fromInquiry = true)
    {
        SaleInquiry? inquiry = null;
        if (fromInquiry)
        {
            inquiry = new SaleInquiry
            {
                InquiryNumber = "INQ-2026-00042", PartnerId = Guid.NewGuid(), Status = "QUOTED",
                ReceivedDate = DateTime.UtcNow.Date, CreatedBy = User
            };
            h.Db.SaleInquiries.Add(inquiry);
            await h.Db.SaveChangesAsync();
        }

        var quotation = new SaleQuotation
        {
            QuotationNumber = "SQ-2026-00015", PartnerId = inquiry?.PartnerId ?? Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
            SourceInquiryId = inquiry?.Id, Status = "ACCEPTED", ValidFrom = DateTime.UtcNow.Date,
            ValidTo = DateTime.UtcNow.Date.AddDays(30), CreatedBy = User
        };
        h.Db.SaleQuotations.Add(quotation);
        await h.Db.SaveChangesAsync();
        return quotation;
    }

    private static CreateSaleOrderFromQuotationCommand Convert(SaleQuotation q, params QuotedSaleOrderLine[] lines) => new()
    {
        SourceQuotationUuid = q.UUID, DeliveryMode = "SELF_PICKUP", CustomerPoReference = "GT-PO-9", CustomerPoDate = new DateTime(2026, 10, 6),
        Lines = [.. lines]
    };

    [Fact]
    public async Task An_order_from_a_quotation_links_back_to_it_and_to_its_inquiry_at_the_quoted_prices()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h);

        var uuid = await h.Service.CreateFromQuotationAsync(Convert(q,
            new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 500m, UnitPrice = 2.50m },
            new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 10m, UnitPrice = 110m, DiscountPercent = 10m, TaxPercent = 17m }), User);

        var model = (await h.Service.GetByIdAsync(uuid))!;
        model.SourceType.Should().Be("FROM_QUOTATION");
        model.SourceQuotation.Should().BeEquivalentTo(new SalesDocumentLinkModel { Uuid = q.UUID, Number = "SQ-2026-00015", Status = "ACCEPTED" });
        model.SourceInquiry!.Number.Should().Be("INQ-2026-00042", "BR-C3-03 — chained from the quotation");
        model.PartnerId.Should().Be(q.PartnerId);
        model.CurrencyId.Should().Be(q.CurrencyId);
        model.Status.Should().Be("DRAFT");
        model.CustomerPoReference.Should().Be("GT-PO-9");
        model.Lines.Select(l => l.UnitPrice).Should().BeEquivalentTo([2.50m, 110m]);
        model.Lines.Single(l => l.UnitPrice == 110m).LineTotal.Should().Be(1158.30m); // 10 × 110 × 0.9 × 1.17
        model.GrandTotal.Should().Be(1250m + 1158.30m);
        h.Pricing.Verify(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never, "the agreed price is the quoted one");
    }

    [Fact]
    public async Task A_quotation_without_an_inquiry_leaves_the_inquiry_link_empty()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h, fromInquiry: false);

        var uuid = await h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        (await h.Service.GetByIdAsync(uuid))!.SourceInquiry.Should().BeNull();
    }

    [Fact]
    public async Task The_callers_quotation_status_change_commits_with_the_order()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h);
        q.Status = "CONVERTED"; // what QUO does on the tracked quotation before calling

        await h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        h.Db.ChangeTracker.Clear();
        (await h.Db.SaleQuotations.AsNoTracking().SingleAsync()).Status.Should().Be("CONVERTED");
    }

    [Fact]
    public async Task A_quotation_already_converted_to_an_order_cannot_be_converted_again()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h);
        await h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        var act = () => h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Another_organizations_quotation_is_not_found()
    {
        var dbName = Guid.NewGuid().ToString();
        var theirs = NewHarness(dbName);
        var q = await SeedAcceptedQuotationAsync(theirs);
        var admin = NewHarness(dbName, superAdmin: true);

        var act = () => admin.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_conversion_with_no_lines_or_a_negative_price_is_refused()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h);

        var none = () => h.Service.CreateFromQuotationAsync(Convert(q), User);
        var negative = () => h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = -1m }), User);

        await none.Should().ThrowAsync<BadRequestException>();
        await negative.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_converted_order_lists_under_from_quotation()
    {
        var h = NewHarness();
        var q = await SeedAcceptedQuotationAsync(h);
        await h.Service.CreateFromQuotationAsync(Convert(q, new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m }), User);

        (await h.Service.GetListAsync(new SaleOrderListFilter { SourceType = "FROM_QUOTATION" })).TotalRecords.Should().Be(1);
    }
}
