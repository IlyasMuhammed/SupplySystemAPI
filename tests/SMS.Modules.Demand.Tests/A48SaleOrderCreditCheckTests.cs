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
/// A48 AR (tasks 118–125, D-5, API-CONTRACT §2) — the credit check inside sale-order confirmation: Off takes no lock and
/// changes nothing; a Block refusal is a 409 that leaves the draft untouched; CREDIT_OVERRIDE + a reason confirms and records
/// the override; Warn confirms with a warning; the customer lock is held across the confirm.
/// </summary>
public class A48SaleOrderCreditCheckTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(DemandDbContext Db, SaleOrderService Service, Mock<ICreditCheck> Credit, List<string> Trace);

    private static CreditCheckResult Result(string mode, bool approved, string? reason = null, string? warning = null) =>
        new(mode, approved, reason, warning, false, 1_000m, 0m, 900m, 900m, 200m, 1_100m, 100m, "PKR");

    private static Harness NewHarness(CreditCheckResult result)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync("SO-2026-00001");
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 200m, Currency, PriceResolutionTier.DefaultSelling, null));
        var availability = new Mock<IAvailabilityCheckService>();
        availability.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());

        var trace = new List<string>();
        var credit = new Mock<ICreditCheck>();
        credit.Setup(c => c.CheckAsync(It.IsAny<CreditCheckRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreditCheckRequest, CancellationToken>((r, _) => trace.Add($"check:{r.AmountBase}"))
            .ReturnsAsync(result);
        credit.Setup(c => c.GetModeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => trace.Add("mode")).ReturnsAsync(result.Mode);
        var released = new Mock<IAsyncDisposable>();
        released.Setup(d => d.DisposeAsync()).Callback(() => trace.Add("unlock")).Returns(ValueTask.CompletedTask);
        credit.Setup(c => c.LockCustomerAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => trace.Add("lock")).ReturnsAsync(released.Object);

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, Mock.Of<IStockReservationService>(), Mock.Of<ITimelineService>(),
            Mock.Of<IBackgroundJobClient>(), availability.Object, Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            creditCheck: credit.Object);
        return new Harness(db, service, credit, trace);
    }

    private static Task<Guid> DraftAsync(Harness h) => h.Service.CreateAsync(new CreateSaleOrderRequest
    {
        PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
        Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 1 }]
    }, User);

    [Fact]
    public async Task Mode_off_confirms_as_before_without_a_lock()
    {
        var h = NewHarness(Result(CreditControlModes.Off, true));
        var so = await DraftAsync(h);

        var result = await h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext(null, false));

        result!.Status.Should().Be("CONFIRMED");
        result.CreditCheck.Should().BeNull();
        h.Trace.Should().Equal("mode");
        h.Credit.Verify(c => c.LockCustomerAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TS_CR_003_a_block_refusal_is_a_409_and_the_draft_is_untouched()
    {
        var h = NewHarness(Result(CreditControlModes.Block, false, "Exceeds the credit limit of 1,000.00 PKR by 100.00 PKR (exposure 900.00, this order 200.00)."));
        var so = await DraftAsync(h);

        await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext(null, false)))
            .Should().ThrowAsync<CreditCheckBlockedException>()
            .WithMessage("Credit check failed: Exceeds the credit limit of 1,000.00 PKR by 100.00 PKR (exposure 900.00, this order 200.00). " +
                         "Confirming needs the CREDIT_OVERRIDE permission and an override reason.")
            .Where(e => e.Result.EffectiveLimit == 1_000m && e.Result.ProjectedExposure == 1_100m && e.Result.AvailableCredit == 100m);

        (await h.Service.GetByIdAsync(so))!.Status.Should().Be("DRAFT");
        h.Trace.Should().Equal("mode", "lock", "check:200", "unlock");
        h.Credit.Verify(c => c.RecordOverrideAsync(It.IsAny<CreditCheckRequest>(), It.IsAny<CreditCheckResult>(), It.IsAny<string>(),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_reason_without_the_permission_is_still_refused()
    {
        var h = NewHarness(Result(CreditControlModes.Block, false, "On hold."));
        var so = await DraftAsync(h);
        await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext("Director said so", false)))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Override_with_permission_and_reason_confirms_and_records_it()
    {
        var h = NewHarness(Result(CreditControlModes.Block, false, "Acme is on credit hold: Dispute."));
        var so = await DraftAsync(h);

        var result = await h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext("Director approved", true, "boss@x.test"));

        result!.Status.Should().Be("CONFIRMED");
        result.CreditWarning.Should().Be("Confirmed over the credit check: Acme is on credit hold: Dispute.");
        result.CreditCheck!.Approved.Should().BeFalse();
        h.Credit.Verify(c => c.RecordOverrideAsync(
            It.Is<CreditCheckRequest>(r => r.DocumentUuid == so && r.DocumentType == "SALE_ORDER" && r.AmountBase == 200m),
            It.IsAny<CreditCheckResult>(), "Director approved", User, "boss@x.test", It.IsAny<CancellationToken>()), Times.Once);
        h.Trace.Should().Equal("mode", "lock", "check:200", "unlock");
    }

    [Fact]
    public async Task Warn_mode_confirms_with_the_warning()
    {
        var h = NewHarness(Result(CreditControlModes.Warn, true, warning: "Exceeds the credit limit of 1,000.00 by 100.00 (exposure 900.00, this order 200.00)."));
        var so = await DraftAsync(h);

        var result = await h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext(null, false));

        result!.Status.Should().Be("CONFIRMED");
        result.CreditWarning.Should().StartWith("Exceeds the credit limit");
        result.CreditCheck!.Mode.Should().Be("Warn");
    }

    [Fact]
    public async Task FE3_the_controller_409_carries_the_credit_check_as_result_creditCheck()
    {
        var check = Result(CreditControlModes.Block, false, "Acme is on credit hold: Dispute.") with { OnHold = true };
        var service = new Mock<ISaleOrderService>();
        service.Setup(s => s.ConfirmWithResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<SaleOrderCreditContext?>()))
            .ThrowsAsync(new CreditCheckBlockedException("Credit check failed: Acme is on credit hold: Dispute. Confirming needs …", check));
        var controller = new SMS.Modules.Demand.Controllers.SaleOrdersController(service.Object, Mock.Of<ISaleOrderReservationService>())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        var response = await controller.Confirm(Guid.NewGuid(), new ConfirmSaleOrderRequest());

        var conflict = response.Should().BeOfType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>().Subject;
        var body = conflict.Value.Should().BeOfType<SMS.Shared.Pagination.ApiResponse>().Subject;
        body.Success.Should().BeFalse();
        body.Message.Should().StartWith("Credit check failed:");
        var result = body.Result.Should().BeOfType<SaleOrderCreditBlockedModel>().Subject;
        result.CreditCheck.Should().BeSameAs(check);
        result.CreditCheck.OnHold.Should().BeTrue();
    }

    [Fact]
    public async Task REV11_a_mode_read_failure_confirms_as_Off_without_a_lock()
    {
        var h = NewHarness(Result(CreditControlModes.Block, false, "On hold."));
        h.Credit.Setup(c => c.GetModeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid column name CreditControlMode"));
        var so = await DraftAsync(h);

        var result = await h.Service.ConfirmWithResultAsync(so, User, new SaleOrderCreditContext(null, false));

        result!.Status.Should().Be("CONFIRMED");
        h.Credit.Verify(c => c.LockCustomerAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Credit.Verify(c => c.CheckAsync(It.IsAny<CreditCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_two_argument_confirm_still_checks_credit()
    {
        var h = NewHarness(Result(CreditControlModes.Block, false, "On hold."));
        var so = await DraftAsync(h);
        await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, User)).Should().ThrowAsync<ConflictException>();
    }
}
