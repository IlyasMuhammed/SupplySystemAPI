using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.Pos;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — D-33..D-35: <c>GET api/finance/posting/by-source/{uuid}</c>, <c>POST sync/push</c> for a document the gateway never
/// mapped, and the QuickBooks call log <c>GET sync/log[/{id}]</c> (org isolation, failedOnly, redaction, INTEGRATION_VIEW).
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~SyncLogAndBySourceE2ETests</c>.</para>
/// </summary>
public sealed class SyncLogAndBySourceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public SyncLogAndBySourceE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    [Fact]
    public async Task By_source_and_push_now_of_an_unmapped_document()
    {
        var o = await _root.NewOrgAsync("BYS");
        var k = o.K;
        (await k.Ok(k.Get($"{Posting}/by-source/{Guid.NewGuid()}"), "unknown uuid")).Items().Should().BeEmpty("200 with an empty list");

        // an invoice issued before QuickBooks was connected: no map at all
        var (_, _, customer, item) = await k.StockedWorldAsync("BYS");
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        o = await o.ConnectLiveAsync();
        (await _f.MapOrNullAsync(o.OrgId, inv.G("uuid").ToString(), "SalesInvoice")).Should().BeNull("issued before connecting");

        (await k.Ok(k.Get($"{Posting}/by-source/{inv.G("uuid")}"), "by-source")).Items()
            .Should().BeEmpty("no target was active at issue: the gateway answers NoTargetConfigured and records nothing (D-10)");

        // D-35: Push now creates the map from the source and queues it
        await k.Ok(k.Post($"{Qb}/sync/push", new { Kind = "SalesInvoice", ExternalIds = new[] { inv.G("uuid").ToString() } }), "push now");
        var queued = await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice");
        queued.State.Should().BeOneOf(new[] { "Pending", "WaitingOnDependency", "InProgress", "Synced" }, queued.ToString());
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");

        // another organization sees none of it
        var other = await _root.NewOrgAsync("BYS2");
        (await other.K.Ok(other.K.Get($"{Posting}/by-source/{inv.G("uuid")}"), "other org")).Items().Should().BeEmpty();
    }

    /// <summary>REV-20 (HIGH): "Push now" as SalesInvoice of a POS cash sale (sent as a SalesReceipt) must not create an Invoice too.</summary>
    [Fact]
    public async Task Push_now_as_SalesInvoice_of_a_receipt_shaped_POS_sale_creates_no_invoice()
    {
        var w = await _root.PosWorldAsync("REV20");
        var o = await new FpOrg(w.O, null).ConnectLiveAsync();
        var k = o.K;
        await k.Ok(k.SetSyncControl("POSSale", enabled: true, confirmRelease: true), "POS sales on");
        var shift = await w.OpenShiftAsync(100m);
        var sale = await w.SellAsync(w.Order(shift, [PosKitLine(w)], [Pos.PosKit.Pay("CASH", 11.70m)]), w.Supervisor.Client);
        var invoice = (await w.OrderAsync(sale.G("orderUuid"))).G("salesInvoiceUuid");
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, invoice, "SalesReceipt")).State.Should().Be("Synced");

        _f.Company.ResetCalls();
        var push = await k.Post($"{Qb}/sync/push", new { Kind = "SalesInvoice", ExternalIds = new[] { invoice.ToString() } });
        push.Status.Should().NotBe(HttpStatusCode.InternalServerError, push.ToString());
        await _f.RunJobsAsync(4);
        _f.Company.Writes.Should().NotContain(c => c.Kind == SyncKind.SalesInvoice, "a receipt-shaped POS sale is never also an Invoice");
        (await _f.MapOrNullAsync(o.OrgId, invoice.ToString(), "SalesInvoice")).Should().BeNull("no placeholder SalesInvoice map");
        (await _f.MapAsync(o.OrgId, invoice, "SalesReceipt")).State.Should().Be("Synced");
    }

    private static object PosKitLine(Pos.PosWorld w) => Pos.PosKit.Line(w.A, 1m, 10m);

    [Fact]
    public async Task The_call_log_is_per_organization_filters_failures_and_is_redacted()
    {
        var a = await (await _root.NewOrgAsync("LOGA")).ConnectLiveAsync();
        var b = await (await _root.NewOrgAsync("LOGB")).ConnectLiveAsync();
        var k = a.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("LOGA");

        // one QuickBooks call fails on purpose (a timeout after the create), then the sweep / retry settles it
        _f.Company.TimeOutAfterNextCreateOf = SyncKind.Customer;
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, a.O.Pkr);
        await _f.RunJobsAsync(2);
        // the backoff (1 min) "passes"; the retry finds the record the timed-out create made (Find-before-retry) and goes on
        await _f.ExecuteAsync("UPDATE x SET NextAttemptAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) FROM integration.Outbox x " +
                              "JOIN integration.Connections c ON c.Id = x.ConnectionId WHERE c.OrganizationId = @o", ("@o", a.OrgId));
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(a.OrgId, customer.Uuid, "Customer")).State.Should().Be("Synced", "the timed-out create was settled");
        _f.Company.Records(SyncKind.Customer).Count(r => r.Name == customer.Name).Should().Be(1, "never created twice");

        var all = await k.Ok(k.Get($"{Qb}/sync/log?failedOnly=false&unresolvedOnly=false&page=1&pageSize=200"), "call log");
        var rows = all.A("data");
        // (the one fake company is shared by every org of the class, so an invoice number another org already used is a
        // "duplicate" here and adopted by Find — in real QuickBooks each org has its own company)
        rows.Should().Contain(r => r.S("kind") == "SalesInvoice" && r.S("outcome") == "Succeeded" && r.S("externalId") == inv.G("uuid").ToString());
        (await _f.MapAsync(a.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");
        var failed = await k.Ok(k.Get($"{Qb}/sync/log?unresolvedOnly=false&page=1&pageSize=200"), "failedOnly (default); unresolvedOnly=false: A44 QA-7 hides calls of Synced records");
        failed.A("data").Should().NotBeEmpty("the timeout is in the log").And.OnlyContain(r => r.S("outcome") != "Succeeded" && r.S("outcome") != "DryRun");
        failed.A("data").Should().Contain(r => r.S("kind") == "Customer" && r.S("outcome") == "Transient", J.Short(failed));
        (await k.Ok(k.Get($"{Qb}/sync/log?failedOnly=false&unresolvedOnly=false&kind=SalesInvoice&page=1&pageSize=200"), "kind filter")).A("data")
            .Should().OnlyContain(r => r.S("kind") == "SalesInvoice");

        // org B never sees org A's calls
        var bRows = (await b.K.Ok(b.K.Get($"{Qb}/sync/log?failedOnly=false&unresolvedOnly=false&page=1&pageSize=200"), "org B log")).A("data");
        bRows.Select(r => r.S("externalId")).Should().NotContain([inv.G("uuid").ToString(), customer.Uuid.ToString()]);
        var aCall = rows.First(r => r.S("externalId") == inv.G("uuid").ToString());
        (await b.K.Get($"{Qb}/sync/log/{aCall.G("id")}")).Status.Should().Be(HttpStatusCode.NotFound, "another org's call");

        // the detail carries the redacted request/response, never a token or secret
        var detail = await k.Ok(k.Get($"{Qb}/sync/log/{aCall.G("id")}"), "call detail");
        // the fake provider records no wire JSON (the real QBO client does — checked in the live run: present, no secrets)
        (detail.S("id"), detail.S("operation")).Should().Be((aCall.S("id"), aCall.S("operation")));
        foreach (var secret in new[] { "AT-fp-", "RT-fp-", "AT-refresh", "RT-refresh", "sap-e2e-client-secret", "Bearer " })
            detail.GetRawText().Should().NotContain(secret);
        (await k.Get($"{Qb}/sync/log/{Guid.NewGuid()}")).Status.Should().Be(HttpStatusCode.NotFound);

        // INTEGRATION_VIEW reads; without it 403
        var viewer = await k.LoginWithPermissionsAsync("logview", "INTEGRATION_VIEW");
        var nobody = await k.LoginWithPermissionsAsync("lognone", "INVOICE_VIEW");
        (await k.Get($"{Qb}/sync/log", viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Get($"{Qb}/sync/log/{aCall.G("id")}", viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Get($"{Qb}/sync/log", nobody)).ShouldBeForbidden("no INTEGRATION_VIEW");
        (await k.Get($"{Qb}/sync/log/{aCall.G("id")}", nobody)).ShouldBeForbidden("no INTEGRATION_VIEW");
        (await k.Get($"{Posting}/by-source/{inv.G("uuid")}", nobody)).ShouldBeForbidden("by-source needs a GL or integration read permission");
        (await k.Get($"{Posting}/by-source/{inv.G("uuid")}", viewer)).Status.Should().Be(HttpStatusCode.OK);
    }
}
