using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A sale order line can only be for a variant whose "Available For Retail" checkbox is checked —
/// the write-side enforcement behind the picker's own RETAIL channel filter, since the picker is
/// only ever a suggestion until the server refuses anything that slips past it.
/// </summary>
public class SaleOrderRetailAvailabilityTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(SaleOrderService Service, Mock<IVariantAvailabilityService> Availability, Mock<IPricingService> Pricing);

    private static Harness NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SO-2026-00001");
        var pricing = new Mock<IPricingService>();
        var availability = new Mock<IVariantAvailabilityService>();

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(),
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            variants: null, availability: availability.Object);

        return new Harness(service, availability, pricing);
    }

    private static void SetupPrice(Mock<IPricingService> pricing, Guid variantUuid) =>
        pricing.Setup(p => p.ResolveSalePriceAsync(variantUuid, It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 100m, Currency, PriceResolutionTier.DefaultSelling, null));

    private static CreateSaleOrderRequest Order(Guid variantUuid) => new()
    {
        PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
        Lines = [new CreateSaleOrderLineRequest { VariantUuid = variantUuid, Quantity = 1 }]
    };

    [Fact]
    public async Task ALineForARetailEligibleVariant_IsAccepted()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant);
        h.Availability.Setup(a => a.GetAvailabilityAsync(variant))
            .ReturnsAsync(new VariantChannelAvailability("Widget (SKU-1)", true, false, false, false, false));

        var uuid = await h.Service.CreateAsync(Order(variant), User);

        uuid.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task ALineForAVariantNotCheckedForRetail_IsRefused_NamingTheVariant()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant);
        // Checked for MIR/MIV only — not retail.
        h.Availability.Setup(a => a.GetAvailabilityAsync(variant))
            .ReturnsAsync(new VariantChannelAvailability("Bulk Cement (CEM-50)", false, false, true, false, false));

        var act = () => h.Service.CreateAsync(Order(variant), User);

        var thrown = await act.Should().ThrowAsync<BadRequestException>();
        thrown.Which.Message.Should().Contain("Bulk Cement (CEM-50)").And.Contain("not available for retail sale");
    }

    [Fact]
    public async Task ALineForAVariantWithNoChannelsChecked_IsRefused()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant);
        h.Availability.Setup(a => a.GetAvailabilityAsync(variant))
            .ReturnsAsync(new VariantChannelAvailability("New Item (NEW-1)", false, false, false, false, false));

        var act = () => h.Service.CreateAsync(Order(variant), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task AVariantTheAvailabilityServiceDoesNotKnow_IsRefused()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant);
        h.Availability.Setup(a => a.GetAvailabilityAsync(variant)).ReturnsAsync((VariantChannelAvailability?)null);

        var act = () => h.Service.CreateAsync(Order(variant), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TheCheckHappensBeforePricingIsResolved()
    {
        // Proves the refusal is really about availability, not a side effect of pricing being asked
        // for an ineligible variant — SetupPrice is deliberately never called here.
        var h = NewHarness();
        var variant = Guid.NewGuid();
        h.Availability.Setup(a => a.GetAvailabilityAsync(variant))
            .ReturnsAsync(new VariantChannelAvailability("X", false, false, false, false, false));

        var act = () => h.Service.CreateAsync(Order(variant), User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("not available for retail sale");
        h.Pricing.Verify(p => p.ResolveSalePriceAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
