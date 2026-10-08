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
/// A34 PC-06 / D-16 (API-CONTRACT §5.2): the line ⏱ endpoints for sale orders, inquiries and quotations (the calculator
/// with the right route, quantity and requested date; Calculated* stored; status rules; the Inventory gate) and the SO
/// line's manual delivery date (DRAFT / CONFIRMED / PARTIALLY_FULFILLED, under the lock, no re-pricing, production not
/// rescheduled). T-C4-06/07.
/// </summary>
public class SaleLineLeadTimeEndpointTests
{
    private const int User = A34SoHarness.User;

    private static async Task SetStatusAsync(A34SoHarness h, Guid so, string status)
    {
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.Status = status;
        await h.Db.SaveChangesAsync();
    }

    // ── sale order line: POST …/lead-time ──────────────────────────────────

    [Fact]
    public async Task T_C4_06_a_draft_line_is_calculated_on_its_effective_route_with_the_orders_expected_date()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var expected = new DateTime(2026, 12, 1);
        var so = await h.DraftAsync("SHIP", Guid.NewGuid(), expected, (mto, null), (Guid.NewGuid(), null));
        var lineUuid = (await h.ReadAsync(so)).Lines.Single(l => l.VariantUuid == mto).UUID;
        h.LeadTimes.Days = 9;

        var result = (await h.Service.CalculateLineLeadTimeAsync(so, lineUuid, User))!;

        var call = h.LeadTimes.Calls.Should().ContainSingle().Subject;
        call.Org.Should().Be(h.OrgId);
        call.Request.VariantUuid.Should().Be(mto);
        call.Request.Quantity.Should().Be(10m);
        call.Request.RouteUuid.Should().Be(h.MfgShip.Uuid, "the line's effective route (its variant's)");
        call.Request.RequestedDate.Should().Be(expected);

        result.LeadTime.TotalLeadTimeDays.Should().Be(9);
        result.Line.Uuid.Should().Be(lineUuid);
        result.Line.CalculatedLeadTimeDays.Should().Be(9);
        result.Line.CalculatedDeliveryDate.Should().Be(DateTime.UtcNow.Date.AddDays(9));
        result.Line.DeliveryDateSource.Should().Be("CALCULATED");
        result.Line.EffectiveRouteCategory.Should().Be("MANUFACTURE");

