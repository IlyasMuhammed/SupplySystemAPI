using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — 3-way matching end to end (C9, D-7, D-8; TS-MT-001..010, BR-MT-001..008, task 585): PO (10 @ 300) → sent →
/// GRN (its automatic invoice rejected, so the test's invoice is the only one billing the receipt) → supplier invoice with
/// one line → POST match → override / approve → payment schedule. Tolerance = the seeded DEFAULT rule (2 % price, 0 %
/// quantity, auto-approve off, D-16) unless the test changes it.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ApMatchingE2ETests</c>.
/// </summary>
public sealed class ApMatchingE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const decimal Price = 300m;
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ApMatchingE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48MT");
    }

    private sealed record Billed(ArOrg O, Guid Po, Guid PoLine, Guid? Grn, Guid? GrnLine, Guid Invoice, string Number);

    /// <summary>PO of <paramref name="poQty"/>, GRN of <paramref name="grnQty"/> (none when null), invoice of qty × price.</summary>
    private async Task<Billed> BillAsync(ArOrg? org, string prefix, decimal poQty, decimal? grnQty, decimal invQty, decimal invPrice, Partner? vendor = null)
    {
        var o = org ?? await _root.NewArOrgAsync(prefix, stock: 1m);
        var v = vendor ?? o.Vendor;
        var (po, poLine) = await o.OrderedAsync(v, poQty, Price);
        Guid? grn = null, grnLine = null;
        if (grnQty is { } q) (grn, grnLine) = await o.ReceivedAsync(po, q);
        var number = $"{o.K.Marker}-{Guid.NewGuid():N}"[..20];
        var inv = await o.SupplierInvoiceAsync(v, number, po, poLine, grn, grnLine, invQty, invPrice);
        return new Billed(o, po, poLine, grn, grnLine, inv, number);
    }

    private static async Task<JsonElement> MatchOkAsync(Billed b, string? type = null) => await b.O.K.Ok(b.O.Match(b.Invoice, type), "match");

    [Fact]
    public async Task TS_MT_001_all_lines_within_tolerance_match()
    {
        var b = await BillAsync(null, "MT1", 10m, 10m, 10m, Price);
        var m = await MatchOkAsync(b);
        m.S("matchType").Should().Be("ThreeWay", "the invoice names a GRN");
        m.S("lineMatchResult").Should().Be("Matched", J.Short(m));
        m.B("withinTolerance").Should().BeTrue();
        m.A("lines").Should().ContainSingle().Which.S("lineStatus").Should().Be("Matched");
        var line = m.A("lines")[0];
        line.D("poQuantity").Should().Be(10m);
        line.D("grnQuantity").Should().Be(10m);
        line.D("invoiceQuantity").Should().Be(10m);
        line.D("poUnitPrice").Should().Be(Price);
        (await b.O.K.Ok(b.O.MatchDetail(b.Invoice), "match detail")).S("lineMatchResult").Should().Be("Matched");
    }

    [Fact]
    public async Task TS_MT_002_price_3pct_over_with_2pct_tolerance_is_a_price_mismatch()
    {
        var b = await BillAsync(null, "MT2", 10m, 10m, 10m, 309m);
        var m = await MatchOkAsync(b);
        m.S("lineMatchResult").Should().Be("Mismatch", J.Short(m));
        var line = m.A("lines").Single();
        line.S("lineStatus").Should().Be("PriceMismatch");
        line.D("priceVariance").Should().Be(9m);
        line.D("priceVariancePercent").Should().Be(3m);
        m.B("withinTolerance").Should().BeFalse();
    }

    [Fact]
    public async Task TS_MT_003_price_1_5pct_over_with_2pct_tolerance_matches()
    {
        var b = await BillAsync(null, "MT3", 10m, 10m, 10m, 304.5m);
        var m = await MatchOkAsync(b);
        m.S("lineMatchResult").Should().Be("Matched", $"BR-MT-003: 1.5 % ≤ 2 % — {J.Short(m)}");
        m.A("lines").Single().D("priceVariancePercent").Should().Be(1.5m);
    }

    [Fact]
    public async Task TS_MT_004_invoice_quantity_above_grn_is_a_quantity_mismatch()
    {
        var b = await BillAsync(null, "MT4", 10m, 8m, 10m, Price);
        var m = await MatchOkAsync(b);
        m.S("lineMatchResult").Should().Be("Mismatch", J.Short(m));
        var line = m.A("lines").Single();
        line.S("lineStatus").Should().Be("QuantityMismatch", "BR-MT-004: 10 invoiced against 8 received");
        line.D("quantityVariance").Should().Be(2m);
    }

    [Fact]
    public async Task TS_MT_005_override_needs_a_reason_and_the_permission()
    {
        var b = await BillAsync(null, "MT5", 10m, 10m, 10m, 330m);
        (await MatchOkAsync(b)).S("lineMatchResult").Should().Be("Mismatch");

        (await b.O.Override(b.Invoice, " ")).ShouldRefuse(HttpStatusCode.BadRequest, "An override needs a reason.", "BR-MT-006");
        var matcher = await b.O.UserWithAsync("mtexec", MATCH_EXECUTE, AP_VIEW);
        (await b.O.Override(b.Invoice, "Agreed price rise", matcher)).ShouldBeForbidden("BR-MT-006: MATCH_OVERRIDE only");
        var overrider = await b.O.UserWithAsync("mtover", MATCH_OVERRIDE, AP_VIEW);
        var m = await b.O.K.Ok(b.O.Override(b.Invoice, "Agreed price rise", overrider), "override");
        m.S("overrideReason").Should().Be("Agreed price rise");
        m.A("lines").Single().S("lineStatus").Should().Be("Overridden", "TS-MT-005");
        m.IsNull("overriddenAt").Should().BeFalse();

        var ok = await BillAsync(b.O, "", 10m, 10m, 10m, Price);
        (await MatchOkAsync(ok)).S("lineMatchResult").Should().Be("Matched");
        (await b.O.Override(ok.Invoice, "nothing to override")).ShouldRefuse(HttpStatusCode.Conflict,
            "Only an invoice with a match variance can be overridden.", "override of a clean match");
    }

    [Fact]
    public async Task TS_MT_006_approve_posts_and_creates_the_payment_schedule()
    {
        var o = await _root.NewArOrgAsync("MT6", gl: true, stock: 1m);
        var b = await BillAsync(o, "", 10m, 10m, 10m, Price);
        (await MatchOkAsync(b)).S("lineMatchResult").Should().Be("Matched");
        var approver = await o.UserWithAsync("mtappr", MATCH_APPROVE, AP_VIEW);
        await o.K.Ok(o.ApproveMatch(b.Invoice, approver), "approve match");

        var inv = await o.SupplierInvoiceDetailAsync(b.Invoice);
        inv.S("matchStatus").Should().Be("Approved", "the existing approval path ran");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_ledger_entries WHERE ReferenceId = @i AND TransactionType = 'INVOICE_APPROVED'",
            ("@i", b.Invoice))).Should().Be(1, "the supplier ledger got the payable");

        var schedule = await o.ScheduleOfAsync(b.Invoice);
        schedule.ValueKind.Should().Be(JsonValueKind.Object, "D-8: a schedule row is created on approval");
        schedule.S("status").Should().Be("Open");
        schedule.D("amountRemaining").Should().Be(3_000m);
        schedule.S("dueDate").Should().StartWith(Iso(Today.AddDays(30)));

        await _f.RunJobsAsync();
        var journals = await SapKit.WaitForAsync(() => _f.JournalsAsync(o.OrgId, b.Invoice), j => j.Count > 0, "the bill's A44 journal");
        journals.Last().Balanced.Should().BeTrue("TS-MT-006: the approval posts through A44");
        var codes = await o.K.DefaultCodesAsync();
        journals.Last().Net(codes["DEFAULT_PAYABLE"]).Should().Be(-3_000m, $"Cr payable — {journals.Last()}");
    }

    [Fact]
    public async Task TS_MT_007_duplicate_vendor_invoice_number_is_rejected()
    {
        var b = await BillAsync(null, "MT7", 10m, 10m, 5m, Price);
        var again = await b.O.TrySupplierInvoice(b.O.Vendor, b.Number, b.Po, b.PoLine, b.Grn, b.GrnLine, 5m, Price);
        again.ShouldRefuse(HttpStatusCode.Conflict, $"Supplier invoice number {b.Number} is already recorded for", "BR-MT-007");

        var other = await b.O.VendorAsync("Other vendor");
        (await b.O.TrySupplierInvoice(other, b.Number, null, null, null, null, 1m, 10m)).Status.Should().Be(HttpStatusCode.OK,
            "the same number at another vendor is fine");

        await b.O.K.Ok(b.O.K.Post($"{SupplierInvoices}/{b.Invoice}/reject", new { Reason = "wrong" }), "reject");
        (await b.O.TrySupplierInvoice(b.O.Vendor, b.Number, b.Po, b.PoLine, b.Grn, b.GrnLine, 5m, Price)).Status.Should().Be(HttpStatusCode.OK,
            "a rejected invoice does not count as a duplicate");
    }

    [Fact]
    public async Task TS_MT_008_auto_approve_within_tolerance()
    {
        var o = await _root.NewArOrgAsync("MT8", stock: 1m);
        (await o.SetDefaultToleranceAsync(2m, autoApprove: true)).B("autoApproveWithin").Should().BeTrue();
        var b = await BillAsync(o, "", 10m, 10m, 10m, 303m);
        var m = await MatchOkAsync(b);
        m.S("lineMatchResult").Should().Be("Matched");
        m.B("autoApproved").Should().BeTrue("BR-MT-005");
        (await o.SupplierInvoiceDetailAsync(b.Invoice)).S("matchStatus").Should().Be("Approved");
        (await o.ScheduleOfAsync(b.Invoice)).ValueKind.Should().Be(JsonValueKind.Object, "approved → scheduled");

        var off = await BillAsync(o, "", 10m, 10m, 10m, 330m);
        var mm = await MatchOkAsync(off);
        mm.B("autoApproved").Should().BeFalse("outside tolerance nothing is auto-approved");
        (await o.SupplierInvoiceDetailAsync(off.Invoice)).S("matchStatus").Should().NotBe("Approved");
    }

    [Fact]
    public async Task TS_MT_009_two_way_match_without_grn()
    {
        var b = await BillAsync(null, "MT9", 10m, null, 10m, Price);
        var m = await MatchOkAsync(b, "TwoWay");
        m.S("matchType").Should().Be("TwoWay");
        m.S("lineMatchResult").Should().Be("Matched", $"BR-MT-002: PO ↔ invoice — {J.Short(m)}");
        m.IsNull("grnUuid").Should().BeTrue();
    }

    [Fact]
    public async Task TS_MT_010_partial_delivery_invoiced_as_received_matches()
    {
        var b = await BillAsync(null, "MT10", 10m, 6m, 6m, Price);
        var m = await MatchOkAsync(b);
        m.S("lineMatchResult").Should().Be("Matched", J.Short(m));
        var line = m.A("lines").Single();
        line.D("poQuantity").Should().Be(10m);
        line.D("grnQuantity").Should().Be(6m);
        line.D("invoiceQuantity").Should().Be(6m);
    }

    [Fact]
    public async Task BR_MT_008_a_mismatch_is_approved_only_after_override()
    {
        var b = await BillAsync(null, "MT88", 10m, 10m, 10m, 330m);
        (await MatchOkAsync(b)).S("lineMatchResult").Should().Be("Mismatch");
        var inv = await b.O.SupplierInvoiceDetailAsync(b.Invoice);
        (await b.O.ApproveMatch(b.Invoice)).ShouldRefuse(HttpStatusCode.Conflict,
            $"Invoice {inv.S("invoiceNumber")} has a match variance; override it before approving.", "BR-MT-008");
        (await b.O.ScheduleOfAsync(b.Invoice)).ValueKind.Should().NotBe(JsonValueKind.Object, "nothing payable yet");

        await b.O.K.Ok(b.O.Override(b.Invoice, "Accepted"), "override");
        await b.O.K.Ok(b.O.ApproveMatch(b.Invoice), "approve after override");
        (await b.O.SupplierInvoiceDetailAsync(b.Invoice)).S("matchStatus").Should().Be("Approved");
    }

    [Fact]
    public async Task Vendor_tolerance_rule_beats_the_default_and_rules_validate()
    {
        var o = await _root.NewArOrgAsync("MTV", stock: 1m);
        var lenient = await o.VendorAsync("Lenient");
        await o.K.Ok(o.CreateTolerance("Lenient 5%", 5m, vendor: lenient.Uuid), "vendor rule");
        (await o.CreateTolerance("Lenient 5%", 5m)).ShouldRefuse(HttpStatusCode.Conflict, "A tolerance rule named 'Lenient 5%' already exists.", "unique name");
        (await o.CreateTolerance("Second", 3m, vendor: lenient.Uuid)).ShouldRefuse(HttpStatusCode.Conflict, "already has an active tolerance rule", "one per vendor");
        (await o.CreateTolerance("Negative", -1m)).ShouldRefuse(HttpStatusCode.BadRequest, "Tolerances cannot be negative.", "negative");
        (await o.CreateTolerance("Huge", 101m)).ShouldRefuse(HttpStatusCode.BadRequest, "A percentage cannot exceed 100.", "over 100");

        var four = await BillAsync(o, "", 10m, 10m, 10m, 312m, vendor: lenient);
        (await MatchOkAsync(four)).S("lineMatchResult").Should().Be("Matched", "4 % within the vendor's 5 %");
        var strict = await BillAsync(o, "", 10m, 10m, 10m, 312m);
        (await MatchOkAsync(strict)).S("lineMatchResult").Should().Be("Mismatch", "4 % outside the default 2 %");
    }
}
