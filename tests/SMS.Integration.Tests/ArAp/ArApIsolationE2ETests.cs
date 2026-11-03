using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using Xunit.Abstractions;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — multi-tenant isolation (task 596): Org B's administrator (every A48 code) reaches none of Org A's AR/AP data.
/// Reads by A's ids are 404 (contract §0: another organization's row is 404), lists and totals hold B's rows only, writes on
/// A's ids are 404 and leave A unchanged, and B's runs never pick A's items.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApIsolationE2ETests</c>.
/// </summary>
public sealed class ArApIsolationE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;
    private readonly ITestOutputHelper _out;

    public ArApIsolationE2ETests(SapWebApplicationFactory factory, ITestOutputHelper output)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48ISO");
        _out = output;
    }

    [Fact]
    public async Task Org_B_sees_and_changes_nothing_of_org_A()
    {
        // ── Org A: receivables, payables, a procedure, a run, a credit memo ─────────
        var a = await _root.NewArOrgAsync("ISOA", stock: 10m);
        var aInv = await a.IssuedInvoiceAsync(a.Customer, 3m);                // 300
        await a.SetCreditLimitAsync(a.Customer, 5_000m);
        var aBill = await a.ApprovedBillAsync(a.Vendor, 700m, 2);
        var aSchedule = (await a.ScheduleOfAsync(aBill)).G("uuid");
        var aProc = await a.K.Ok(a.CreateProcedure("ISOAPROC", 0, 0m, null, [Level(1, 7)]), "A procedure");
        var aRun = (await a.K.Ok(a.Propose(Today.AddDays(5)), "A run")).RunUuid();
        var aNote = (await a.K.Ok(a.CreditMemo(a.Customer.Uuid, 50m), "A credit memo")).UuidOf();
        var aTerm = await a.K.Ok(a.CreateTerm("ISOTERM", 1m, 5, 30), "A term");

        // ── Org B: a little of its own ─────────────────────────────────────────────
        var b = await _root.NewArOrgAsync("ISOB", stock: 10m);
        await b.IssuedInvoiceAsync(b.Customer, 1m);                           // 100
        var bBill = await b.ApprovedBillAsync(b.Vendor, 40m, 2);
        var k = b.K;

        var leaks = new List<string>();
        void Expect404(string what, Api r)
        {
            if (r.Status != HttpStatusCode.NotFound) leaks.Add($"{what}: expected 404 — {r}");
        }

        // Reads by A's ids.
        Expect404("exposure", await b.Exposure(a.Customer.Uuid));
        Expect404("customer profile", await b.Profile(a.Customer.Uuid, "Customer"));
        Expect404("vendor profile", await b.Profile(a.Vendor.Uuid, "Vendor"));
        Expect404("credit history", await b.CreditHistory(a.Customer.Uuid));
        Expect404("customer balance", await b.CustomerBalance(a.Customer.Uuid));
        Expect404("customer open items", await b.CustomerOpenItems(a.Customer.Uuid));
        Expect404("customer transactions", await b.CustomerTransactions(a.Customer.Uuid));
        Expect404("customer statement", await b.Statement(a.Customer.Uuid, Today.AddDays(-30), Today));
        Expect404("vendor balance", await b.VendorBalance(a.Vendor.Uuid));
        Expect404("vendor open items", await b.VendorOpenItems(a.Vendor.Uuid));
        Expect404("match detail", await b.MatchDetail(aBill));
        Expect404("payment run", await b.Run(aRun));
        Expect404("dunning procedure", await k.Get($"{Dunning}/procedures/{aProc.G("uuid")}"));
        Expect404("dunning history", await b.DunningHistory(a.Customer.Uuid));
        Expect404("credit memo applications", await k.Get($"/api/finance/ar/credit-memo/{aNote}/applications"));

        // Writes on A's ids.
        Expect404("hold", await b.Hold(a.Customer.Uuid, "iso"));
        Expect404("temporary limit", await b.TempLimit(a.Customer.Uuid, 9_000m, Today.AddDays(5)));
        Expect404("risk tier", await b.RiskTier(a.Customer.Uuid, "High"));
        Expect404("credit check", await b.Check(a.Customer.Uuid, 1m));
        Expect404("dunning reset", await b.DunningReset(a.Customer.Uuid));
        Expect404("profile put", await b.PutProfile(a.Customer.Uuid, new { notes = "iso" }));
        Expect404("match", await b.Match(aBill));
        Expect404("override", await b.Override(aBill, "iso"));
        Expect404("approve match", await b.ApproveMatch(aBill));
        Expect404("block schedule", await b.Block(aSchedule));
        Expect404("schedule term", await b.SetScheduleTerm(aSchedule, null));
        Expect404("approve run", await b.ApproveRun(aRun));
        Expect404("execute run", await b.ExecuteRun(aRun));
        Expect404("cancel run", await b.CancelRun(aRun));
        Expect404("apply A's credit memo", await b.ApplyCreditMemo(aNote, [(aInv, 10m)]));
        Expect404("credit memo for A's customer", await b.CreditMemo(a.Customer.Uuid, 10m));
        Expect404("debit memo for A's vendor", await b.DebitMemo(a.Vendor.Uuid, 10m));
        Expect404("reconcile A's vendor", await b.VendorReconciliation(a.Vendor.Uuid, 0m));
        Expect404("email A's statement", await b.EmailStatement(a.Customer.Uuid, Today.AddDays(-30), Today, "iso@a48-qa.test"));
        Expect404("update A's term", await k.Put($"/api/finance/early-payment-terms/{aTerm.G("uuid")}", new { code = "ISOTERM", name = "x", discountPercent = 2m, discountDays = 5, netDays = 30, isActive = true }));

        // Lists and totals are B's only.
        var arAging = await k.Ok(b.ArAging(), "B AR aging");
        if (arAging.A("lines").Any(l => l.G("partnerId") == a.Customer.Uuid)) leaks.Add("B's AR aging lists A's customer");
        if (arAging.P("totals").D("grandTotal") != 100m) leaks.Add($"B's AR total is {arAging.P("totals").D("grandTotal")}, not 100");
        var apAging = await k.Ok(b.ApAging(), "B AP aging");
        if (apAging.A("lines").Any(l => l.G("partnerId") == a.Vendor.Uuid)) leaks.Add("B's AP aging lists A's vendor");
        var schedules = Rows(await k.Ok(b.ScheduleList(), "B schedules"));
        if (schedules.Any(s => s.G("invoiceUuid") == aBill)) leaks.Add("B's schedules hold A's bill");
        if (Rows(await k.Ok(b.Procedures(), "B procedures")).Any(p => p.S("code") == "ISOAPROC")) leaks.Add("B sees A's procedure");
        if (Rows(await k.Ok(k.Get($"{Runs}?page=1&pageSize=200"), "B runs")).Any(r => r.RunUuid() == aRun)) leaks.Add("B lists A's run");
        if (Rows(await k.Ok(k.Get("/api/finance/early-payment-terms"), "B terms")).Any(t => t.S("code") == "ISOTERM")) leaks.Add("B sees A's term");
        if (Rows(await k.Ok(k.Get("/api/finance/partner-profiles?role=Customer&page=1&pageSize=200"), "B profiles")).Any(p => p.G("partnerUuid") == a.Customer.Uuid))
            leaks.Add("B lists A's profile");
        if (Rows(await k.Ok(k.Get("/api/finance/matching/pending?page=1&pageSize=200"), "B pending matches")).Any(m => m.G("invoiceUuid") == aBill))
            leaks.Add("B's pending matches hold A's bill");

        // B's own runs never reach A.
        var bRun = await k.Ok(b.Propose(Today.AddDays(5)), "B run");
        if (bRun.RunItems().Any(i => i.G("invoiceUuid") == aBill)) leaks.Add("B's run proposed A's bill");
        bRun.RunItems().Select(i => i.G("invoiceUuid")).Should().Contain(bBill);
        var bDun = await b.Preview();
        if (bDun.Status == HttpStatusCode.OK && bDun.Result.A("items").Any(i => J.Short(i).Contains(a.Customer.Uuid.ToString())))
            leaks.Add("B's dunning run picked A's customer");

        foreach (var l in leaks) _out.WriteLine(l);
        Log("isolation.txt", $"{leaks.Count} leaks{Environment.NewLine}{string.Join(Environment.NewLine, leaks)}");
        leaks.Should().BeEmpty("task 596: Org A data is not visible to Org B");

        // A is unchanged.
        var ap = await a.ProfileAsync(a.Customer.Uuid);
        ap.B("creditHold").Should().BeFalse();
        ap.S("riskTier").Should().Be("Medium");
        (await a.RunAsync(aRun)).RunStatus().Should().Be("Proposed");
        (await a.ScheduleOfAsync(aBill)).B("paymentBlock").Should().BeFalse();
        (await a.SalesBalanceAsync(aInv)).Should().Be(300m);
    }
}
