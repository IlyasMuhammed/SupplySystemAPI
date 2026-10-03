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
/// A32 C2 — Sale Quotation (PC-03..08): numbering, the state machine, line types and totals, customer responses,
/// conversion and create-from-inquiry. Spec §12 T-C2-01..13 (T-C2-11 is in <see cref="QuotationExpiryJobTests"/>),
/// BR-C2-01..11, and the own-organization rule (another org's quotation is "not found", super admin included).
/// </summary>
public class SaleQuotationServiceTests
{
    private const int User = 7;
    private static readonly Guid Customer         = Guid.NewGuid();
    private static readonly Guid Vendor           = Guid.NewGuid();
    private static readonly Guid InactiveCustomer = Guid.NewGuid();
    private static readonly Guid Pkr              = Guid.NewGuid();
    private static readonly DateTime From         = new(2026, 10, 1);
    private static readonly DateTime To           = new(2026, 10, 31);

    private sealed class H
    {
        public required DemandDbContext Db;
        public required StaticTenantContext Tenant;
        public required SaleQuotationService Svc;
        public required Mock<IPricingService> Pricing;
        public required FakeTaxCodes TaxCodes;
        public required Mock<ISaleInquiryService> Inquiries;
        public required Mock<ISaleOrderService> SaleOrders;
        public required Mock<IDocumentNumberGenerator> Numbers;
        public readonly List<CreateSaleOrderFromQuotationCommand> Commands = [];
        public string? StatusSeenByOrderService;
        public int? QuotationsSavedWhenMarkedQuoted;
        public Guid OrgId => Tenant.OrganizationId;
    }

