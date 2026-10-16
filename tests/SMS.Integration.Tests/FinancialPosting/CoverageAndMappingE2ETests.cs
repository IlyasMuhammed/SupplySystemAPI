using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — mappings, coverage, validation and the dead letter on the real host with only Intuit faked:
/// <list type="bullet">
/// <item>T-40..T-44, D-17, BR-18 — <see cref="QuickBooks_records_import_as_remote_only_link_and_unlink"/>.</item>
/// <item>T-45..T-49, D-18 — <see cref="Tax_import_saves_only_unambiguous_suggestions_and_the_validator_lists_every_unmapped_code"/>.</item>
/// <item>D-16 — <see cref="Accounts_auto_map_into_the_A42_mappings"/>.</item>
/// <item>T-50..T-54, D-27, D-11 — <see cref="Coverage_validation_and_the_dead_letter"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~CoverageAndMappingE2ETests</c>.</para>
/// </summary>
public sealed class CoverageAndMappingE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public CoverageAndMappingE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private async Task<JsonElement> SyncItemAsync(SapKit k, string kind, Guid externalId)
    {
        var page = await k.Ok(k.Get($"{Qb}/sync/items?kind={kind}&page=1&pageSize=100"), "sync items");
        return page.A("data").Single(i => i.S("externalId") == externalId.ToString());
    }

    private async Task<int> PartnerCountAsync(Guid org) =>
        Convert.ToInt32((await _f.QueryAsync("SELECT COUNT(*) AS N FROM suppliers.BusinessPartners WHERE OrganizationId = @o", ("@o", org))).Single()["N"]);

    [Fact]
    public async Task QuickBooks_records_import_as_remote_only_link_and_unlink()
    {
        var o = await (await _root.NewOrgAsync("MAP")).ConnectLiveAsync();
        var k = o.K;
        var seedA = _f.Company.Seed(SyncKind.Customer, $"{k.Marker} QBO Customer A");
        var seedB = _f.Company.Seed(SyncKind.Customer, $"{k.Marker} QBO Customer B");
        _f.Company.Seed(SyncKind.Vendor, $"{k.Marker} QBO Vendor");
        var partnersBefore = await PartnerCountAsync(o.OrgId);

        // T-40 — import: remote-only candidates, nothing created in SCM (BR-18)
        var import = await k.Ok(k.Post($"{Qb}/matching/Customer/import"), "import QuickBooks customers");
        import.I("read").Should().BeGreaterThanOrEqualTo(2, J.Short(import));
        import.I("added").Should().BeGreaterThanOrEqualTo(2);
        (await PartnerCountAsync(o.OrgId)).Should().Be(partnersBefore, "BR-18: an import never creates SCM customers");
        (await _f.QueryAsync("SELECT COUNT(*) AS N FROM integration.RemoteEntities r JOIN integration.Connections c ON c.Id = r.ConnectionId " +
                             "WHERE c.OrganizationId = @o AND r.Kind = 'Customer'", ("@o", o.OrgId))).Single()["N"]
            .Should().Be(import.I("read"), "one row per QuickBooks customer");

        // T-41 — the unlinked list
        var unlinked = (await k.Ok(k.Get($"{Qb}/matching/Customer/remote?linked=false"), "unlinked")).A("data");
        unlinked.Select(r => r.S("remoteId")).Should().Contain([seedA.Id, seedB.Id]);

        // re-import is idempotent
        var again = await k.Ok(k.Post($"{Qb}/matching/Customer/import"), "import again");
        again.I("added").Should().Be(0, J.Short(again));

        // T-42 — link an SCM customer to a QuickBooks one: adopted, never re-created
        var customer = await k.CreateCustomerAsync("MAP Customer");
        var linked = await k.Ok(k.Post($"{Qb}/matching/Customer/remote/{seedA.Id}/link", new { externalId = customer.Uuid.ToString() }), "link");
        var map = await _f.MapAsync(o.OrgId, customer.Uuid, "Customer");
        (map.RemoteId, map.State).Should().Be((seedA.Id, "Synced"), map.ToString());
        (await k.Ok(k.Get($"{Qb}/matching/Customer/remote?linked=false"), "unlinked")).A("data")
            .Select(r => r.S("remoteId")).Should().NotContain(seedA.Id);
        (await k.Post($"{Qb}/matching/Customer/remote/{seedB.Id}/link", new { externalId = customer.Uuid.ToString() }))
            .Status.Should().BeOneOf(new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest }, "an SCM record links to one QuickBooks record");

        // T-43 — unlink refused while documents synced against it are open
        var (_, _, _, item) = await k.StockedWorldAsync("MAP");
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice")).State.Should().Be("Synced");
        var item0 = await SyncItemAsync(k, "Customer", customer.Uuid);
        var refused = await k.Post($"{Qb}/sync/items/{item0.G("id")}/unlink");
        refused.Status.Should().Be(HttpStatusCode.Conflict, refused.ToString());
        refused.Result.I("openDocuments").Should().BeGreaterThanOrEqualTo(1);

        // T-44 — unlink a customer without open documents: map cleared, QuickBooks record kept
        var other = await k.CreateCustomerAsync("MAP Other");
        await k.Ok(k.Post($"{Qb}/matching/Customer/remote/{seedB.Id}/link", new { externalId = other.Uuid.ToString() }), "link other");
        var otherItem = await SyncItemAsync(k, "Customer", other.Uuid);
        await k.Ok(k.Post($"{Qb}/sync/items/{otherItem.G("id")}/unlink"), "unlink");
        var cleared = await _f.MapAsync(o.OrgId, other.Uuid, "Customer");
        (cleared.RemoteId, cleared.State).Should().Be((null, "NotSynced"), cleared.ToString());
        _f.Company.Records(SyncKind.Customer).Should().Contain(r => r.Id == seedB.Id && r.Active, "unlink never touches QuickBooks");
        (await k.Ok(k.Get($"{Qb}/matching/Customer/remote?linked=false"), "unlinked")).A("data")
            .Select(r => r.S("remoteId")).Should().Contain(seedB.Id, "free to link again");
    }

    [Fact]
    public async Task Tax_import_saves_only_unambiguous_suggestions_and_the_validator_lists_every_unmapped_code()
    {
        _f.Company.ExtraTaxCodes.Add(new RemoteTaxCode("R5A", "Reduced 5% A", null, true, 5m, true));
        _f.Company.ExtraTaxCodes.Add(new RemoteTaxCode("R5B", "Reduced 5% B", null, true, 5m, true));
        _f.Company.ExtraTaxCodes.Add(new RemoteTaxCode("R8", "Rate 8%", null, true, 8m, true));
        try
        {
            var o = await (await _root.NewOrgAsync("TAX")).ConnectLiveAsync();
            var k = o.K;
            await k.Ok(k.Post($"{Qb}/reference/refresh"), "refresh reference data");
            var zero = await k.EnsureTaxCodeAsync("ZR0", 0m, "SALES", isDefault: false);
            var five = await k.EnsureTaxCodeAsync("GST5", 5m, "SALES", isDefault: false);
            var eight = await k.EnsureTaxCodeAsync("GST8", 8m, "SALES", isDefault: false);
            var twelve = await k.EnsureTaxCodeAsync("GST12", 12m, "SALES", isDefault: false);

            // T-45..T-47 — suggest by rate + name; 0 % → NON; ambiguous (two 5 % codes) and no-match (12 %) left for the admin
            var result = await k.Ok(k.Post($"{Qb}/tax-mappings/import"), "tax import");
            var saved = result.A("savedRows").ToDictionary(r => r.S("sourceTaxCode") ?? $"{r.D("taxPercent")}%", r => r.S("qboTaxCodeId"));
            saved.Should().Contain("ZR0", "NON").And.Contain("GST8", "R8");
            var review = result.A("needsReview").ToDictionary(r => r.S("sourceTaxCode") ?? $"{r.D("taxPercent")}%");
            review.Should().ContainKey("GST5");
            review["GST5"].A("candidates").Select(c => c.S("id")).Should().BeEquivalentTo("R5A", "R5B");
            review.Should().ContainKey("GST12");
            result.I("alreadyMapped").Should().BeGreaterThanOrEqualTo(1, "GST17 was mapped at connect");
            (await k.Ok(k.Get($"{Qb}/tax-mappings"), "tax mappings")).Items()
                .Should().Contain(m => m.S("sourceTaxCode") == "ZR0" && m.S("qboTaxCodeId") == "NON");

            // T-48 — import twice changes nothing
            (await k.Ok(k.Post($"{Qb}/tax-mappings/import"), "tax import again")).I("saved").Should().Be(0);

            // T-49 — the validator reports every unmapped code at once (all errors in one go)
            var (_, _, customer, item) = await k.StockedWorldAsync("TAX");
            var (_, _, inv) = await k.SellAsync(customer, o.O.Pkr, SapKit.Line(item, 1m, taxCode: five), SapKit.Line(item, 1m, taxCode: twelve));
            await _f.RunJobsAsync(3);
            var blocked = await _f.MapAsync(o.OrgId, inv, "SalesInvoice");
            (blocked.State, blocked.LastErrorCode).Should().Be(("Blocked", "TAX_UNMAPPED"), blocked.ToString());
            blocked.LastError.Should().Contain("GST5").And.Contain("GST12", "every unmapped code in one message");
            _ = (zero, eight);
        }
        finally
        {
            _f.Company.ExtraTaxCodes.Clear();
        }
    }

    [Fact]
    public async Task Accounts_auto_map_into_the_A42_mappings()
    {
        var o = await _root.NewOrgAsync("ACC");
        var k = o.K;
        var before = await k.Ok(k.Get($"{Qb}/accounts"), "accounts before the GL exists");
        before.B("glAvailable").Should().BeFalse();

        await k.CompleteGlSetupAsync();
        o = await o.ConnectLiveAsync();
        var view = await k.Ok(k.Get($"{Qb}/accounts"), "accounts");
        view.B("glAvailable").Should().BeTrue();
        view.A("qboAccounts").Select(a => a.S("id")).Should().Contain(["1", "2", "3", "4"]);
        view.I("mapped").Should().Be(0);
        view.I("totalAccounts").Should().Be(view.A("accounts").Count);

        // auto-map: AcctNum, exact name, normalised name — written as A42 mappings (QUICKBOOKS_ONLINE)
        var auto = await k.Ok(k.Post($"{Qb}/accounts/auto-map", new { overwrite = false }), "auto-map");
        var a42 = (await k.Ok(k.Get("/api/finance/external-mappings?systemType=QUICKBOOKS_ONLINE"), "A42 mappings")).Items();
        a42.Should().HaveCount(auto.I("mapped"), J.Short(auto));
        auto.A("matches").Should().OnlyContain(m => m.S("rule") == "AcctNum" || m.S("rule") == "ExactName" || m.S("rule") == "NormalizedName");

        // an explicit mapping through the batch PUT
        var leaf = view.A("accounts").First(a => a.S("status") == "Unmapped" && a.B("isActive"));
        var saved = await k.Ok(k.Put($"{Qb}/accounts", new { mappings = new[] { new { accountUuid = leaf.G("accountUuid"), qboAccountId = "3" } } }), "map one");
        saved.I("mapped").Should().Be(auto.I("mapped") + 1, J.Short(saved));
        (await k.Ok(k.Get("/api/finance/external-mappings?systemType=QUICKBOOKS_ONLINE"), "A42 mappings")).Items()
            .Should().Contain(m => m.P("account").S("accountCode") == leaf.S("code") && m.S("externalAccountCode") == "3");

        // an id QuickBooks does not have is refused
        (await k.Put($"{Qb}/accounts", new { mappings = new[] { new { accountUuid = leaf.G("accountUuid"), qboAccountId = "NOPE" } } }))
            .Status.Should().Be(HttpStatusCode.BadRequest);

        // coverage shows the accounts as information only (D-16)
        var coverage = await k.Ok(k.Get($"{Qb}/coverage"), "coverage");
        coverage.P("accounts").B("blocking").Should().BeFalse();
        coverage.P("accounts").I("mapped").Should().Be(saved.I("mapped"));
    }

    [Fact]
    public async Task Coverage_validation_and_the_dead_letter()
    {
        var fresh = await _root.NewOrgAsync("COV");
        var k = fresh.K;

        // T-50 — not connected: not ready, the blocking issue says so
        var c0 = await k.Ok(k.Get($"{Qb}/coverage"), "coverage before connecting");
        c0.B("ready").Should().BeFalse();
        c0.A("blockingIssues").Select(i => i.S("code")).Should().Contain("NOT_CONNECTED");

        var o = await fresh.ConnectLiveAsync();
        var (_, vendor, customer, item) = await k.StockedWorldAsync("COV");
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        await k.RecordPaymentAsync(customer, 5m, "CASH"); // D-68: deposit accounts are checked for the methods in use
        await _f.RunJobsAsync(4);

        // T-51 — per kind: what SCM sent vs mapped
        var c1 = await k.Ok(k.Get($"{Qb}/coverage"), "coverage");
        var customers = c1.A("entities").Single(e => e.S("kind") == "Customer");
        customers.I("mapped").Should().BeGreaterThanOrEqualTo(1, J.Short(customers));
        (customers.I("mapped") + customers.I("skipped") + customers.I("pending")).Should().Be(customers.I("total"));
        // T-52 — payments are on by default: their deposit accounts are named as an issue until set (D-27)
        var issues = c1.A("blockingIssues").Concat(c1.A("warnings")).Select(i => i.S("code")).ToList();
        issues.Should().Contain("DEPOSIT_ACCOUNTS", J.Short(c1));
        Console.WriteLine($"PROBE coverage issues: blocking={J.Short(c1.P("blockingIssues"))} warnings={J.Short(c1.P("warnings"))}");
        c1.A("blockingIssues").Concat(c1.A("warnings")).First(i => i.S("code") == "DEPOSIT_ACCOUNTS").S("message").Should().Contain("CASH");
        // the preflight carries the same check while it applies
        (await k.Ok(k.Get($"{Qb}/preflight"), "preflight")).A("checks").Should().Contain(c => c.S("code")!.Contains("DEPOSIT"));
        var (bank1, _) = await k.BankAccountsAsync();
        await k.Ok(k.PutDefaults(PaymentMethodCodesAll().Select(m => ($"DepositToAccountRef.{m}", (string?)bank1)).ToArray()), "deposit accounts");
        var c2 = await k.Ok(k.Get($"{Qb}/coverage"), "coverage");
        c2.A("blockingIssues").Concat(c2.A("warnings")).Select(i => i.S("code")).Should().NotContain("DEPOSIT_ACCOUNTS");

        // T-53 — validation/check: no Intuit call, the result of the stored payload
        _f.Company.ResetCalls();
        var ok = await k.Ok(k.Post($"{Qb}/validation/check", new { kind = "SalesInvoice", externalId = inv.G("uuid").ToString() }), "check synced");
        (ok.B("found"), ok.B("valid"), ok.S("state"), ok.S("documentType"), ok.S("syncControl")).Should().Be((true, true, "Synced", "SalesInvoice", "Process"));
        var missing = await k.Ok(k.Post($"{Qb}/validation/check", new { kind = "SalesInvoice", externalId = Guid.NewGuid().ToString() }), "check unknown");
        missing.B("found").Should().BeFalse();
        var twelve = await k.EnsureTaxCodeAsync("GST12", 12m, "SALES", isDefault: false);
        var (_, _, blockedInv) = await k.SellAsync(customer, o.O.Pkr, SapKit.Line(item, 1m, taxCode: twelve));
        await _f.RunJobsAsync(2);
        var bad = await k.Ok(k.Post($"{Qb}/validation/check", new { kind = "SalesInvoice", externalId = blockedInv.ToString() }), "check blocked");
        bad.B("valid").Should().BeFalse();
        bad.A("errors").Select(e => e.S("code")).Should().Contain("TAX_UNMAPPED");
        _f.Company.Calls.Where(c => c.Operation is "Create" or "Update" or "Void").Should().BeEmpty("validation never writes to QuickBooks");

        // T-54 — dead letter. A duplicate number is adopted (Find), and the fake cannot refuse, so two synced bills are put into
        // the dead-letter states by SQL (what exhausted retries / an unknown outcome leave behind): bulk retry re-queues the
        // Failed one and it syncs; skip dismisses the NeedsResolution one.
        await k.Ok(k.SetSyncControl("PurchaseBill", enabled: true), "bills on");
        var bill = await k.ApprovedBillAsync(vendor, 100m);
        var bill2 = await k.ApprovedBillAsync(vendor, 200m);
        await _f.RunJobsAsync(4);
        await _f.ExecuteAsync("UPDATE m SET State = 'Failed', LastErrorCode = 'QA_FORCED', LastError = 'forced by QA', LastPushedFingerprint = NULL " +
                              "FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId " +
                              "WHERE c.OrganizationId = @o AND m.ExternalId = @e", ("@o", o.OrgId), ("@e", bill.ToString()));
        await _f.ExecuteAsync("UPDATE m SET State = 'NeedsResolution', LastErrorCode = 'QA_FORCED', LastError = 'forced by QA' " +
                              "FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId " +
                              "WHERE c.OrganizationId = @o AND m.ExternalId = @e", ("@o", o.OrgId), ("@e", bill2.ToString()));

        var dead = await k.Ok(k.Get($"{Qb}/sync/items?kind=Bill&state=Failed&page=1&pageSize=50"), "Failed bills");
        dead.A("data").Select(i => i.S("externalId")).Should().Contain(bill.ToString());

        var billItem2 = await SyncItemAsync(k, "Bill", bill2);
        await k.Ok(k.Post($"{Qb}/sync/items/{billItem2.G("id")}/skip"), "skip (dismiss) the NeedsResolution bill");
        (await _f.MapAsync(o.OrgId, bill2, "Bill")).State.Should().NotBe("NeedsResolution", "dismissed from the dead letter");

        var retry = await k.Ok(k.Post($"{Qb}/sync/items/retry-failed", new { kind = "Bill" }), "retry failed bills");
        (retry.I("found"), retry.I("requeued")).Should().Be((1, 1), J.Short(retry));
        _f.Company.ResetCalls();
        await _f.RunJobsAsync(4);
        (await _f.MapAsync(o.OrgId, bill, "Bill")).State.Should().Be("Synced", "re-queued and sent again");
        _f.Company.Writes.Should().NotContain(w => w.Kind == SyncKind.Bill && w.Operation == "Create", "the existing bill is updated, never duplicated");
        (await k.Ok(k.Post($"{Qb}/sync/items/retry-failed", new { kind = "Bill" }), "retry again")).I("found").Should().Be(0);
    }

    private static IEnumerable<string> PaymentMethodCodesAll() => PaymentMethodCodes.All;
}
