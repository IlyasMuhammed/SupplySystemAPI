using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — credit control end to end (C2, D-5; TS-CR-001..010, BR-CR-001..007, task 583). Every test gets its own
/// organization (Finance on, item at 100 with no tax, so an order of q units is worth 100·q) and switches
/// <c>CreditControlMode</c> on itself — it is Off by default (D-5). Limits are the partner master's <c>CreditLimit</c> (D-4).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArCreditControlE2ETests</c>.
/// </summary>
public sealed class ArCreditControlE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArCreditControlE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48CR");
    }

    private async Task<ArOrg> BlockOrgAsync(string prefix, string mode = "Block")
    {
        var o = await _root.NewArOrgAsync(prefix, stock: 600m);
        await o.CreditModeAsync(mode);
        return o;
    }

    private const string Failed = "Credit check failed:";

    [Fact]
    public async Task Settings_default_to_Off()
    {
        var o = await _root.NewArOrgAsync("CR0", stock: 10m);
        var s = await o.K.Ok(o.K.Get("/api/finance/ar-ap/settings"), "settings");
        s.S("creditControlMode").Should().Be("Off", "D-5: credit control is opt-in");
        (await o.PutSettings("Sometimes")).Status.Should().Be(HttpStatusCode.BadRequest, "an unknown mode is refused");
    }

    [Fact]
    public async Task TS_CR_001_no_limit_confirms()
    {
        var o = await BlockOrgAsync("CR1");
        await o.SetCreditLimitAsync(o.Customer, null);
        var so = await o.DraftSoAsync(o.Customer, 50m);
        var r = await o.TryConfirm(so);
        r.Status.Should().Be(HttpStatusCode.OK, $"no limit means unlimited — {r}");
        (await o.SoStatusAsync(so)).Should().Be("CONFIRMED");
        (await o.ExposureAsync(o.Customer.Uuid)).IsNull("effectiveCreditLimit").Should().BeTrue();

        await o.SetCreditLimitAsync(o.Customer, 0m); // D-CORE-3: 0 = no limit too
        var so2 = await o.DraftSoAsync(o.Customer, 50m);
        (await o.TryConfirm(so2)).Status.Should().Be(HttpStatusCode.OK, "a limit of 0 is no limit (POS semantics)");
    }

    [Fact]
    public async Task TS_CR_002_within_limit_confirms()
    {
        var o = await BlockOrgAsync("CR2");
        await o.SetCreditLimitAsync(o.Customer, 10_000m);
        await o.IssuedInvoiceAsync(o.Customer, 80m); // 8 000 open = 80 %
        var so = await o.DraftSoAsync(o.Customer, 10m);
        var r = await o.TryConfirm(so);
        r.Status.Should().Be(HttpStatusCode.OK, r.ToString());
        r.Result.P("creditCheck").B("approved").Should().BeTrue();
        r.Result.P("creditCheck").D("projectedExposure").Should().Be(9_000m);
    }

    [Fact]
    public async Task TS_CR_003_over_limit_blocks()
    {
        var o = await BlockOrgAsync("CR3");
        await o.SetCreditLimitAsync(o.Customer, 10_000m);
        await o.IssuedInvoiceAsync(o.Customer, 100m); // at 100 %
        var so = await o.DraftSoAsync(o.Customer, 5m);
        var r = await o.TryConfirm(so);
        r.ShouldRefuse(HttpStatusCode.Conflict, Failed, "BR-CR-001: projected exposure above the limit");
        r.ShouldRefuse(HttpStatusCode.Conflict, "Exceeds the credit limit of", "the limit's block reason");
        r.ShouldRefuse(HttpStatusCode.Conflict, "Confirming needs the CREDIT_OVERRIDE permission and an override reason.", "the override hint");
        (await o.SoStatusAsync(so)).Should().Be("DRAFT", "a refused confirm changes nothing");

        var check = await o.K.Ok(o.Check(o.Customer.Uuid, 500m), "credit check endpoint");
        check.B("approved").Should().BeFalse();
        check.D("exposure").Should().Be(10_000m);
        check.D("projectedExposure").Should().Be(10_500m);
        check.S("blockReason").Should().Contain("Exceeds the credit limit of");
    }

    [Fact]
    public async Task TS_CR_004_hold_blocks_regardless_of_amount()
    {
        var o = await BlockOrgAsync("CR4");
        await o.SetCreditLimitAsync(o.Customer, null);
        await o.K.Ok(o.Hold(o.Customer.Uuid, "Cheque bounced"), "place hold");
        (await o.Hold(o.Customer.Uuid, "again")).ShouldRefuse(HttpStatusCode.Conflict, "is already on credit hold.", "a second hold");
        var so = await o.DraftSoAsync(o.Customer, 1m);
        var r = await o.TryConfirm(so);
        r.ShouldRefuse(HttpStatusCode.Conflict, "is on credit hold: Cheque bounced.", "BR-CR-003: a hold blocks every order");
        (await o.ExposureAsync(o.Customer.Uuid)).B("isOnHold").Should().BeTrue();
    }

    [Fact]
    public async Task TS_CR_005_temporary_limit_above_permanent_is_used()
    {
        var o = await BlockOrgAsync("CR5");
        await o.SetCreditLimitAsync(o.Customer, 1_000m);
        (await o.TempLimit(o.Customer.Uuid, 500m, Today.AddDays(7))).ShouldRefuse(HttpStatusCode.BadRequest,
            "The temporary limit must be above the permanent limit", "a temporary limit below the permanent one");
        (await o.TempLimit(o.Customer.Uuid, 5_000m, Today)).ShouldRefuse(HttpStatusCode.BadRequest, "The expiry date must be after today.", "expiry today");
        await o.K.Ok(o.TempLimit(o.Customer.Uuid, 5_000m, Today.AddDays(7)), "temporary limit");

        var e = await o.ExposureAsync(o.Customer.Uuid);
        e.D("effectiveCreditLimit").Should().Be(5_000m, "BR-CR-002: MAX(limit, temporary) while not expired");
        var so = await o.DraftSoAsync(o.Customer, 30m);
        (await o.TryConfirm(so)).Status.Should().Be(HttpStatusCode.OK, "3 000 is within the temporary 5 000");

        var history = await o.CreditHistoryAsync(o.Customer.Uuid);
        history.Should().Contain(h => h.S("action") == "TempIncrease" && h.ND("amount") == 5_000m, "BR-CR-006");
    }

    [Fact]
    public async Task TS_CR_006_expired_temporary_limit_falls_back_to_permanent()
    {
        var o = await BlockOrgAsync("CR6");
        await o.SetCreditLimitAsync(o.Customer, 1_000m);
        await o.K.Ok(o.TempLimit(o.Customer.Uuid, 5_000m, Today.AddDays(7)), "temporary limit");
        // Time passes: the expiry is now yesterday.
        (await _f.ExecuteAsync("UPDATE finance.partner_financial_profiles SET TemporaryCreditExpiry = @d WHERE OrganizationId = @o AND PartnerUuid = @p",
            ("@d", Today.AddDays(-1)), ("@o", o.OrgId), ("@p", o.Customer.Uuid))).Should().Be(1);

        var e = await o.ExposureAsync(o.Customer.Uuid);
        e.D("effectiveCreditLimit").Should().Be(1_000m, "BR-CR-005: an expired temporary limit no longer counts");
        var so = await o.DraftSoAsync(o.Customer, 30m);
        (await o.TryConfirm(so)).ShouldRefuse(HttpStatusCode.Conflict, Failed, "3 000 over the permanent 1 000");
        var p = await o.ProfileAsync(o.Customer.Uuid);
        p.B("temporaryLimitActive").Should().BeFalse();
    }

    [Fact]
    public async Task TS_CR_007_hold_release_then_the_same_order_confirms()
    {
        var o = await BlockOrgAsync("CR7");
        await o.SetCreditLimitAsync(o.Customer, 50_000m);
        var so = await o.DraftSoAsync(o.Customer, 10m);

        await o.K.Ok(o.Hold(o.Customer.Uuid, "Awaiting financials"), "hold");
        (await o.TryConfirm(so)).ShouldRefuse(HttpStatusCode.Conflict, "is on credit hold: Awaiting financials.", "held");
        (await o.Release(o.Customer.Uuid, "")).ShouldRefuse(HttpStatusCode.BadRequest, "A reason is required.", "release without reason");
        await o.K.Ok(o.Release(o.Customer.Uuid, "Financials received"), "release");
        (await o.Release(o.Customer.Uuid, "twice")).ShouldRefuse(HttpStatusCode.Conflict, "is not on credit hold.", "release twice");

        var r = await o.TryConfirm(so);
        r.Status.Should().Be(HttpStatusCode.OK, $"released — {r}");
        (await o.SoStatusAsync(so)).Should().Be("CONFIRMED");

        var h = await o.CreditHistoryAsync(o.Customer.Uuid);
        h.Select(x => x.S("action")).Should().Contain(["Hold", "Release"], "BR-CR-006: every hold/release is audited");
        h.First(x => x.S("action") == "Hold").S("reason").Should().Be("Awaiting financials");
    }

    [Fact]
    public async Task TS_CR_008_concurrent_confirms_cannot_both_pass_the_limit()
    {
        var o = await BlockOrgAsync("CR8");
        await o.SetCreditLimitAsync(o.Customer, 1_000m);
        for (var round = 1; round <= 3; round++)
        {
            var a = await o.DraftSoAsync(o.Customer, 6m);
            var b = await o.DraftSoAsync(o.Customer, 6m);
            var results = await Task.WhenAll(o.TryConfirm(a), o.TryConfirm(b));
            results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(round == 1 ? 1 : 0,
                $"round {round}: 600 + 600 > 1 000, the per-customer lock lets one through at most — {string.Join(" | ", results.Select(r => r.ToString()))}");
            results.Where(r => r.Status != HttpStatusCode.OK).Should().OnlyContain(r => r.Status == HttpStatusCode.Conflict);
        }
        (await o.ExposureAsync(o.Customer.Uuid)).D("openOrdersAmount").Should().Be(600m);
    }

    [Fact]
    public async Task TS_CR_009_exposure_is_open_orders_plus_open_invoices()
    {
        var o = await BlockOrgAsync("CR9");
        await o.SetCreditLimitAsync(o.Customer, 10_000m);
        var inv = await o.IssuedInvoiceAsync(o.Customer, 10m);          // 1 000
        await o.PayCustomerAsync(o.Customer, 170m, (inv, 170m));        // 830 open
        var so = await o.DraftSoAsync(o.Customer, 7m);                  // 700 confirmed, not invoiced
        await o.K.ConfirmSaleOrderAsync(so);
        await o.DraftSoAsync(o.Customer, 9m);                           // a draft order does not count

        var e = await o.ExposureAsync(o.Customer.Uuid);
        e.D("openOrdersAmount").Should().Be(700m, "confirmed, not yet invoiced");
        e.D("openInvoicesAmount").Should().Be(830m, "issued balance after the payment");
        e.D("totalExposure").Should().Be(1_530m, "BR-CR-004");
        e.D("availableCredit").Should().Be(8_470m);
        e.B("isOverLimit").Should().BeFalse();
        e.S("baseCurrencyCode").Should().Be("PKR");
        e.S("creditControlMode").Should().Be("Block");
    }

    [Fact]
    public async Task TS_CR_010_risk_tier_defaults_to_medium_and_persists()
    {
        var o = await BlockOrgAsync("CR10");
        (await o.ProfileAsync(o.Customer.Uuid)).S("riskTier").Should().Be("Medium", "BR-CR-007");
        var viewer = await o.UserWithAsync("arview", AR_VIEW);
        (await o.RiskTier(o.Customer.Uuid, "High", h: viewer)).ShouldBeForbidden("BR-CR-007: CREDIT_MANAGE only");
        (await o.RiskTier(o.Customer.Uuid, "Extreme")).Status.Should().Be(HttpStatusCode.BadRequest);
        await o.K.Ok(o.RiskTier(o.Customer.Uuid, "High", "Late payer"), "risk tier");
        (await o.ProfileAsync(o.Customer.Uuid)).S("riskTier").Should().Be("High");
        (await o.ExposureAsync(o.Customer.Uuid)).S("riskTier").Should().Be("High");
        (await o.CreditHistoryAsync(o.Customer.Uuid)).Should().Contain(h => h.S("action") == "RiskTier");
    }

    [Fact]
    public async Task Off_mode_leaves_sale_orders_untouched()
    {
        var o = await _root.NewArOrgAsync("CROFF", stock: 300m);
        await o.SetCreditLimitAsync(o.Customer, 100m);
        await o.IssuedInvoiceAsync(o.Customer, 5m);                 // already over the 100 limit
        await o.K.Ok(o.Hold(o.Customer.Uuid, "Held while Off"), "a hold can be placed while Off");
        var so = await o.DraftSoAsync(o.Customer, 20m);
        var r = await o.TryConfirm(so);
        r.Status.Should().Be(HttpStatusCode.OK, $"D-5: mode Off never blocks — {r}");
        r.Result.IsNull("creditCheck").Should().BeTrue("creditCheck is null when the mode is Off");
        (await o.SoStatusAsync(so)).Should().Be("CONFIRMED");
    }

    [Fact]
    public async Task Warn_mode_confirms_with_a_warning()
    {
        var o = await BlockOrgAsync("CRW", mode: "Warn");
        await o.SetCreditLimitAsync(o.Customer, 100m);
        var so = await o.DraftSoAsync(o.Customer, 20m);
        var r = await o.TryConfirm(so);
        r.Status.Should().Be(HttpStatusCode.OK, $"Warn never blocks on the limit — {r}");
        r.Result.S("creditWarning").Should().NotBeNullOrWhiteSpace("the confirm result carries the warning");

        await o.K.Ok(o.Hold(o.Customer.Uuid, "Held in warn"), "hold");
        (await o.TryConfirm(await o.DraftSoAsync(o.Customer, 1m))).ShouldRefuse(HttpStatusCode.Conflict, "is on credit hold",
            "D-CORE-4: a hold blocks whenever the mode is not Off");
    }

    [Fact]
    public async Task Override_needs_the_permission_and_a_reason()
    {
        var o = await BlockOrgAsync("CROV");
        await o.SetCreditLimitAsync(o.Customer, 100m);
        var clerk = await o.UserWithAsync("soclerk", "SALE_ORDER_VIEW", "SALE_ORDER_CONFIRM");
        var so = await o.DraftSoAsync(o.Customer, 20m);
        (await o.ConfirmWithOverride(so, "Director approved", clerk)).ShouldRefuse(HttpStatusCode.Conflict, Failed,
            "a reason without CREDIT_OVERRIDE does not override");

        var manager = await o.UserWithAsync("socredit", "SALE_ORDER_VIEW", "SALE_ORDER_CONFIRM", CREDIT_OVERRIDE);
        (await o.ConfirmWithOverride(so, null, manager)).ShouldRefuse(HttpStatusCode.Conflict, Failed, "the permission without a reason");
        var ok = await o.ConfirmWithOverride(so, "Director approved", manager);
        ok.Status.Should().Be(HttpStatusCode.OK, ok.ToString());
        (await o.SoStatusAsync(so)).Should().Be("CONFIRMED");
        (await o.CreditHistoryAsync(o.Customer.Uuid)).Should().Contain(h => h.S("action") == "Override" && h.S("reason")!.Contains("Director approved"));
    }

    [Fact]
    public async Task Invoice_creation_only_warns()
    {
        var o = await BlockOrgAsync("CRINV");
        await o.SetCreditLimitAsync(o.Customer, 100_000m);
        var so = await o.DraftSoAsync(o.Customer, 10m);
        await o.K.ConfirmSaleOrderAsync(so);
        var delivery = await o.K.DeliverAsync(so);
        await o.K.Ok(o.Hold(o.Customer.Uuid, "Held after confirm"), "hold");

        var r = await o.K.TryRaiseInvoiceAsync(delivery);
        r.Status.Should().Be(HttpStatusCode.OK, $"D-5: invoice creation is warning only — {r}");
        r.Result.S("creditWarning").Should().NotBeNullOrWhiteSpace("the warning is on the invoice response");
    }

    /// <summary>Coordinator (1): confirm keeps working without a body or Content-Type (the override body is optional).</summary>
    [Fact]
    public async Task Confirm_without_body_or_content_type_is_not_415()
    {
        var o = await _root.NewArOrgAsync("CRNB", stock: 50m);
        foreach (var mode in new[] { "Off", "Block" })
        {
            await o.CreditModeAsync(mode);
            var so = await o.DraftSoAsync(o.Customer, 1m);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sale-orders/{so}/confirm");
            using var resp = await o.K.Admin.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"{mode}: no body, no Content-Type — {await resp.Content.ReadAsStringAsync()}");
            (await o.SoStatusAsync(so)).Should().Be("CONFIRMED");
        }
    }

    /// <summary>Tasks 124–126 at HTTP level: the check runs on confirm, a customer with no profile gets one, the 409 explains the exposure.</summary>
    [Fact]
    public async Task Tasks_124_126_confirm_runs_the_check_auto_creates_the_profile_and_explains_the_block()
    {
        var o = await BlockOrgAsync("CR124");
        var fresh = await o.CustomerAsync("Never opened");
        await o.SetCreditLimitAsync(fresh, 1_000m);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.partner_financial_profiles WHERE OrganizationId = @o AND PartnerUuid = @p",
            ("@o", o.OrgId), ("@p", fresh.Uuid))).Should().Be(0, "no profile yet");

        var ok = await o.TryConfirm(await o.DraftSoAsync(fresh, 6m));
        ok.Status.Should().Be(HttpStatusCode.OK, $"task 126: no profile → allowed within the limit — {ok}");
        ok.Result.P("creditCheck").B("approved").Should().BeTrue("task 124: the check ran on confirm");
        ok.Result.P("creditCheck").D("projectedExposure").Should().Be(600m);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.partner_financial_profiles WHERE OrganizationId = @o AND PartnerUuid = @p",
            ("@o", o.OrgId), ("@p", fresh.Uuid))).Should().Be(1, "task 125: the profile is created on first use");

        var blocked = await o.TryConfirm(await o.DraftSoAsync(fresh, 6m));
        blocked.ShouldRefuse(HttpStatusCode.Conflict, "Exceeds the credit limit of 1,000.00 PKR by 200.00 PKR (exposure 600.00, this order 600.00).",
            "a user-friendly 409 with the exposure details");
    }

    [Fact]
    public async Task Credit_endpoints_validate_and_404_unknown_customers()
    {
        var o = await BlockOrgAsync("CRV");
        var nobody = Guid.NewGuid();
        (await o.Exposure(nobody)).Status.Should().Be(HttpStatusCode.NotFound);
        (await o.Hold(o.Customer.Uuid, " ")).ShouldRefuse(HttpStatusCode.BadRequest, "A reason is required.", "blank reason");
        (await o.TempLimit(o.Customer.Uuid, 0m, Today.AddDays(3))).ShouldRefuse(HttpStatusCode.BadRequest,
            "The temporary limit must be greater than zero.", "zero temp limit");
        await o.SetCreditLimitAsync(o.Customer, null);
        (await o.TempLimit(o.Customer.Uuid, 5_000m, Today.AddDays(3))).ShouldRefuse(HttpStatusCode.BadRequest,
            "has no permanent credit limit; set one on the customer before granting a temporary one.", "no permanent limit");
        (await o.Profile(o.Vendor.Uuid, "Customer")).ShouldRefuse(HttpStatusCode.BadRequest, "is not a customer.", "a vendor-only partner");
        (await o.K.Get($"/api/finance/partner-profiles/{o.Customer.Uuid}")).ShouldRefuse(HttpStatusCode.BadRequest,
            "role is required: Customer or Vendor.", "role missing");
    }
}
