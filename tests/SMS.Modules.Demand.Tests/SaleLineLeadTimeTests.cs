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
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 PC-06 (D-15, D-16), T-C4-06/07: lead-time fields on inquiry, quotation and sale order lines — the effective date
/// (manual ?? calculated) and its source derived in the models, the full SO POST/PUT round-trip (C-14, lines are
/// rebuilt), the conversion copies (inquiry → quotation → SO), and an inquiry's CAN_SUPPLY accepting the calculated date.
/// </summary>
public class SaleLineLeadTimeTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Variant  = Guid.NewGuid();
    private static readonly Guid Product  = Guid.NewGuid();

    // ── sale orders ─────────────────────────────────────────────────────────

    private sealed record SoHarness(DemandDbContext Db, SaleOrderService Service, Guid OrgId, string DbName);

    private static SoHarness NewSo()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var numbers = new Mock<IDocumentNumberGenerator>();
        var n = 0;
        numbers.Setup(x => x.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"SO-2026-{++n:00000}");
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 10m, Currency, PriceResolutionTier.DefaultSelling, null));
        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReservationSummary>());
        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, stock.Object, Mock.Of<ITimelineService>(),
            Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(), Mock.Of<IPurchaseOrderService>(),
            Mock.Of<ISaleOrderEmailService>());
        return new SoHarness(db, service, tenant.OrganizationId, dbName);
    }

    private static async Task<SaleOrder> ReadSo(SoHarness h, Guid uuid)
    {
        await using var fresh = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, new StaticTenantContext { OrganizationId = h.OrgId });
        return await fresh.SaleOrders.Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);
    }

    private static CreateSaleOrderLineRequest SoLine(int? days = null, DateTime? calculated = null, DateTime? manual = null, DateTime? at = null) => new()
    {
        VariantUuid = Guid.NewGuid(), Quantity = 2m,
        CalculatedLeadTimeDays = days, CalculatedDeliveryDate = calculated, LeadTimeCalculatedAt = at, ManualDeliveryDate = manual
    };

    [Fact]
    public async Task T_C4_06_the_full_POST_keeps_the_lead_time_fields_and_the_detail_derives_the_effective_date_and_its_source()
    {
        var h = NewSo();
        var at = new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc);

        var uuid = await h.Service.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Customer, CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines =
            [
                SoLine(5, new DateTime(2026, 10, 9, 13, 0, 0), new DateTime(2026, 10, 20, 8, 0, 0), at), // both → MANUAL
                SoLine(5, new DateTime(2026, 10, 9), at: at),                                           // calculated only
                SoLine()                                                                                 // nothing
            ]
        }, User);

        var stored = (await ReadSo(h, uuid)).Lines.OrderBy(l => l.Id).ToList();
        stored[0].CalculatedLeadTimeDays.Should().Be(5);
        stored[0].CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 9), "dates are date-only");
        stored[0].ManualDeliveryDate.Should().Be(new DateTime(2026, 10, 20), "dates are date-only");
        stored[0].LeadTimeCalculatedAt.Should().Be(at);

        var lines = (await h.Service.GetByIdAsync(uuid))!.Lines;
        var byDays = lines.OrderBy(l => l.CalculatedLeadTimeDays is null).ThenBy(l => l.ManualDeliveryDate is null).ToList();
        byDays[0].EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 20));
        byDays[0].DeliveryDateSource.Should().Be("MANUAL");
        byDays[0].CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 9));
        byDays[0].LeadTimeCalculatedAt.Should().Be(at);
        byDays[1].EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 9));
        byDays[1].DeliveryDateSource.Should().Be("CALCULATED");
        byDays[1].ManualDeliveryDate.Should().BeNull();
        byDays[2].EffectiveDeliveryDate.Should().BeNull();
        byDays[2].DeliveryDateSource.Should().Be("NONE");
    }

    [Fact]
    public async Task C_14_the_full_PUT_rebuilds_the_lines_but_keeps_the_lead_time_fields_it_is_sent()
    {
        var h = NewSo();
        var uuid = await h.Service.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Customer, CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [SoLine(5, new DateTime(2026, 10, 9))]
        }, User);
        var at = new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc);

        (await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [SoLine(8, new DateTime(2026, 10, 13), new DateTime(2026, 10, 30), at)]
        }, User)).Should().BeTrue();

        var line = (await ReadSo(h, uuid)).Lines.Single();
        line.CalculatedLeadTimeDays.Should().Be(8);
        line.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 13));
        line.ManualDeliveryDate.Should().Be(new DateTime(2026, 10, 30));
        line.LeadTimeCalculatedAt.Should().Be(at);
    }

    [Fact]
    public async Task A_negative_calculated_lead_time_is_refused()
    {
        var h = NewSo();

        await FluentActions.Awaiting(() => h.Service.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Customer, CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [SoLine(-1, new DateTime(2026, 10, 9))]
        }, User)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task D_15_an_order_from_a_quotation_takes_the_promised_date_as_its_manual_date_and_the_calculated_fields()
    {
        var h = NewSo();
        var quotation = new SaleQuotation
        {
            QuotationNumber = "SQ-2026-00001", PartnerId = Customer, CurrencyId = Currency, Status = "CONVERTED",
            ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31), CreatedBy = User
        };
        h.Db.SaleQuotations.Add(quotation);
        await h.Db.SaveChangesAsync();
        var at = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);

        var uuid = await h.Service.CreateFromQuotationAsync(new CreateSaleOrderFromQuotationCommand
        {
            SourceQuotationUuid = quotation.UUID, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines =
            [
                new QuotedSaleOrderLine
                {
                    VariantUuid = Variant, Quantity = 3m, UnitPrice = 50m, ManualDeliveryDate = new DateTime(2026, 10, 25),
                    CalculatedLeadTimeDays = 6, CalculatedDeliveryDate = new DateTime(2026, 10, 9), LeadTimeCalculatedAt = at
                }
            ]
        }, User);

        var line = (await ReadSo(h, uuid)).Lines.Single();
        line.ManualDeliveryDate.Should().Be(new DateTime(2026, 10, 25));
        line.CalculatedLeadTimeDays.Should().Be(6);
        line.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 9));
        line.LeadTimeCalculatedAt.Should().Be(at);
    }

    // ── quotations ──────────────────────────────────────────────────────────

    private sealed record QHarness(DemandDbContext Db, SaleQuotationService Service, Guid OrgId, List<CreateSaleOrderFromQuotationCommand> Commands);

    private static QHarness NewQuotations()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"SQ-2026-{++seq:D5}");
        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", true, false, true));
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new SalePriceResolution(true, 100m, null, PriceResolutionTier.VariantDefault, null));
        var inquiries = new Mock<ISaleInquiryService>();
        inquiries.Setup(i => i.MarkQuotedAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                 .Returns(async (Guid uuid, int _) => (await db.SaleInquiries.FirstAsync(x => x.UUID == uuid)).Status = "QUOTED");
        var commands = new List<CreateSaleOrderFromQuotationCommand>();
        var saleOrders = new Mock<ISaleOrderService>();
        saleOrders.Setup(s => s.CreateFromQuotationAsync(It.IsAny<CreateSaleOrderFromQuotationCommand>(), It.IsAny<int>()))
                  .Returns(async (CreateSaleOrderFromQuotationCommand cmd, int _) => { commands.Add(cmd); await db.SaveChangesAsync(); return Guid.NewGuid(); });
        var svc = new SaleQuotationService(db, tenant, numbers.Object, partners.Object, orgCurrency.Object, pricing.Object,
            inquiries.Object, saleOrders.Object);
        return new QHarness(db, svc, tenant.OrganizationId, commands);
    }

    private static SaleQuotationLineRequest QLine(Guid? variant = null, decimal qty = 10m, DateTime? promised = null) => new()
    {
        LineType = "NORMAL", VariantUuid = variant ?? Variant, Quantity = qty, UnitPrice = 50m, ProductDescription = "Widget",
        PromisedDeliveryDate = promised
    };

    private static async Task<SaleQuotationLine> SetCalculatedAsync(QHarness h, Guid quotationUuid, int days, DateTime date, DateTime at)
    {
        var line = await h.Db.SaleQuotationLines.SingleAsync(l => l.SaleQuotation.UUID == quotationUuid);
        line.CalculatedLeadTimeDays = days;
        line.CalculatedDeliveryDate = date;
        line.LeadTimeCalculatedAt   = at;
        await h.Db.SaveChangesAsync();
        return line;
    }

    [Fact]
    public async Task D_15_a_quotation_line_derives_its_effective_date_from_the_promised_date_then_the_calculated_one()
    {
        var h = NewQuotations();
        var uuid = await h.Service.CreateAsync(new CreateSaleQuotationRequest
        {
            PartnerId = Customer, CurrencyId = Currency, ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31),
            Lines = [QLine()]
        }, User);
        await SetCalculatedAsync(h, uuid, 4, new DateTime(2026, 10, 8), DateTime.UtcNow);

        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        line.CalculatedLeadTimeDays.Should().Be(4);
        line.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        line.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        line.DeliveryDateSource.Should().Be("CALCULATED");

        var stored = await h.Db.SaleQuotationLines.SingleAsync();
        stored.PromisedDeliveryDate = new DateTime(2026, 10, 15);
        await h.Db.SaveChangesAsync();
        line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        line.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 15));
        line.DeliveryDateSource.Should().Be("MANUAL");
    }

    [Fact]
    public async Task Changing_a_quotation_lines_variant_or_quantity_clears_its_calculated_lead_time_but_other_edits_keep_it()
    {
        var h = NewQuotations();
        var uuid = await h.Service.CreateAsync(new CreateSaleQuotationRequest
        {
            PartnerId = Customer, CurrencyId = Currency, ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31),
            Lines = [QLine()]
        }, User);
        var line = await SetCalculatedAsync(h, uuid, 4, new DateTime(2026, 10, 8), DateTime.UtcNow);

        (await h.Service.UpdateLineAsync(uuid, line.UUID, QLine(promised: new DateTime(2026, 10, 12)), User)).Should().BeTrue();
        (await h.Db.SaleQuotationLines.AsNoTracking().SingleAsync()).CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 8),
            "the variant and quantity it was calculated for are unchanged");

        (await h.Service.UpdateLineAsync(uuid, line.UUID, QLine(qty: 12m), User)).Should().BeTrue();
        var stored = await h.Db.SaleQuotationLines.AsNoTracking().SingleAsync();
        stored.CalculatedLeadTimeDays.Should().BeNull("it was calculated for another quantity");
        stored.CalculatedDeliveryDate.Should().BeNull();
        stored.LeadTimeCalculatedAt.Should().BeNull();
    }

    [Fact]
    public async Task D_15_converting_sends_the_promised_date_as_the_manual_date_and_the_calculated_fields()
    {
        var h = NewQuotations();
        var uuid = await h.Service.CreateAsync(new CreateSaleQuotationRequest
        {
            PartnerId = Customer, CurrencyId = Currency, ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31),
            Lines = [QLine(promised: new DateTime(2026, 10, 22))]
        }, User);
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var line = await SetCalculatedAsync(h, uuid, 4, new DateTime(2026, 10, 8), at);
        await h.Service.SendAsync(uuid, User);
        await h.Service.RecordCustomerResponseAsync(uuid, line.UUID, new RecordCustomerResponseRequest { Response = "ACCEPTED" }, User);
        await h.Service.AcceptAsync(uuid, User);

        await h.Service.ConvertToOrderAsync(uuid, new ConvertSaleQuotationToOrderRequest(), User);

        var sent = h.Commands.Should().ContainSingle().Subject.Lines.Should().ContainSingle().Subject;
        sent.ManualDeliveryDate.Should().Be(new DateTime(2026, 10, 22));
        sent.CalculatedLeadTimeDays.Should().Be(4);
        sent.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        sent.LeadTimeCalculatedAt.Should().Be(at);
    }

    [Fact]
    public async Task D_15_a_quotation_from_an_inquiry_copies_the_estimate_and_the_calculated_fields_onto_the_offered_line()
    {
        var h = NewQuotations();
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var inquiry = new SaleInquiry
        {
            InquiryNumber = "INQ-2026-00001", PartnerId = Customer, ReceivedDate = new DateTime(2026, 10, 1), Status = "REVIEW_COMPLETE", CreatedBy = User,
            Lines =
            {
                new SaleInquiryLine
                {
                    LineNumber = 1, VariantUuid = Variant, ProductUuid = Product, ProductDescription = "Widget", RequestedQuantity = 10m,
                    LineStatus = "CAN_SUPPLY", CalculatedLeadTimeDays = 6, CalculatedDeliveryDate = new DateTime(2026, 10, 8), LeadTimeCalculatedAt = at
                }
            }
        };
        h.Db.SaleInquiries.Add(inquiry);
        await h.Db.SaveChangesAsync();

        var uuid = await h.Service.CreateFromInquiryAsync(inquiry.UUID, new CreateSaleQuotationFromInquiryRequest
        {
            CurrencyId = Currency, ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31)
        }, User);

        var line = await h.Db.SaleQuotationLines.AsNoTracking().SingleAsync(l => l.SaleQuotation.UUID == uuid);
        line.PromisedDeliveryDate.Should().BeNull("no estimate was typed on the inquiry line");
        line.CalculatedLeadTimeDays.Should().Be(6);
        line.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        line.LeadTimeCalculatedAt.Should().Be(at);
    }

    // ── inquiries ───────────────────────────────────────────────────────────

    private sealed record IHarness(DemandDbContext Db, SaleInquiryService Service, Guid OrgId);

    private static IHarness NewInquiries()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", true, false, true));
        var variants = new Mock<IProductVariantResolver>();
        variants.Setup(v => v.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) => (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(i => i == Variant).Distinct()
                    .ToDictionary(i => i, _ => new VariantDescription(Variant, Product, "SKU-1", "Red", "Widget", false, "PCS")));
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync(new List<UserIdentity>());
        var svc = new SaleInquiryService(db, tenant, Mock.Of<IDocumentNumberGenerator>(), partners.Object, variants.Object, users.Object);
        return new IHarness(db, svc, tenant.OrganizationId);
    }

    private static async Task<(Guid Inquiry, Guid Line)> SeedInquiryAsync(IHarness h, DateTime? calculated)
    {
        var inquiry = new SaleInquiry
        {
            InquiryNumber = "INQ-2026-00001", PartnerId = Customer, ReceivedDate = new DateTime(2026, 10, 1), Status = "UNDER_REVIEW", CreatedBy = User,
            Lines =
            {
                new SaleInquiryLine
                {
                    LineNumber = 1, VariantUuid = Variant, ProductUuid = Product, ProductDescription = "Widget", RequestedQuantity = 10m,
                    CalculatedLeadTimeDays = calculated is null ? null : 6, CalculatedDeliveryDate = calculated,
                    LeadTimeCalculatedAt = calculated is null ? null : DateTime.UtcNow
                }
            }
        };
        h.Db.SaleInquiries.Add(inquiry);
        await h.Db.SaveChangesAsync();
        return (inquiry.UUID, inquiry.Lines.Single().UUID);
    }

    private static UpdateSaleInquiryLineRequest CanSupply(decimal qty = 10m, DateTime? estimate = null, string status = "CAN_SUPPLY", decimal? canSupply = null) => new()
    {
        VariantUuid = Variant, ProductUuid = Product, ProductDescription = "Widget", RequestedQuantity = qty,
        LineStatus = status, EstimatedDeliveryDate = estimate, CanSupplyQuantity = canSupply
    };

    [Fact]
    public async Task D_15_CAN_SUPPLY_accepts_the_calculated_date_when_no_estimate_is_typed_and_shows_it_as_the_effective_date()
    {
        var h = NewInquiries();
        var (uuid, line) = await SeedInquiryAsync(h, new DateTime(2026, 10, 8));

        (await h.Service.UpdateLineAsync(uuid, line, CanSupply(), User)).Should().BeTrue();

        var model = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        model.LineStatus.Should().Be("CAN_SUPPLY");
        model.EstimatedDeliveryDate.Should().BeNull("the manual date stays empty; the calculated one stands in for it");
        model.CalculatedDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        model.CalculatedLeadTimeDays.Should().Be(6);
        model.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 8));
        model.DeliveryDateSource.Should().Be("CALCULATED");
    }

    [Fact]
    public async Task CAN_SUPPLY_still_needs_a_date_when_nothing_was_calculated_and_a_typed_estimate_wins()
    {
        var h = NewInquiries();
        var (uuid, line) = await SeedInquiryAsync(h, null);

        await FluentActions.Awaiting(() => h.Service.UpdateLineAsync(uuid, line, CanSupply(), User)).Should().ThrowAsync<BadRequestException>();

        var h2 = NewInquiries();
        var (uuid2, line2) = await SeedInquiryAsync(h2, new DateTime(2026, 10, 8));
        await h2.Service.UpdateLineAsync(uuid2, line2, CanSupply(estimate: new DateTime(2026, 10, 18)), User);
        var model = (await h2.Service.GetByIdAsync(uuid2))!.Lines.Single();
        model.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 10, 18));
        model.DeliveryDateSource.Should().Be("MANUAL");
    }

    [Fact]
    public async Task Changing_an_inquiry_lines_quantity_clears_its_calculated_lead_time()
    {
        var h = NewInquiries();
        var (uuid, line) = await SeedInquiryAsync(h, new DateTime(2026, 10, 8));

        await h.Service.UpdateLineAsync(uuid, line, CanSupply(qty: 20m, estimate: new DateTime(2026, 10, 18)), User);

        var stored = await h.Db.SaleInquiryLines.AsNoTracking().SingleAsync();
        stored.CalculatedLeadTimeDays.Should().BeNull();
        stored.CalculatedDeliveryDate.Should().BeNull();
        stored.LeadTimeCalculatedAt.Should().BeNull();
    }
}
