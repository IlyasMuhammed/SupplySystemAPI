using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.Pos;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;
using static SMS.Integration.Tests.Pos.PosKit;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — sync control (D-14, T-62..T-68) on the real host with only Intuit faked:
/// <list type="bullet">
/// <item>T-62 defaults, T-63 hold, T-64 confirm + release, T-65 skip, T-66 hold of queued work, BR-29, BR-31 —
/// <see cref="Defaults_hold_release_skip_and_no_row_means_enabled"/>.</item>
/// <item>T-67 (POS hold generalised) — <see cref="POS_sales_are_held_by_default_and_released_when_enabled"/>.</item>
/// <item>T-68, D-31 — <see cref="INTEGRATION_VIEW_reads_and_INTEGRATION_MANAGE_writes"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~SyncControlE2ETests</c>.</para>
/// </summary>
public sealed class SyncControlE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public SyncControlE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private bool Sent(string docNumber) => _f.Company.Records(SyncKind.SalesInvoice).Any(r => r.Name == docNumber);

    [Fact]
    public async Task Defaults_hold_release_skip_and_no_row_means_enabled()
    {
        var o = await (await _root.NewOrgAsync("SC1")).ConnectLiveAsync();
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("SC1");

        // T-62 — one row per type, the D-14 / §22.3 defaults (purchase side off without hold, POS off with hold)
        var rows = await k.SyncControlAllAsync();
        rows.Select(r => r.S("documentType")).Should().BeEquivalentTo(
            "Customer", "Vendor", "Item", "SalesInvoice", "CreditNote", "Payment", "POSSale", "POSReturn", "PurchaseBill", "BillPayment", "VendorCredit");
        string State(string t) { var r = rows.Single(x => x.S("documentType") == t); return $"{r.B("isEnabled")}/{r.B("holdWhenDisabled")}/{r.S("action")}"; }
        foreach (var t in new[] { "Customer", "Vendor", "Item", "SalesInvoice", "CreditNote", "Payment" }) State(t).Should().Be("True/False/Process", t);
        foreach (var t in new[] { "POSSale", "POSReturn" }) State(t).Should().Be("False/True/Hold", t);
        foreach (var t in new[] { "BillPayment", "VendorCredit" }) State(t).Should().Be("False/False/Skip", t);
        (await _f.QueryAsync("SELECT COUNT(*) AS N FROM integration.SyncControlConfigs s JOIN integration.Connections c ON c.Id = s.ConnectionId " +
                             "WHERE c.OrganizationId = @o", ("@o", o.OrgId))).Single()["N"].Should().Be(11, "a new connection is seeded with every row");

        // T-63 — disabled with hold: an issued invoice is held, nothing goes to QuickBooks
        await k.Ok(k.SetSyncControl("SalesInvoice", enabled: false, hold: true), "hold sales invoices");
        var inv1 = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        await _f.RunJobsAsync(3);
        Sent(inv1.S("invoiceNumber")!).Should().BeFalse("held");
        (await k.SyncControlRowAsync("SalesInvoice")).I("heldCount").Should().Be(1);

        // T-64 — enabling needs a confirmation that says how many; the explicit release refuses while disabled
        var unconfirmed = await k.SetSyncControl("SalesInvoice", enabled: true, confirmRelease: false);
        unconfirmed.Status.Should().Be(HttpStatusCode.Conflict, unconfirmed.ToString());
        unconfirmed.Result.I("heldCount").Should().Be(1);
        (await k.Post($"{Qb}/sync-control/SalesInvoice/release")).Status.Should().Be(HttpStatusCode.Conflict, "turned off: enable it instead");

        // BR-31 — enabling releases everything held
        var enabled = await k.Ok(k.SetSyncControl("SalesInvoice", enabled: true, confirmRelease: true), "enable with release");
        enabled.I("released").Should().Be(1, J.Short(enabled));
        await _f.RunJobsAsync(3);
        Sent(inv1.S("invoiceNumber")!).Should().BeTrue("released and sent");
        (await _f.MapAsync(o.OrgId, inv1.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");

        // T-66 — queued but not yet sent, then disabled with hold: it waits too
        var inv2 = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        var holdNow = await k.Ok(k.SetSyncControl("SalesInvoice", enabled: false, hold: true), "hold again");
        holdNow.I("heldNow").Should().BeGreaterThanOrEqualTo(1, J.Short(holdNow));
        await _f.RunJobsAsync(3);
        Sent(inv2.S("invoiceNumber")!).Should().BeFalse();
        await k.Ok(k.SetSyncControl("SalesInvoice", enabled: true, confirmRelease: true), "enable");
        await _f.RunJobsAsync(3);
        Sent(inv2.S("invoiceNumber")!).Should().BeTrue();

        // T-65 — disabled without hold (skip): nothing held, nothing sent, enabling releases nothing
        await k.Ok(k.SetSyncControl("SalesInvoice", enabled: false, hold: false), "skip sales invoices");
        (await k.SyncControlRowAsync("SalesInvoice")).S("action").Should().Be("Skip");
        var inv3 = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        await _f.RunJobsAsync(3);
        Sent(inv3.S("invoiceNumber")!).Should().BeFalse("skipped");
        (await k.SyncControlRowAsync("SalesInvoice")).I("heldCount").Should().Be(0);
        (await k.Ok(k.SetSyncControl("SalesInvoice", enabled: true), "enable")).I("released").Should().Be(0);

        // BR-29 — no row for a type = enabled (Payment has no legacy switch to fall back to)
        await _f.ExecuteAsync("DELETE s FROM integration.SyncControlConfigs s JOIN integration.Connections c ON c.Id = s.ConnectionId " +
                              "WHERE c.OrganizationId = @o AND s.DocumentType = 'Payment'", ("@o", o.OrgId));
        var payRow = await k.SyncControlRowAsync("Payment");
        (payRow.B("isEnabled"), payRow.B("isDefault")).Should().Be((true, true), J.Short(payRow));
        var pay = await k.RecordPaymentAsync(customer, inv1.D("grandTotal"), "CASH", (inv1.G("uuid"), inv1.D("grandTotal")));
        await _f.RunJobsAsync(3);
        (await _f.MapAsync(o.OrgId, pay.G("paymentUuid"), "Payment")).State.Should().Be("Synced", "no row = enabled");

        // the change is audited (D-14 ModifiedBy/At)
        var si = await k.SyncControlRowAsync("SalesInvoice");
        (si.IsNull("modifiedAt"), si.B("isDefault")).Should().Be((false, false));
    }

    [Fact]
    public async Task POS_sales_are_held_by_default_and_released_when_enabled()
    {
        var w = await _root.PosWorldAsync("SCPOS");
        var o = await new FpOrg(w.O, null).ConnectLiveAsync();
        var k = o.K;
        var shift = await w.OpenShiftAsync(100m);

        var sale = await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), w.Supervisor.Client);
        var invoice = (await w.OrderAsync(sale.G("orderUuid"))).G("salesInvoiceUuid");
        await _f.RunJobsAsync(3);
        foreach (var kind in new[] { "SalesReceipt", "SalesInvoice" })
            (await _f.MapOrNullAsync(o.OrgId, invoice.ToString(), kind))?.State.Should().NotBe("Synced", $"held ({kind})");
        (await k.SyncControlRowAsync("POSSale")).I("heldCount").Should().Be(1, "the POS sale is held while POS sales are off (D-14 default)");

        var released = await k.Ok(k.SetSyncControl("POSSale", enabled: true, confirmRelease: true), "enable POS sales");
        released.I("released").Should().Be(1);
        await _f.RunJobsAsync(4);
        var map = await _f.MapOrNullAsync(o.OrgId, invoice.ToString(), "SalesReceipt")
                  ?? await _f.MapOrNullAsync(o.OrgId, invoice.ToString(), "SalesInvoice");
        map.Should().NotBeNull();
        map!.State.Should().Be("Synced", map.ToString());

        // the old settings card (AutoPushPosSales / PosSalesWaiting) still reads the same thing (R-1 bridge)
        var settings = await k.Ok(k.Get($"{Qb}/settings"), "settings");
        (settings.B("autoPushPosSales"), settings.I("posSalesWaiting")).Should().Be((true, 0));
    }

    [Fact]
    public async Task INTEGRATION_VIEW_reads_and_INTEGRATION_MANAGE_writes()
    {
        var o = await (await _root.NewOrgAsync("SCP")).ConnectLiveAsync();
        var k = o.K;
        var viewer = await k.LoginWithPermissionsAsync("qbview", "INTEGRATION_VIEW");
        var manager = await k.LoginWithPermissionsAsync("qbmanage", "INTEGRATION_VIEW", "INTEGRATION_MANAGE");
        var nobody = await k.LoginWithPermissionsAsync("qbnone", "INVOICE_VIEW");

        string[] reads =
        [
            $"{Qb}/sync-control", $"{Qb}/provider-defaults", $"{Qb}/document-overrides", $"{Qb}/accounts", $"{Qb}/coverage",
            $"{Qb}/matching/Customer/remote?linked=false"
        ];
        foreach (var url in reads)
        {
            (await k.Get(url, viewer)).Status.Should().Be(HttpStatusCode.OK, $"INTEGRATION_VIEW reads {url}");
            (await k.Get(url, nobody)).ShouldBeForbidden($"no integration permission: {url}");
        }
        (await k.Post($"{Qb}/validation/check", new { kind = "Customer", externalId = Guid.NewGuid().ToString() }, viewer))
            .Status.Should().Be(HttpStatusCode.OK, "validation/check is a read (VIEW)");

        var writes = new (string Method, string Url, object? Body)[]
        {
            ("PUT", $"{Qb}/sync-control/Payment", new { isEnabled = true, holdWhenDisabled = false, confirmRelease = true }),
            ("POST", $"{Qb}/sync-control/Payment/release", null),
            ("PUT", $"{Qb}/provider-defaults", new { values = Array.Empty<object>() }),
            ("PUT", $"{Qb}/document-overrides", new { overrides = Array.Empty<object>() }),
            ("POST", $"{Qb}/accounts/auto-map", new { overwrite = false }),
            ("PUT", $"{Qb}/accounts", new { mappings = Array.Empty<object>() }),
            ("POST", $"{Qb}/tax-mappings/import", null),
            ("POST", $"{Qb}/matching/Customer/import", null),
            ("POST", $"{Qb}/matching/Customer/remote/1/link", new { externalId = Guid.NewGuid().ToString() }),
            ("POST", $"{Qb}/sync/items/retry-failed", new { kind = (string?)null }),
            ("POST", $"{Qb}/sync/items/{Guid.NewGuid()}/skip", null),
            ("POST", $"{Qb}/sync/items/{Guid.NewGuid()}/unlink", null)
        };
        foreach (var (method, url, body) in writes)
        {
            (await k.Send(new HttpMethod(method), url, body, viewer)).ShouldBeForbidden($"INTEGRATION_VIEW: {method} {url}");
            (await k.Send(new HttpMethod(method), url, body, manager)).ShouldNotBeForbidden($"INTEGRATION_MANAGE: {method} {url}");
        }
    }
}
