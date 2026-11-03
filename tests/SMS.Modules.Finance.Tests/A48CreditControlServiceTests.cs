using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — credit control over the real documents (TS-CR-003/004/007..010, BR-CR-005/006, tasks 117, 120, 126).</summary>
public class A48CreditControlServiceTests
{
    private static CreditCheckRequest Order(A48ArWorld w, Guid customer, decimal amount) =>
        new(w.Org, customer, amount, null, amount, CreditControlService.DocSaleOrder, Guid.NewGuid(), "SO-TEST");

    [Fact]
    public async Task Mode_off_approves_without_reading_anything_or_creating_a_profile()
    {
        var w = new A48ArWorld();
        var c = w.Customer(limit: 10m);
        await w.Desk.InvoiceAsync(c, 500m);

        var r = await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1_000m)));

        r.Mode.Should().Be(CreditControlModes.Off);
        r.Approved.Should().BeTrue();
        r.Exposure.Should().Be(0m);
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task TS_CR_009_exposure_is_open_orders_plus_open_invoices_less_unapplied_credit()
    {
        var w = new A48ArWorld();
        var c = w.Customer(limit: 10_000m);
        await w.Desk.InvoiceAsync(c, 500m);                                       // invoiced, open 500
        await w.Desk.PlaceOrderAsync(c, new ReceivablesDesk.OrderLine(2m, 400m));  // FULFILLED, not invoiced: 800 open order

        var e = await w.InScopeAsync(s => s.Credit.GetExposureAsync(c));
        e.OpenOrdersAmount.Should().Be(800m);
        e.OpenInvoicesAmount.Should().Be(500m);
        e.TotalExposure.Should().Be(1_300m);
        e.AvailableCredit.Should().Be(8_700m);

        await w.Desk.PayAsync(c, 650m);                                           // 500 settles the invoice, 150 on account
        e = await w.InScopeAsync(s => s.Credit.GetExposureAsync(c));
        e.OpenInvoicesAmount.Should().Be(0m, "unapplied credit reduces the invoice part, never below zero");
        e.TotalExposure.Should().Be(800m);
    }

    [Fact]
    public async Task TS_CR_003_block_mode_refuses_an_order_over_the_limit()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer("Acme Ltd", limit: 1_000m);
        await w.Desk.InvoiceAsync(c, 900m);

        var r = await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 200m)));

        r.Approved.Should().BeFalse();
        r.BlockReason.Should().Be("Exceeds the credit limit of 1,000.00 by 100.00 (exposure 900.00, this order 200.00).");
        r.ProjectedExposure.Should().Be(1_100m);

        (await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 100m)))).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Warn_mode_passes_over_the_limit_with_a_warning()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Warn);
        var c = w.Customer(limit: 100m);

        var r = await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 150m)));
        r.Approved.Should().BeTrue();
        r.Warning.Should().StartWith("Exceeds the credit limit of 100.00");
    }

    [Fact]
    public async Task Task126_first_check_creates_the_default_profile_and_allows()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer();

        (await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1m)))).Approved.Should().BeTrue();
        var profile = await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync());
        profile.PartnerUuid.Should().Be(c);
        profile.RiskTier.Should().Be("Medium");
        profile.CreditHold.Should().BeFalse();
    }

    [Fact]
    public async Task TS_CR_004_and_007_hold_blocks_any_order_and_release_unblocks_with_an_ordered_audit_trail()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer("Acme Ltd");

        await w.InScopeAsync(s => s.Credit.PlaceHoldAsync(c, "Bounced cheques", 42, "Tester"));
        var blocked = await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1m)));
        blocked.Approved.Should().BeFalse();
        blocked.OnHold.Should().BeTrue();
        blocked.BlockReason.Should().Be("Acme Ltd is on credit hold: Bounced cheques.");

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.PlaceHoldAsync(c, "again", 42, "Tester")))
            .Should().ThrowAsync<ConflictException>().WithMessage("Acme Ltd is already on credit hold.");

        await w.InScopeAsync(s => s.Credit.ReleaseHoldAsync(c, "Paid up", 42, "Tester"));
        (await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1m)))).Approved.Should().BeTrue();
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.ReleaseHoldAsync(c, "again", 42, "Tester")))
            .Should().ThrowAsync<ConflictException>().WithMessage("Acme Ltd is not on credit hold.");

        var history = await w.InScopeAsync(s => s.Credit.HistoryAsync(c, 1, 25));
        history.Data.Select(h => h.Action).Should().Equal("Release", "Hold");   // newest first
        history.Data.Should().OnlyContain(h => h.RequestedByName == "Tester");
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.PlaceHoldAsync(c, " ", 1, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A reason is required.");
    }

    [Fact]
    public async Task TS_CR_010_risk_tier_is_persisted_and_audited()
    {
        var w = new A48ArWorld();
        var c = w.Customer();

        var e = await w.InScopeAsync(s => s.Credit.SetRiskTierAsync(c, "high", "Late payer", 42, "Tester"));
        e.RiskTier.Should().Be("High");
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync())).RiskTier.Should().Be("High");
        (await w.InScopeAsync(s => s.Credit.HistoryAsync(c, 1, 25))).Data.Single().Should()
            .Match<CreditHistoryModel>(h => h.Action == "RiskTier" && h.PreviousValue == "Medium" && h.NewValue == "High");

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.SetRiskTierAsync(c, "Extreme", "x", 1, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown riskTier 'Extreme'. Allowed: Low, Medium, High.");
    }

    [Fact]
    public async Task Temporary_limit_rules_and_BR_CR_005_expiry()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var none = w.Customer("No Limit Co");
        var c = w.Customer("Acme Ltd", limit: 1_000m);
        var today = DateOnly.FromDateTime(w.Clock.Value);

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.SetTemporaryLimitAsync(none, 5_000m, today.AddDays(5), "x", 1, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("No Limit Co has no permanent credit limit; set one on the customer before granting a temporary one.");
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.SetTemporaryLimitAsync(c, 900m, today.AddDays(5), "x", 1, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The temporary limit must be above the permanent limit (1,000.00).");
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Credit.SetTemporaryLimitAsync(c, 2_000m, today, "x", 1, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The expiry date must be after today.");

        var e = await w.InScopeAsync(s => s.Credit.SetTemporaryLimitAsync(c, 2_000m, today.AddDays(5), "Season", 1, "Tester"));
        e.EffectiveCreditLimit.Should().Be(2_000m);
        (await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1_500m)))).Approved.Should().BeTrue();   // TS-CR-005

        w.Clock.Value = w.Clock.Value.AddDays(6);
        (await w.InScopeAsync(s => s.Credit.CheckAsync(Order(w, c, 1_500m)))).Approved.Should().BeFalse();  // TS-CR-006

        e = await w.InScopeAsync(s => s.Credit.GetExposureAsync(c));
        e.TemporaryCreditLimit.Should().BeNull("an expired temporary limit is cleared on read (BR-CR-005)");
        (await w.InScopeAsync(s => s.Credit.HistoryAsync(c, 1, 25))).Data.Select(h => h.Action).Should().Equal("TempExpired", "TempIncrease");
    }

    [Fact]
    public async Task Task120_a_sales_invoice_moves_value_from_orders_to_invoices_and_only_warns()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer(limit: 1_000m);
        var order = await w.Desk.PlaceOrderAsync(c, new ReceivablesDesk.OrderLine(1m, 900m));   // 900 open order
        var delivery = w.Desk.Deliver(order, "DELIVERED", (0, 1m));

        var ok = await w.InScopeAsync(s => s.Invoices.CreateFromFulfillmentAsync(delivery, 42));
        ok.CreditWarning.Should().BeNull("the invoice bills the order already counted, so exposure does not grow");

        await w.InScopeAsync(s => s.Credit.PlaceHoldAsync(c, "Dispute", 42, null));
        var order2 = await w.Desk.PlaceOrderAsync(c, new ReceivablesDesk.OrderLine(1m, 50m));
        var held = await w.InScopeAsync(s => s.Invoices.CreateFromFulfillmentAsync(w.Desk.Deliver(order2, "DELIVERED", (0, 1m)), 42));
        held.InvoiceUuid.Should().NotBeEmpty("a warning never refuses the invoice");
        held.CreditWarning.Should().Contain("on credit hold: Dispute.");
    }

    [Fact]
    public async Task Override_is_recorded_with_its_document()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer(limit: 100m);
        var request = Order(w, c, 500m);
        var result = await w.InScopeAsync(s => s.Credit.CheckAsync(request));

        await w.InScopeAsync(async s => { await s.Credit.RecordOverrideAsync(request, result, "Director approved", 42, "Tester"); return 0; });

        var row = await w.InScopeAsync(s => s.Db.CreditHoldRequests.SingleAsync());
        row.Action.Should().Be("Override");
        row.Amount.Should().Be(500m);
        row.SourceDocumentType.Should().Be("SALE_ORDER");
        row.SourceDocumentNumber.Should().Be("SO-TEST");
        row.Reason.Should().Contain("Director approved");
    }

    [Fact]
    public async Task TS_CR_008_concurrent_check_and_confirm_on_one_customer_cannot_both_pass_on_the_same_headroom()
    {
        var w = new A48ArWorld();
        await w.SetModeAsync(CreditControlModes.Block);
        var c = w.Customer(limit: 1_000m);

        async Task<bool> ConfirmAsync()
        {
            await using var s = w.Scope();
            await using var _ = await s.Credit.LockCustomerAsync(w.Org, c);
            var r = await s.Credit.CheckAsync(Order(w, c, 600m));
            if (!r.Approved) return false;
            await Task.Delay(20);                                                 // the confirm's own work
            await w.Desk.PlaceOrderAsync(c, new ReceivablesDesk.OrderLine(1m, 600m));   // the committed order
            return true;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(ConfirmAsync)));
        results.Count(r => r).Should().Be(1);
        (await w.InScopeAsync(s => s.Credit.GetExposureAsync(c))).TotalExposure.Should().Be(600m);
    }

    [Fact]
    public async Task REV14_billed_lines_are_not_counted_twice_and_cancelled_lines_not_at_all()
    {
        var w = new A48ArWorld();
        var c = w.Customer(limit: 10_000m);
        var order = await w.Desk.PlaceOrderAsync(c, new ReceivablesDesk.OrderLine(1m, 500m), new ReceivablesDesk.OrderLine(1m, 300m));
        await w.Desk.IssueDeliveryAsync(w.Desk.Deliver(order, "DELIVERED", (0, 1m)));

        // Demand's best-effort InvoicedQty counter failed after the issue (REV-14), and line 2 was cancelled.
        await using (var s = w.Scope())
        {
            var lines = await s.Demand.SaleOrderLines.Where(l => order.LineUuids.Contains(l.UUID)).ToListAsync();
            foreach (var l in lines) l.InvoicedQty = 0m;
            lines.Single(l => l.UUID == order.LineUuids[1]).Status = "CANCELLED";
            await s.Demand.SaveChangesAsync();
        }

        var e = await w.InScopeAsync(s => s.Credit.GetExposureAsync(c));
        e.OpenOrdersAmount.Should().Be(0m);
        e.OpenInvoicesAmount.Should().Be(500m);
    }

    [Fact]
    public async Task Exposure_list_filters_over_limit_and_on_hold()
    {
        var w = new A48ArWorld();
        var a = w.Customer("Alpha", limit: 100m);
        var b = w.Customer("Beta", limit: 10_000m);
        await w.Desk.InvoiceAsync(a, 500m);
        await w.Desk.InvoiceAsync(b, 500m);
        await w.InScopeAsync(s => s.Credit.PlaceHoldAsync(b, "x", 1, null));

        (await w.InScopeAsync(s => s.Credit.ListExposuresAsync(new CreditExposureFilter { OverLimit = true })))
            .Data.Select(x => x.CustomerName).Should().Equal("Alpha");
        (await w.InScopeAsync(s => s.Credit.ListExposuresAsync(new CreditExposureFilter { OnHold = true })))
            .Data.Select(x => x.CustomerName).Should().Equal("Beta");
    }
}