    private static H NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"SQ-2026-{++seq:D5}");

        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", true, false, true));
        partners.Setup(p => p.GetAsync(Vendor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PartnerRoleInfo(Vendor, "Steel Supplier", false, true, true));
        partners.Setup(p => p.GetAsync(InactiveCustomer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PartnerRoleInfo(InactiveCustomer, "Dormant Co", true, false, false));

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Pkr);

        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new SalePriceResolution(true, 100m, null, PriceResolutionTier.VariantDefault, null));

        var taxCodes   = new FakeTaxCodes();
        var inquiries  = new Mock<ISaleInquiryService>();
        var saleOrders = new Mock<ISaleOrderService>();

        var svc = new SaleQuotationService(
            db, tenant, numbers.Object, partners.Object, orgCurrency.Object, pricing.Object,
            inquiries.Object, saleOrders.Object, taxCodes: taxCodes);

        var h = new H
        {
            Db = db, Tenant = tenant, Svc = svc, Pricing = pricing, TaxCodes = taxCodes,
            Inquiries = inquiries, SaleOrders = saleOrders, Numbers = numbers
        };

        // ISaleInquiryService.MarkQuotedAsync's contract: tracked load from the same context, set QUOTED, no save.
        inquiries.Setup(i => i.MarkQuotedAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                 .Returns(async (Guid uuid, int user) =>
                 {
                     h.QuotationsSavedWhenMarkedQuoted = await db.SaleQuotations.AsNoTracking().CountAsync();
                     var inquiry = await db.SaleInquiries.FirstAsync(x => x.UUID == uuid);
                     if (inquiry.Status != "REVIEW_COMPLETE") throw new ConflictException("not REVIEW_COMPLETE");
                     inquiry.Status = "QUOTED";
                     inquiry.ModifiedBy = user;
                 });

        // ISaleOrderService.CreateFromQuotationAsync's contract: its ONE SaveChangesAsync commits the order and the
        // quotation status the caller set on the same scoped context.
        saleOrders.Setup(s => s.CreateFromQuotationAsync(It.IsAny<CreateSaleOrderFromQuotationCommand>(), It.IsAny<int>()))
                  .Returns(async (CreateSaleOrderFromQuotationCommand cmd, int _) =>
                  {
                      h.Commands.Add(cmd);
                      h.StatusSeenByOrderService = db.SaleQuotations.Local.Single(q => q.UUID == cmd.SourceQuotationUuid).Status;
                      await db.SaveChangesAsync();
                      return Guid.NewGuid();
                  });

        return h;
    }

    // ── builders ─────────────────────────────────────────────────────────────

    private static CreateSaleQuotationRequest Header(Guid? partner = null, params SaleQuotationLineRequest[] lines) => new()
    {
        PartnerId = partner ?? Customer, CurrencyId = Pkr, ValidFrom = From, ValidTo = To,
        CustomerReference = "RFQ-77", PaymentTerms = "Net 30", Lines = [.. lines]
    };

    private static SaleQuotationLineRequest Normal(Guid? variant = null, decimal qty = 10m, decimal? price = null,
        decimal discount = 0m, decimal tax = 0m, Guid? taxCode = null) => new()
    {
        LineType = "NORMAL", VariantUuid = variant ?? Guid.NewGuid(), Quantity = qty, UnitPrice = price,
        DiscountPercent = discount, TaxPercent = tax, TaxCodeUuid = taxCode, ProductDescription = "Widget"
    };

    private static SaleQuotationLineRequest Rejected(Guid reason, Guid? variant = null, decimal qty = 200m) => new()
    {
        LineType = "REJECTED", VariantUuid = variant, Quantity = qty, RejectionReasonUuid = reason,
        RejectionNotes = "Out of stock", ProductDescription = "Product B"
    };

    private static SaleQuotationLineRequest Alternative(int? forNumber = null, Guid? forUuid = null, decimal qty = 200m, decimal? price = 48m) => new()
    {
        LineType = "ALTERNATIVE", VariantUuid = Guid.NewGuid(), Quantity = qty, UnitPrice = price,
        AlternativeForLineNumber = forNumber, AlternativeForLineUuid = forUuid, AlternativeNotes = "Same spec", ProductDescription = "Product B2"
    };

    private static async Task<RejectionReason> Reason(H h, string code = "OOS", bool active = true, Guid? org = null)
    {
        var r = new RejectionReason
        {
            OrganizationId = org ?? h.OrgId, Code = code, Description = code, IsActive = active, IsSystem = true, DisplayOrder = 10
        };
        h.Db.RejectionReasons.Add(r);
        await h.Db.SaveChangesAsync();
        return r;
    }

    private static Task<SaleQuotation> Stored(H h, Guid uuid) =>
        h.Db.SaleQuotations.AsNoTracking().Include(q => q.Lines).IgnoreQueryFilters().SingleAsync(q => q.UUID == uuid);

    private static async Task<(Guid Uuid, SaleQuotation Q)> SentQuotation(H h, params SaleQuotationLineRequest[] lines)
    {
        var uuid = await h.Svc.CreateAsync(Header(null, lines.Length == 0 ? [Normal(price: 50m)] : lines), User);
        (await h.Svc.SendAsync(uuid, User)).Should().BeTrue();
        return (uuid, await Stored(h, uuid));
    }

    private static Task Respond(H h, Guid uuid, Guid line, string response, decimal? counter = null, bool acceptCounter = false) =>
        h.Svc.RecordCustomerResponseAsync(uuid, line,
            new RecordCustomerResponseRequest { Response = response, CounterPrice = counter, AcceptCounterPrice = acceptCounter }, User);

    // ── PC-03 numbering / T-C2-01 create ─────────────────────────────────────

    [Fact]
    public async Task T_C2_01_an_independent_quotation_is_a_numbered_DRAFT_with_no_inquiry()
    {
        var h = NewHarness();

        var uuid = await h.Svc.CreateAsync(Header(null, Normal(price: 50m)), User);

        var q = await Stored(h, uuid);
        q.Status.Should().Be("DRAFT");
        q.QuotationNumber.Should().Be("SQ-2026-00001");
        q.SourceInquiryId.Should().BeNull();
        q.OrganizationId.Should().Be(h.OrgId);
        q.PartnerId.Should().Be(Customer);
        q.CreatedBy.Should().Be(User);
        q.TraceId.Should().NotBeEmpty();
        q.Lines.Should().ContainSingle().Which.LineNumber.Should().Be(1);
        h.Numbers.Verify(n => n.NextAsync("SQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Each_quotation_draws_the_next_SQ_number()
    {
        var h = NewHarness();
        var a = await h.Svc.CreateAsync(Header(), User);
        var b = await h.Svc.CreateAsync(Header(), User);
        (await Stored(h, a)).QuotationNumber.Should().Be("SQ-2026-00001");
        (await Stored(h, b)).QuotationNumber.Should().Be("SQ-2026-00002");
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("inactive")]
    [InlineData("unknown")]
    public async Task BR_C2_01_only_an_active_customer_of_this_organization_can_be_quoted(string who)
    {
        var h = NewHarness();
        var partner = who switch { "vendor" => Vendor, "inactive" => InactiveCustomer, _ => Guid.NewGuid() };

        var act = () => h.Svc.CreateAsync(Header(partner), User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await h.Db.SaleQuotations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BR_C2_03_valid_to_before_valid_from_is_refused()
    {
        var h = NewHarness();
        var req = Header();
        req.ValidTo = From.AddDays(-1);

        await FluentActions.Awaiting(() => h.Svc.CreateAsync(req, User)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("*valid*");
    }

    // ── PC-05 line types, alternatives, totals ───────────────────────────────

    [Fact]
    public async Task T_C2_03_a_REJECTED_line_and_its_ALTERNATIVE_are_saved_linked()
    {
        var h = NewHarness();
        var oos = await Reason(h);

        var uuid = await h.Svc.CreateAsync(Header(null, Normal(price: 50m), Rejected(oos.UUID), Alternative(forNumber: 2)), User);

        var lines = (await Stored(h, uuid)).Lines.OrderBy(l => l.LineNumber).ToList();
        lines.Select(l => l.LineType).Should().Equal("NORMAL", "REJECTED", "ALTERNATIVE");
        lines[1].RejectionReasonId.Should().Be(oos.Id);
        lines[1].UnitPrice.Should().Be(0m, "a REJECTED line carries no price");
        lines[1].LineTotal.Should().Be(0m);
        lines[2].AlternativeForLineId.Should().Be(lines[1].Id);

        // and through the API shape
        var model = (await h.Svc.GetByIdAsync(uuid))!;
        var alt = model.Lines.Single(l => l.LineType == "ALTERNATIVE");
        alt.AlternativeForLineUuid.Should().Be(lines[1].UUID);
        alt.AlternativeForLineNumber.Should().Be(2);
        model.Lines.Single(l => l.LineType == "REJECTED").RejectionReasonCode.Should().Be("OOS");
    }

    [Fact]
    public async Task An_ALTERNATIVE_can_be_added_later_for_an_existing_REJECTED_line_by_uuid()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var uuid = await h.Svc.CreateAsync(Header(null, Rejected(oos.UUID)), User);
        var rejected = (await Stored(h, uuid)).Lines.Single();

        var altUuid = await h.Svc.AddLineAsync(uuid, Alternative(forUuid: rejected.UUID), User);

        var alt = (await Stored(h, uuid)).Lines.Single(l => l.UUID == altUuid);
        alt.AlternativeForLineId.Should().Be(rejected.Id);
        alt.LineNumber.Should().Be(2);
    }

    [Fact]
    public async Task T_C2_04_an_ALTERNATIVE_without_a_REJECTED_line_to_point_to_is_refused()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(null, Normal(price: 50m)), User);
        var normal = (await Stored(h, uuid)).Lines.Single();

        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Alternative(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*alternative*");
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Alternative(forUuid: normal.UUID), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*REJECTED*");
        await FluentActions.Awaiting(() => h.Svc.CreateAsync(Header(null, Normal(price: 1m), Alternative(forNumber: 1)), User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task BR_C2_06_an_ALTERNATIVE_cannot_point_to_another_quotations_REJECTED_line()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var other = await h.Svc.CreateAsync(Header(null, Rejected(oos.UUID)), User);
        var foreignRejected = (await Stored(h, other)).Lines.Single();
        var uuid = await h.Svc.CreateAsync(Header(), User);

        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Alternative(forUuid: foreignRejected.UUID), User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_REJECTED_line_needs_an_active_rejection_reason_of_this_organization()
    {
        var h = NewHarness();
        var inactive = await Reason(h, "OLD", active: false);
        var foreign  = await Reason(h, "FOR", org: Guid.NewGuid());
        var uuid = await h.Svc.CreateAsync(Header(), User);

        var noReason = Rejected(Guid.Empty);
        noReason.RejectionReasonUuid = null;
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, noReason, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*reason*");
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Rejected(inactive.UUID), User))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Rejected(foreign.UUID), User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task NORMAL_and_ALTERNATIVE_lines_need_a_variant_and_a_positive_quantity()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(), User);

        var noVariant = Normal();
        noVariant.VariantUuid = null;
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, noVariant, User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(qty: 0m), User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, new SaleQuotationLineRequest { LineType = "BOGUS", VariantUuid = Guid.NewGuid(), Quantity = 1 }, User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Line_totals_follow_the_SaleOrderLine_formula_with_the_tax_code_snapshot_and_the_header_sums_them()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var oos = await Reason(h);

        // 10 × 100, less 10%, plus 17% (code wins over the typed 99) = 1053; 5 × 20 at 5% typed = 105; REJECTED adds nothing.
        var uuid = await h.Svc.CreateAsync(Header(null,
            Normal(qty: 10m, price: 100m, discount: 10m, tax: 99m, taxCode: gst.Uuid),
            Normal(qty: 5m, price: 20m, tax: 5m),
            Rejected(oos.UUID)), User);

        var q = await Stored(h, uuid);
        var first = q.Lines.Single(l => l.LineNumber == 1);
        first.TaxCodeUuid.Should().Be(gst.Uuid);
        first.TaxCode.Should().Be("GST17");
        first.TaxPercent.Should().Be(17m);
        first.TaxAmount.Should().Be(153m);
        first.LineTotal.Should().Be(1053m);
        q.Lines.Single(l => l.LineNumber == 2).LineTotal.Should().Be(105m);

        q.Subtotal.Should().Be(1100m);
        q.DiscountAmount.Should().Be(100m);
        q.TaxAmount.Should().Be(158m);
        q.GrandTotal.Should().Be(1158m);
    }

    [Fact]
    public async Task An_inactive_or_purchase_only_tax_code_is_refused_and_a_typed_percentage_is_held_to_0_100()
    {
        var h = NewHarness();
        var inactive = h.TaxCodes.Add("OLD", 10m, active: false);
        var purchase = h.TaxCodes.Add("INPUT", 10m, TaxCodeUsage.Purchase);
        var uuid = await h.Svc.CreateAsync(Header(), User);

        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(taxCode: inactive.Uuid), User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(taxCode: purchase.Uuid), User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(tax: 101m), User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(discount: 100.5m), User)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task An_omitted_unit_price_comes_from_the_sale_price_waterfall_for_the_customer_at_valid_from()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        h.Pricing.Setup(p => p.ResolveSalePriceAsync(variant, Customer, 4m, From, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SalePriceResolution(true, 37.5m, Pkr, PriceResolutionTier.Contract, Guid.NewGuid()));

        var uuid = await h.Svc.CreateAsync(Header(null, Normal(variant, qty: 4m)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(37.5m);
    }

    [Fact]
    public async Task Updating_and_deleting_lines_recomputes_the_header()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(null, Normal(qty: 2m, price: 10m), Normal(qty: 1m, price: 5m)), User);
        var lines = (await Stored(h, uuid)).Lines.OrderBy(l => l.LineNumber).ToList();

        (await h.Svc.UpdateLineAsync(uuid, lines[0].UUID, Normal(lines[0].VariantUuid, qty: 3m, price: 10m), User)).Should().BeTrue();
        (await Stored(h, uuid)).GrandTotal.Should().Be(35m);

        (await h.Svc.DeleteLineAsync(uuid, lines[1].UUID, User)).Should().BeTrue();
        var q = await Stored(h, uuid);
        q.Lines.Should().ContainSingle();
        q.GrandTotal.Should().Be(30m);
    }

    [Fact]
    public async Task A_REJECTED_line_that_still_has_alternatives_cannot_be_deleted_or_turned_into_another_type()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var uuid = await h.Svc.CreateAsync(Header(null, Rejected(oos.UUID), Alternative(forNumber: 1)), User);
        var rejected = (await Stored(h, uuid)).Lines.Single(l => l.LineType == "REJECTED");

        await FluentActions.Awaiting(() => h.Svc.DeleteLineAsync(uuid, rejected.UUID, User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.UpdateLineAsync(uuid, rejected.UUID, Normal(price: 1m), User)).Should().ThrowAsync<BadRequestException>();
    }

    // ── PC-04 state machine / PC-07 send, responses, accept, reject ──────────

    [Fact]
    public async Task T_C2_05_send_moves_DRAFT_to_SENT_and_stamps_who_and_when()
    {
        var h = NewHarness();
        var before = DateTime.UtcNow;

        var (_, q) = await SentQuotation(h);

        q.Status.Should().Be("SENT");
        q.SentAt.Should().NotBeNull().And.BeOnOrAfter(before);
        q.SentByUserId.Should().Be(User);
    }

    [Fact]
    public async Task BR_C2_04_a_quotation_with_no_NORMAL_or_ALTERNATIVE_line_cannot_be_sent()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var empty = await h.Svc.CreateAsync(Header(), User);
        var onlyRejected = await h.Svc.CreateAsync(Header(null, Rejected(oos.UUID)), User);

        await FluentActions.Awaiting(() => h.Svc.SendAsync(empty, User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.SendAsync(onlyRejected, User)).Should().ThrowAsync<BadRequestException>();
        (await Stored(h, onlyRejected)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task T_C2_06_BR_C2_05_nothing_but_DRAFT_is_editable()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);
        var line = q.Lines.Single();

        await FluentActions.Awaiting(() => h.Svc.UpdateLineAsync(uuid, line.UUID, Normal(price: 1m), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not editable*");
        await FluentActions.Awaiting(() => h.Svc.AddLineAsync(uuid, Normal(price: 1m), User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.DeleteLineAsync(uuid, line.UUID, User)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.UpdateAsync(uuid, new UpdateSaleQuotationRequest { ValidFrom = From, ValidTo = To }, User))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Svc.SendAsync(uuid, User)).Should().ThrowAsync<BadRequestException>();

        var model = (await h.Svc.GetByIdAsync(uuid))!;
        model.IsEditable.Should().BeFalse();
        model.AllowedActions.Should().BeEquivalentTo(["RECORD_RESPONSE", "ACCEPT", "REJECT", "COPY"]);
    }

    [Fact]
    public async Task A_DRAFT_header_update_replaces_the_header_and_keeps_the_customer()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(null, Normal(price: 50m)), User);

        (await h.Svc.UpdateAsync(uuid, new UpdateSaleQuotationRequest
        {
            ValidFrom = From, ValidTo = To.AddDays(10), PaymentTerms = "Net 60", CustomerReference = "RFQ-88"
        }, User)).Should().BeTrue();

        var q = await Stored(h, uuid);
        q.PaymentTerms.Should().Be("Net 60");
        q.CustomerReference.Should().Be("RFQ-88");
        q.ValidTo.Should().Be(To.AddDays(10));
        q.PartnerId.Should().Be(Customer);
        q.ModifiedBy.Should().Be(User);
        q.CurrencyId.Should().Be(Pkr, "an omitted currency falls back to the base currency");

        var model = (await h.Svc.GetByIdAsync(uuid))!;
        model.IsEditable.Should().BeTrue();
        model.AllowedActions.Should().BeEquivalentTo(["SEND", "COPY"]);
    }

    [Fact]
    public async Task T_C2_07_a_customer_ACCEPTED_is_recorded_with_its_date()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);

        await h.Svc.RecordCustomerResponseAsync(uuid, q.Lines.Single().UUID, new RecordCustomerResponseRequest
        {
            Response = "ACCEPTED", ResponseDate = new DateTime(2026, 10, 5), Notes = "OK by phone"
        }, User);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.CustomerResponse.Should().Be("ACCEPTED");
        line.CustomerResponseDate.Should().Be(new DateTime(2026, 10, 5));
        line.CustomerResponseNotes.Should().Be("OK by phone");
    }

    [Fact]
    public async Task T_C2_08_BR_C2_09_a_COUNTER_without_a_positive_price_is_refused()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);
        var line = q.Lines.Single().UUID;

        await FluentActions.Awaiting(() => Respond(h, uuid, line, "COUNTER")).Should().ThrowAsync<BadRequestException>().WithMessage("*counter*");
        await FluentActions.Awaiting(() => Respond(h, uuid, line, "COUNTER", 0m)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task T_C2_09_a_COUNTER_with_its_price_is_saved()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);

        await Respond(h, uuid, q.Lines.Single().UUID, "COUNTER", 45m);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.CustomerResponse.Should().Be("COUNTER");
        line.CustomerCounterPrice.Should().Be(45m);
        line.UnitPrice.Should().Be(50m, "a counter does not change the quoted price until the seller accepts it");
        line.CustomerResponseDate.Should().NotBeNull();
    }

    [Fact]
    public async Task Accepting_a_counter_price_takes_it_as_the_line_price_and_recomputes_the_totals()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h, Normal(qty: 10m, price: 50m));
        var line = q.Lines.Single().UUID;
        await Respond(h, uuid, line, "COUNTER", 45m);

        await Respond(h, uuid, line, "ACCEPTED", acceptCounter: true);

        var stored = await Stored(h, uuid);
        stored.Lines.Single().UnitPrice.Should().Be(45m);
        stored.Lines.Single().LineTotal.Should().Be(450m);
        stored.Lines.Single().CustomerResponse.Should().Be("ACCEPTED");
        stored.GrandTotal.Should().Be(450m);
    }

    [Fact]
    public async Task Accepting_a_counter_price_on_a_line_with_no_counter_is_refused()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);

        await FluentActions.Awaiting(() => Respond(h, uuid, q.Lines.Single().UUID, "ACCEPTED", acceptCounter: true))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Customer_responses_are_only_recorded_while_SENT_and_never_on_a_REJECTED_line()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var draft = await h.Svc.CreateAsync(Header(null, Normal(price: 50m)), User);
        var draftLine = (await Stored(h, draft)).Lines.Single().UUID;
        await FluentActions.Awaiting(() => Respond(h, draft, draftLine, "ACCEPTED"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*SENT*");

        var (uuid, q) = await SentQuotation(h, Normal(price: 50m), Rejected(oos.UUID));
        await FluentActions.Awaiting(() => Respond(h, uuid, q.Lines.Single(l => l.LineType == "REJECTED").UUID, "ACCEPTED"))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => Respond(h, uuid, q.Lines.Single(l => l.LineType == "NORMAL").UUID, "MAYBE"))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task T_C2_10_BR_C2_07_accept_needs_at_least_one_ACCEPTED_line()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h, Normal(price: 50m), Normal(price: 20m));
        var lines = q.Lines.OrderBy(l => l.LineNumber).ToList();

        await FluentActions.Awaiting(() => h.Svc.AcceptAsync(uuid, User)).Should().ThrowAsync<BadRequestException>();

        await Respond(h, uuid, lines[0].UUID, "ACCEPTED");
        (await h.Svc.AcceptAsync(uuid, User)).Should().BeTrue();

        (await Stored(h, uuid)).Status.Should().Be("ACCEPTED");
        (await h.Svc.GetByIdAsync(uuid))!.AllowedActions.Should().BeEquivalentTo(["CONVERT", "COPY"]);
        await FluentActions.Awaiting(() => h.Svc.AcceptAsync(uuid, User)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task BR_C2_08_reject_needs_every_offered_line_rejected_by_the_customer()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var (uuid, q) = await SentQuotation(h, Normal(price: 50m), Normal(price: 20m), Rejected(oos.UUID));
        var offered = q.Lines.Where(l => l.LineType == "NORMAL").ToList();

        await Respond(h, uuid, offered[0].UUID, "REJECTED");
        await FluentActions.Awaiting(() => h.Svc.RejectAsync(uuid, "Too dear", User)).Should().ThrowAsync<BadRequestException>();

        await Respond(h, uuid, offered[1].UUID, "REJECTED");
        (await h.Svc.RejectAsync(uuid, "Too dear", User)).Should().BeTrue();

        var stored = await Stored(h, uuid);
        stored.Status.Should().Be("REJECTED");
        stored.InternalNotes.Should().Contain("Too dear");
        (await h.Svc.GetByIdAsync(uuid))!.AllowedActions.Should().BeEquivalentTo(["COPY"]);
    }

    // ── PC-07 convert ────────────────────────────────────────────────────────

    [Fact]
    public async Task T_C2_12_T_C2_13_convert_sends_only_ACCEPTED_lines_at_their_quoted_price_and_marks_the_quotation_CONVERTED()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var oos = await Reason(h);
        var (uuid, q) = await SentQuotation(h,
            Normal(qty: 10m, price: 50m, discount: 5m, taxCode: gst.Uuid),
            Rejected(oos.UUID),
            Alternative(forNumber: 2, qty: 200m, price: 48m),
            Alternative(forNumber: 2, qty: 150m, price: 45m),
            Normal(qty: 50m, price: 120m));
        var byNumber = q.Lines.ToDictionary(l => l.LineNumber);
        await Respond(h, uuid, byNumber[1].UUID, "ACCEPTED");
        await Respond(h, uuid, byNumber[3].UUID, "ACCEPTED");
        await Respond(h, uuid, byNumber[4].UUID, "REJECTED");
        await Respond(h, uuid, byNumber[5].UUID, "COUNTER", 110m);   // unresolved — excluded (T-C2-13)
        await h.Svc.AcceptAsync(uuid, User);

        var soUuid = await h.Svc.ConvertToOrderAsync(uuid, new ConvertSaleQuotationToOrderRequest
        {
            CustomerPoReference = "PO-4711", CustomerPoDate = new DateTime(2026, 10, 6), DeliveryMode = "SELF_PICKUP", Notes = "rush"
        }, User);

        soUuid.Should().NotBeNull();
        var cmd = h.Commands.Should().ContainSingle().Subject;
        cmd.SourceQuotationUuid.Should().Be(uuid);
        cmd.CustomerPoReference.Should().Be("PO-4711");
        cmd.CustomerPoDate.Should().Be(new DateTime(2026, 10, 6));
        cmd.DeliveryMode.Should().Be("SELF_PICKUP");
        cmd.Notes.Should().Be("rush");
        cmd.Lines.Should().HaveCount(2);
        cmd.Lines.Should().ContainEquivalentOf(new QuotedSaleOrderLine
        {
            VariantUuid = byNumber[1].VariantUuid!.Value, Quantity = 10m, UnitPrice = 50m, DiscountPercent = 5m, TaxPercent = 17m, TaxCodeUuid = gst.Uuid
        });
        cmd.Lines.Should().ContainEquivalentOf(new QuotedSaleOrderLine
        {
            VariantUuid = byNumber[3].VariantUuid!.Value, Quantity = 200m, UnitPrice = 48m, DiscountPercent = 0m, TaxPercent = 0m
        });

        h.StatusSeenByOrderService.Should().Be("CONVERTED", "the quotation is marked before the order service's single save");
        var stored = await Stored(h, uuid);
        stored.Status.Should().Be("CONVERTED");
        stored.ModifiedBy.Should().Be(User);
    }

    [Fact]
    public async Task Only_an_ACCEPTED_quotation_converts_and_a_second_conversion_is_a_conflict()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);

        await FluentActions.Awaiting(() => h.Svc.ConvertToOrderAsync(uuid, new(), User)).Should().ThrowAsync<BadRequestException>();

        await Respond(h, uuid, q.Lines.Single().UUID, "ACCEPTED");
        await h.Svc.AcceptAsync(uuid, User);
        await h.Svc.ConvertToOrderAsync(uuid, new(), User);

        await FluentActions.Awaiting(() => h.Svc.ConvertToOrderAsync(uuid, new(), User)).Should().ThrowAsync<ConflictException>();
        h.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_order_creation_leaves_the_quotation_ACCEPTED()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);
        await Respond(h, uuid, q.Lines.Single().UUID, "ACCEPTED");
        await h.Svc.AcceptAsync(uuid, User);
        h.SaleOrders.Setup(s => s.CreateFromQuotationAsync(It.IsAny<CreateSaleOrderFromQuotationCommand>(), It.IsAny<int>()))
                    .ThrowsAsync(new BadRequestException("A shipping address is required when the delivery mode is SHIP."));

        await FluentActions.Awaiting(() => h.Svc.ConvertToOrderAsync(uuid, new(), User)).Should().ThrowAsync<BadRequestException>();

        (await Stored(h, uuid)).Status.Should().Be("ACCEPTED");
    }

    // ── PC-08 create from inquiry ────────────────────────────────────────────

    private static async Task<SaleInquiry> ReviewedInquiry(H h, RejectionReason reason, Guid? org = null, string status = "REVIEW_COMPLETE")
    {
        var inquiry = new SaleInquiry
        {
            OrganizationId = org ?? h.OrgId, InquiryNumber = "INQ-2026-00001", PartnerId = Customer, Status = status,
            CustomerReference = "THEIR-RFQ-9", CustomerReferenceDate = new DateTime(2026, 9, 28), ReceivedDate = new DateTime(2026, 9, 29),
            Lines =
            {
                new SaleInquiryLine
                {
                    OrganizationId = org ?? h.OrgId, LineNumber = 1, VariantUuid = Guid.NewGuid(), ProductDescription = "Blue widget",
                    RequestedQuantity = 100m, LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = new DateTime(2026, 10, 20)
                },
                new SaleInquiryLine
                {
                    OrganizationId = org ?? h.OrgId, LineNumber = 2, VariantUuid = Guid.NewGuid(), ProductDescription = "Red widget",
                    RequestedQuantity = 1000m, CanSupplyQuantity = 600m, LineStatus = "PARTIAL", EstimatedDeliveryDate = new DateTime(2026, 10, 25)
                },
                new SaleInquiryLine
                {
                    OrganizationId = org ?? h.OrgId, LineNumber = 3, ProductDescription = "Some gadget we do not stock",
                    RequestedQuantity = 50m, LineStatus = "CANNOT_SUPPLY", RejectionReasonId = reason.Id, RejectionNotes = "Not in range"
                },
                new SaleInquiryLine
                {
                    OrganizationId = org ?? h.OrgId, LineNumber = 4, VariantUuid = Guid.NewGuid(), ProductDescription = "Green widget",
                    RequestedQuantity = 30m, LineStatus = "CANNOT_SUPPLY", RejectionReasonId = reason.Id,
                    AlternativeVariantUuid = Guid.NewGuid(), AlternativeNotes = "Olive is the same part"
                }
            }
        };
        h.Db.SaleInquiries.Add(inquiry);
        await h.Db.SaveChangesAsync();
        return inquiry;
    }

    private static CreateSaleQuotationFromInquiryRequest FromInquiry() => new() { CurrencyId = Pkr, ValidFrom = From, ValidTo = To, PaymentTerms = "Net 30" };

    [Fact]
    public async Task T_C2_02_T_C1_11_a_quotation_from_a_reviewed_inquiry_maps_every_line_and_marks_the_inquiry_QUOTED_in_the_same_save()
    {
        var h = NewHarness();
        var nip = await Reason(h, "NIP");
        var inquiry = await ReviewedInquiry(h, nip);
        var inqLines = inquiry.Lines.ToDictionary(l => l.LineNumber);

        var uuid = await h.Svc.CreateFromInquiryAsync(inquiry.UUID, FromInquiry(), User);

        h.QuotationsSavedWhenMarkedQuoted.Should().Be(0, "MarkQuotedAsync runs before the one SaveChangesAsync");
        (await h.Db.SaleInquiries.AsNoTracking().SingleAsync()).Status.Should().Be("QUOTED");

        var q = await Stored(h, uuid);
        q.Status.Should().Be("DRAFT");
        q.SourceInquiryId.Should().Be(inquiry.Id);
        q.PartnerId.Should().Be(Customer);
        q.CustomerReference.Should().Be("THEIR-RFQ-9");
        q.PaymentTerms.Should().Be("Net 30");

        var lines = q.Lines.OrderBy(l => l.LineNumber).ToList();
        lines.Select(l => (l.LineType, l.Quantity)).Should().Equal(
            ("NORMAL", 100m), ("NORMAL", 600m), ("REJECTED", 50m), ("REJECTED", 30m), ("ALTERNATIVE", 30m));
        lines.Select(l => l.SourceInquiryLineId).Should().Equal(
            inqLines[1].Id, inqLines[2].Id, inqLines[3].Id, inqLines[4].Id, inqLines[4].Id);

        lines[0].VariantUuid.Should().Be(inqLines[1].VariantUuid);
        lines[0].UnitPrice.Should().Be(100m, "the sale-price waterfall prices NORMAL lines");
        lines[0].PromisedDeliveryDate.Should().Be(new DateTime(2026, 10, 20));
        lines[1].PromisedDeliveryDate.Should().Be(new DateTime(2026, 10, 25));
        lines[2].VariantUuid.Should().BeNull("a free-text line can still be rejected");
        lines[2].RejectionReasonId.Should().Be(nip.Id);
        lines[2].RejectionNotes.Should().Be("Not in range");
        lines[2].UnitPrice.Should().Be(0m);
        lines[4].VariantUuid.Should().Be(inqLines[4].AlternativeVariantUuid);
        lines[4].AlternativeForLineId.Should().Be(lines[3].Id);
        lines[4].AlternativeNotes.Should().Be("Olive is the same part");
        lines[4].UnitPrice.Should().Be(100m);
        q.GrandTotal.Should().Be((100m + 600m + 30m) * 100m);

        (await h.Svc.GetByIdAsync(uuid))!.SourceInquiry!.Number.Should().Be("INQ-2026-00001");
        h.Inquiries.Verify(i => i.MarkQuotedAsync(inquiry.UUID, User), Times.Once);
    }

    [Fact]
    public async Task A_CAN_SUPPLY_line_with_no_catalog_item_is_refused_naming_the_line()
    {
        var h = NewHarness();
        var inquiry = await ReviewedInquiry(h, await Reason(h));
        inquiry.Lines.Single(l => l.LineNumber == 2).VariantUuid = null;
        await h.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => h.Svc.CreateFromInquiryAsync(inquiry.UUID, FromInquiry(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*line 2*");
        (await h.Db.SaleQuotations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Only_a_REVIEW_COMPLETE_inquiry_of_this_organization_can_be_quoted()
    {
        var h = NewHarness();
        var reason = await Reason(h);
        var underReview = await ReviewedInquiry(h, reason, status: "UNDER_REVIEW");
        await FluentActions.Awaiting(() => h.Svc.CreateFromInquiryAsync(underReview.UUID, FromInquiry(), User))
            .Should().ThrowAsync<BadRequestException>();

        var quoted = await ReviewedInquiry(h, reason, status: "QUOTED");
        await FluentActions.Awaiting(() => h.Svc.CreateFromInquiryAsync(quoted.UUID, FromInquiry(), User))
            .Should().ThrowAsync<ConflictException>();

        var foreign = await ReviewedInquiry(h, reason, org: Guid.NewGuid());
        h.Tenant.IsSuperAdmin = true;
        await FluentActions.Awaiting(() => h.Svc.CreateFromInquiryAsync(foreign.UUID, FromInquiry(), User))
            .Should().ThrowAsync<NotFoundException>();

        (await h.Db.SaleQuotations.CountAsync()).Should().Be(0);
    }

    // ── copy ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_makes_a_new_DRAFT_with_the_lines_and_fresh_responses()
    {
        var h = NewHarness();
        var oos = await Reason(h);
        var (uuid, q) = await SentQuotation(h, Normal(price: 50m), Rejected(oos.UUID), Alternative(forNumber: 2));
        await Respond(h, uuid, q.Lines.Single(l => l.LineNumber == 1).UUID, "COUNTER", 40m);

        var copyUuid = await h.Svc.CopyAsync(uuid, User);

        var copy = await Stored(h, copyUuid!.Value);
        copy.Status.Should().Be("DRAFT");
        copy.QuotationNumber.Should().NotBe(q.QuotationNumber);
        copy.Lines.Should().HaveCount(3);
        copy.Lines.Should().OnlyContain(l => l.CustomerResponse == "PENDING" && l.CustomerCounterPrice == null);
        var copiedRejected = copy.Lines.Single(l => l.LineType == "REJECTED");
        copy.Lines.Single(l => l.LineType == "ALTERNATIVE").AlternativeForLineId.Should().Be(copiedRejected.Id);
        (await Stored(h, uuid)).Status.Should().Be("SENT", "the original is untouched");
    }

    // ── own organization (super admin included) ──────────────────────────────

    [Fact]
    public async Task Another_organizations_quotation_is_not_found_for_every_action_even_for_a_super_admin()
    {
        var h = NewHarness();
        var (uuid, q) = await SentQuotation(h);
        var line = q.Lines.Single().UUID;

        h.Tenant.OrganizationId = Guid.NewGuid();
        h.Tenant.IsSuperAdmin = true;

        (await h.Svc.GetByIdAsync(uuid)).Should().BeNull();
        (await h.Svc.GetListAsync(new SaleQuotationListFilter())).Data.Should().BeEmpty();
        (await h.Svc.UpdateAsync(uuid, new UpdateSaleQuotationRequest { ValidFrom = From, ValidTo = To }, User)).Should().BeFalse();
        (await h.Svc.AddLineAsync(uuid, Normal(price: 1m), User)).Should().BeNull();
        (await h.Svc.UpdateLineAsync(uuid, line, Normal(price: 1m), User)).Should().BeFalse();
        (await h.Svc.DeleteLineAsync(uuid, line, User)).Should().BeFalse();
        (await h.Svc.SendAsync(uuid, User)).Should().BeFalse();
        (await h.Svc.RecordCustomerResponseAsync(uuid, line, new RecordCustomerResponseRequest { Response = "ACCEPTED" }, User)).Should().BeFalse();
        (await h.Svc.AcceptAsync(uuid, User)).Should().BeFalse();
        (await h.Svc.RejectAsync(uuid, null, User)).Should().BeFalse();
        (await h.Svc.ConvertToOrderAsync(uuid, new(), User)).Should().BeNull();
        (await h.Svc.CopyAsync(uuid, User)).Should().BeNull();

        (await Stored(h, uuid)).Status.Should().Be("SENT");
    }

    [Fact]
    public async Task An_unknown_line_of_an_own_quotation_is_not_found()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(null, Normal(price: 1m)), User);

        (await h.Svc.UpdateLineAsync(uuid, Guid.NewGuid(), Normal(price: 1m), User)).Should().BeFalse();
        (await h.Svc.DeleteLineAsync(uuid, Guid.NewGuid(), User)).Should().BeFalse();
    }

    // ── list ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_status_and_search_and_pages()
    {
        var h = NewHarness();
        await SentQuotation(h);
        await h.Svc.CreateAsync(Header(), User);
        await h.Svc.CreateAsync(Header(), User);

        var sent = await h.Svc.GetListAsync(new SaleQuotationListFilter { Status = "SENT" });
        sent.TotalRecords.Should().Be(1);
        sent.Data.Single().LineCount.Should().Be(1);
        sent.Data.Single().GrandTotal.Should().Be(500m);

        var page = await h.Svc.GetListAsync(new SaleQuotationListFilter { PageSize = 2, Page = 2 });
        page.TotalRecords.Should().Be(3);
        page.Data.Should().ContainSingle();

        (await h.Svc.GetListAsync(new SaleQuotationListFilter { Search = "00002" })).Data.Single().QuotationNumber.Should().Be("SQ-2026-00002");
    }
}