        var stored = (await h.ReadAsync(so)).Lines.Single(l => l.UUID == lineUuid);
        stored.CalculatedLeadTimeDays.Should().Be(9);
        stored.CalculatedDeliveryDate.Should().Be(DateTime.UtcNow.Date.AddDays(9));
        stored.LeadTimeCalculatedAt.Should().NotBeNull();
        stored.ManualDeliveryDate.Should().BeNull("the manual date is untouched");
    }

    [Fact]
    public async Task A_line_with_no_route_of_its_own_is_calculated_on_the_org_default_of_the_orders_class()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var lineUuid = (await h.ReadAsync(so)).Lines.Single().UUID;

        await h.Service.CalculateLineLeadTimeAsync(so, lineUuid, User);

        h.LeadTimes.Calls.Single().Request.RouteUuid.Should().Be(h.PickAndShip.Uuid);
    }

    [Fact]
    public async Task Only_a_draft_orders_line_lead_time_is_calculated()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var lineUuid = (await h.ReadAsync(so)).Lines.Single().UUID;
        await SetStatusAsync(h, so, "CONFIRMED");

        var refused = await FluentActions.Awaiting(() => h.Service.CalculateLineLeadTimeAsync(so, lineUuid, User))
            .Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().Be("Sale order SO-2026-00001 is CONFIRMED: line lead times can only be calculated on a draft.");
        h.LeadTimes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_order_or_an_unknown_line_is_not_found()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var lineUuid = (await h.ReadAsync(so)).Lines.Single().UUID;
        var other = A34SoHarness.Create(dbName: h.DbName);

        (await other.Service.CalculateLineLeadTimeAsync(so, lineUuid, User)).Should().BeNull();
        (await h.Service.CalculateLineLeadTimeAsync(so, Guid.NewGuid(), User)).Should().BeNull();
        (await other.Service.UpdateLineDeliveryDateAsync(so, lineUuid, new DateTime(2026, 11, 1), User)).Should().BeNull();
    }

    [Fact]
    public async Task Without_the_Inventory_module_there_is_no_calculation()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var lineUuid = (await h.ReadAsync(so)).Lines.Single().UUID;
        h.Tenants.Disable(h.OrgId, A34.Inventory);

        var refused = await FluentActions.Awaiting(() => h.Service.CalculateLineLeadTimeAsync(so, lineUuid, User))
            .Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().Be("Lead-time calculation needs the Inventory module.");
    }

    // ── sale order line: PUT …/delivery-date ───────────────────────────────

    [Fact]
    public async Task T_C4_06_07_the_manual_date_is_set_date_only_and_cleared_back_to_the_calculated_one()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var line = (await h.ReadAsync(so)).Lines.Single();
        var stored = await h.Db.SaleOrderLines.SingleAsync(l => l.UUID == line.UUID);
        stored.CalculatedDeliveryDate = new DateTime(2026, 11, 3);
        await h.Db.SaveChangesAsync();

        var set = (await h.Service.UpdateLineDeliveryDateAsync(so, line.UUID, new DateTime(2026, 11, 20, 15, 0, 0), User))!;
        set.Line.ManualDeliveryDate.Should().Be(new DateTime(2026, 11, 20));
        set.Line.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 11, 20));
        set.Line.DeliveryDateSource.Should().Be("MANUAL");
        set.ProductionNotRescheduled.Should().BeFalse();
        set.Warning.Should().BeNull();

        var cleared = (await h.Service.UpdateLineDeliveryDateAsync(so, line.UUID, null, User))!;
        cleared.Line.ManualDeliveryDate.Should().BeNull();
        cleared.Line.EffectiveDeliveryDate.Should().Be(new DateTime(2026, 11, 3));
        cleared.Line.DeliveryDateSource.Should().Be("CALCULATED");
        (await h.ReadAsync(so)).Lines.Single().UnitPrice.Should().Be(line.UnitPrice, "no re-pricing");
    }

    [Fact]
    public async Task D_16_on_a_confirmed_order_the_date_changes_but_an_existing_production_order_is_not_rescheduled()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null));
        var line = (await h.ReadAsync(so)).Lines.Single();
        await SetStatusAsync(h, so, "CONFIRMED");
        var po = h.Production.Add(h.OrgId, so, line.UUID, "PLANNED", 10m, h.MfgShip.Uuid);

        var result = (await h.Service.UpdateLineDeliveryDateAsync(so, line.UUID, new DateTime(2026, 12, 24), User))!;

        result.Line.ManualDeliveryDate.Should().Be(new DateTime(2026, 12, 24));
        result.ProductionNotRescheduled.Should().BeTrue();
        result.Warning.Should().Be($"{po.Number} was planned for the earlier date and is not rescheduled.");
        (await h.ReadAsync(so)).Lines.Single().ManualDeliveryDate.Should().Be(new DateTime(2026, 12, 24));
    }

    [Fact]
    public async Task A_cancelled_order_or_line_keeps_its_dates()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null), (Guid.NewGuid(), null));
        var lines = (await h.ReadAsync(so)).Lines.OrderBy(l => l.Id).ToList();
        await SetStatusAsync(h, so, "CONFIRMED");
        var cancelledLine = await h.Db.SaleOrderLines.SingleAsync(l => l.UUID == lines[1].UUID);
        cancelledLine.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => h.Service.UpdateLineDeliveryDateAsync(so, lines[1].UUID, new DateTime(2026, 12, 1), User))
            .Should().ThrowAsync<BadRequestException>();

        await SetStatusAsync(h, so, "CANCELLED");
        var refused = await FluentActions.Awaiting(() => h.Service.UpdateLineDeliveryDateAsync(so, lines[0].UUID, new DateTime(2026, 12, 1), User))
            .Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().Be("Sale order SO-2026-00001 is CANCELLED: its delivery dates can no longer change.");
    }

    // ── D-24: the new sale order actions are gated as the contract says ────

    [Theory]
    [InlineData(nameof(SMS.Modules.Demand.Controllers.SaleOrdersController.CalculateLineLeadTime), "{uuid:guid}/lines/{lineUuid:guid}/lead-time", new[] { "SALE_ORDER_EDIT" })]
    [InlineData(nameof(SMS.Modules.Demand.Controllers.SaleOrdersController.UpdateLineDeliveryDate), "{uuid:guid}/lines/{lineUuid:guid}/delivery-date", new[] { "SALE_ORDER_EDIT" })]
    [InlineData(nameof(SMS.Modules.Demand.Controllers.SaleOrdersController.CreateProductionOrders), "{uuid:guid}/create-production-orders", new[] { "SALE_ORDER_CONFIRM", "PROD_CREATE" })]
    public void The_new_sale_order_actions_are_gated_and_routed_per_the_contract(string action, string template, string[] codes)
    {
        var method = typeof(SMS.Modules.Demand.Controllers.SaleOrdersController).GetMethod(action)!;
        var gate = method.GetCustomAttributes(typeof(SMS.Shared.Authorization.RequirePermissionAttribute), false)
            .Cast<SMS.Shared.Authorization.RequirePermissionAttribute>().Should().ContainSingle().Subject;
        gate.AnyOf.Should().Equal(codes);
        method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Single().Template.Should().Be(template);
    }

    // ── inquiry and quotation lines ────────────────────────────────────────

    private sealed record LineHarness(DemandDbContext Db, SalesLineLeadTimeService Service, FakeLeadTimes LeadTimes, A34Tenants Tenants, Guid OrgId);

    private static LineHarness NewLines()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync(new List<UserIdentity>());
        var partners = new Mock<IPartnerRoleLookup>();
        var variants = new Mock<IProductVariantResolver>();
        variants.Setup(v => v.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, VariantDescription>());
        var inquiries = new SaleInquiryService(db, tenant, Mock.Of<IDocumentNumberGenerator>(), partners.Object, variants.Object, users.Object);
        partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new PartnerRoleInfo(id, "Acme", true, false, true));
        var quotations = new SaleQuotationService(db, tenant, Mock.Of<IDocumentNumberGenerator>(), partners.Object,
            Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IPricingService>(), inquiries, Mock.Of<ISaleOrderService>(), variants: variants.Object);
        var leadTimes = new FakeLeadTimes();
        var tenants = new A34Tenants();
        tenants.Enable(tenant.OrganizationId, A34.Demand, A34.Inventory);
        return new LineHarness(db, new SalesLineLeadTimeService(db, tenant, inquiries, quotations, leadTimes, tenants), leadTimes, tenants, tenant.OrganizationId);
    }

    private static async Task<(Guid Inquiry, Guid Line)> InquiryAsync(LineHarness h, string status = "UNDER_REVIEW", Guid? variant = null, bool freeText = false)
    {
        var inquiry = new SaleInquiry
        {
            InquiryNumber = "INQ-2026-00001", PartnerId = Guid.NewGuid(), ReceivedDate = new DateTime(2026, 10, 1), Status = status, CreatedBy = User,
            Lines =
            {
                new SaleInquiryLine
                {
                    LineNumber = 1, VariantUuid = freeText ? null : variant ?? Guid.NewGuid(), ProductDescription = "Widget",
                    RequestedQuantity = 25m, RequestedDeliveryDate = new DateTime(2026, 11, 30), EstimatedDeliveryDate = new DateTime(2026, 11, 28)
                }
            }
        };
        h.Db.SaleInquiries.Add(inquiry);
        await h.Db.SaveChangesAsync();
        return (inquiry.UUID, inquiry.Lines.Single().UUID);
    }

    [Fact]
    public async Task An_inquiry_line_is_calculated_on_the_variants_route_with_its_requested_date()
    {
        var h = NewLines();
        var variant = Guid.NewGuid();
        var (uuid, line) = await InquiryAsync(h, variant: variant);
        h.LeadTimes.Days = 4;

        var result = (await h.Service.CalculateInquiryLineAsync(uuid, line, User))!;

        var call = h.LeadTimes.Calls.Should().ContainSingle().Subject;
        call.Org.Should().Be(h.OrgId);
        call.Request.Should().Be(new LeadTimeRequest(variant, 25m, null, new DateTime(2026, 11, 30)));
        result.Line.CalculatedLeadTimeDays.Should().Be(4);
        result.Line.CalculatedDeliveryDate.Should().Be(DateTime.UtcNow.Date.AddDays(4));
        result.Line.EstimatedDeliveryDate.Should().Be(new DateTime(2026, 11, 28), "the manual date is untouched");
        result.Line.DeliveryDateSource.Should().Be("MANUAL");
        result.LeadTime.TotalLeadTimeDays.Should().Be(4);
        (await h.Db.SaleInquiryLines.AsNoTracking().SingleAsync()).CalculatedLeadTimeDays.Should().Be(4);
    }

    [Fact]
    public async Task A_read_only_inquiry_or_a_free_text_line_is_refused_and_another_orgs_is_not_found()
    {
        var h = NewLines();
        var (quoted, quotedLine) = await InquiryAsync(h, status: "QUOTED");
        await FluentActions.Awaiting(() => h.Service.CalculateInquiryLineAsync(quoted, quotedLine, User)).Should().ThrowAsync<BadRequestException>();

        var h2 = NewLines();
        var (open, freeLine) = await InquiryAsync(h2, freeText: true);
        await FluentActions.Awaiting(() => h2.Service.CalculateInquiryLineAsync(open, freeLine, User)).Should().ThrowAsync<BadRequestException>();
        (await h2.Service.CalculateInquiryLineAsync(Guid.NewGuid(), freeLine, User)).Should().BeNull();
        (await h2.Service.CalculateInquiryLineAsync(open, Guid.NewGuid(), User)).Should().BeNull();
        h.LeadTimes.Calls.Should().BeEmpty();
        h2.LeadTimes.Calls.Should().BeEmpty();
    }

    private static async Task<(Guid Quotation, Guid Line)> QuotationAsync(LineHarness h, string status = "DRAFT", Guid? variant = null)
    {
        var quotation = new SaleQuotation
        {
            QuotationNumber = "SQ-2026-00001", PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(), Status = status,
            ValidFrom = new DateTime(2026, 10, 1), ValidTo = new DateTime(2026, 10, 31), CreatedBy = User,
            Lines = { new SaleQuotationLine { LineNumber = 1, VariantUuid = variant ?? Guid.NewGuid(), ProductDescription = "Widget", Quantity = 12m, UnitPrice = 5m } }
        };
        h.Db.SaleQuotations.Add(quotation);
        await h.Db.SaveChangesAsync();
        return (quotation.UUID, quotation.Lines.Single().UUID);
    }

    [Fact]
    public async Task A_draft_quotation_line_is_calculated_with_no_requested_date_and_a_sent_one_is_refused()
    {
        var h = NewLines();
        var variant = Guid.NewGuid();
        var (uuid, line) = await QuotationAsync(h, variant: variant);
        h.LeadTimes.Days = 6;

        var result = (await h.Service.CalculateQuotationLineAsync(uuid, line, User))!;

        h.LeadTimes.Calls.Single().Request.Should().Be(new LeadTimeRequest(variant, 12m, null, null));
        result.Line.CalculatedLeadTimeDays.Should().Be(6);
        result.Line.EffectiveDeliveryDate.Should().Be(DateTime.UtcNow.Date.AddDays(6));
        result.Line.DeliveryDateSource.Should().Be("CALCULATED");

        var (sent, sentLine) = await QuotationAsync(h, status: "SENT");
        var refused = await FluentActions.Awaiting(() => h.Service.CalculateQuotationLineAsync(sent, sentLine, User))
            .Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().Be("Quotation SQ-2026-00001 is SENT: line lead times can only be calculated on a draft.");
    }

    [Fact]
    public async Task Inquiry_and_quotation_lines_need_the_Inventory_module_too()
    {
        var h = NewLines();
        var (uuid, line) = await QuotationAsync(h);
        h.Tenants.Disable(h.OrgId, A34.Inventory);

        var refused = await FluentActions.Awaiting(() => h.Service.CalculateQuotationLineAsync(uuid, line, User))
            .Should().ThrowAsync<BadRequestException>();
        refused.Which.Message.Should().Be("Lead-time calculation needs the Inventory module.");
    }
}
