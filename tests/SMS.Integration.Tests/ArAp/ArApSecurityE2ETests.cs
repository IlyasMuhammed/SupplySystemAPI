using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using Xunit.Abstractions;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — D-14 / API-CONTRACT §0.2: each permission code allows exactly its actions. One user per code (a custom role
/// holding only that code); every A48 action is called by every user; the answer must be 403 exactly when the code is not one
/// the contract lists for the route (any other status — 200, 400, 404, 409 — means the permission let the call through).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApSecurityE2ETests</c>.
/// </summary>
public sealed class ArApSecurityE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;
    private readonly ITestOutputHelper _out;

    public ArApSecurityE2ETests(SapWebApplicationFactory factory, ITestOutputHelper output)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48SEC");
        _out = output;
    }

    private sealed record Action(string Name, string[] Allowed, Func<HttpClient, Task<Api>> Call);

    private static readonly string[] RunCodes = [PAYMENT_RUN_PROPOSE, PAYMENT_RUN_EDIT, PAYMENT_RUN_APPROVE, PAYMENT_RUN_EXECUTE, PAYMENT_RUN_ADMIN];

    private static List<Action> Actions(ArOrg o, Guid invoice)
    {
        var c = o.Customer.Uuid;
        var v = o.Vendor.Uuid;
        var nothing = Guid.NewGuid();
        var from = Iso(Today.AddDays(-30));
        var to = Iso(Today);
        return
        [
            new("AR aging", [AR_VIEW], h => o.ArAging("", h)),
            new("AR summary", [AR_VIEW], h => o.ArSummary(h)),
            new("AR DSO", [AR_VIEW], h => o.Dso(90, h)),
            new("customer balance", [AR_VIEW], h => o.CustomerBalance(c, h)),
            new("customer statement", [AR_VIEW], h => o.Statement(c, Today.AddDays(-30), Today, h: h)),
            new("exposure", [AR_VIEW, CREDIT_CHECK, CREDIT_MANAGE], h => o.Exposure(c, h)),
            new("credit check", [CREDIT_CHECK], h => o.Check(c, 10m, h)),
            new("hold", [CREDIT_MANAGE], h => o.Hold(c, "sec", h)),
            new("risk tier", [CREDIT_MANAGE], h => o.RiskTier(c, "Low", "sec", h)),
            new("credit history", [AR_VIEW, CREDIT_MANAGE], h => o.CreditHistory(c, h)),
            new("statement email", [AR_STATEMENT_SEND], h => o.EmailStatement(c, Today.AddDays(-30), Today, "sec@a48-qa.test", h)),
            new("statement batch", [AR_STATEMENT_SEND], h => o.BatchStatements(Today.AddDays(-30), Today, 1_000_000m, false, h)),
            new("credit memo", [AR_CREDIT_MEMO], h => o.CreditMemo(c, 1m, h: h)),
            new("dunning preview", [DUNNING_EXECUTE], h => o.Preview(Today, h)),
            new("dunning runs", [AR_VIEW, DUNNING_EXECUTE, DUNNING_ADMIN], h => o.K.Get($"{Dunning}/runs?page=1", h)),
            new("dunning reset", [DUNNING_ADMIN], h => o.DunningReset(c, "sec", null, h)),
            new("dunning procedure create", [FINANCE_AR_AP_ADMIN], h => o.CreateProcedure($"S{Guid.NewGuid():N}"[..8].ToUpperInvariant(), 0, 0m, null, [Level(1, 7)], false, h)),
            new("dunning procedures", [AR_VIEW, DUNNING_EXECUTE, DUNNING_ADMIN, FINANCE_AR_AP_ADMIN], h => o.Procedures(h)),
            new("AP aging", [AP_VIEW], h => o.ApAging("", h)),
            new("AP summary", [AP_VIEW], h => o.ApSummary(h)),
            new("AP DPO", [AP_VIEW], h => o.Dpo(90, h)),
            new("vendor balance", [AP_VIEW], h => o.VendorBalance(v, h)),
            new("vendor reconciliation", [AP_EXECUTE], h => o.VendorReconciliation(v, 0m, null, h)),
            new("schedule block", [AP_EXECUTE], h => o.Block(nothing, "sec", h)),
            new("schedules list", [AP_VIEW, AP_EXECUTE, .. RunCodes], h => o.ScheduleList("", h)),
            new("debit memo", [AP_DEBIT_MEMO], h => o.DebitMemo(v, 1m, h: h)),
            new("match", [MATCH_EXECUTE], h => o.Match(invoice, null, h)),
            new("match detail", [AP_VIEW, MATCH_EXECUTE, MATCH_OVERRIDE, MATCH_APPROVE], h => o.MatchDetail(invoice, h)),
            new("override", [MATCH_OVERRIDE], h => o.Override(invoice, "sec", h)),
            new("approve match", [MATCH_APPROVE], h => o.ApproveMatch(nothing, h)),
            new("tolerance create", [FINANCE_AR_AP_ADMIN], h => o.CreateTolerance($"Sec {Guid.NewGuid():N}"[..16], 1m, h: h)),
            new("propose", [PAYMENT_RUN_PROPOSE], h => o.Propose(Today.AddDays(-400), h: h)),
            new("runs list", [AP_VIEW, .. RunCodes], h => o.K.Get($"{Runs}?page=1", h)),
            new("remove item", [PAYMENT_RUN_EDIT], h => o.RemoveItem(nothing, nothing, "sec", h)),
            new("approve run", [PAYMENT_RUN_APPROVE], h => o.ApproveRun(nothing, h)),
            new("execute run", [PAYMENT_RUN_EXECUTE], h => o.ExecuteRun(nothing, h)),
            new("export run", [PAYMENT_RUN_EXECUTE], h => o.K.Get($"{Runs}/{nothing}/export?format=csv", h)),
            new("cancel run", [PAYMENT_RUN_ADMIN], h => o.CancelRun(nothing, "sec", h)),
            new("early term create", [FINANCE_AR_AP_ADMIN], h => o.CreateTerm($"T{Guid.NewGuid():N}"[..8].ToUpperInvariant(), 1m, 5, 30, h)),
            new("settings put", [FINANCE_AR_AP_ADMIN], h => o.PutSettings("Off", h)),
            new("profile put", [FINANCE_AR_AP_ADMIN], h => o.PutProfile(c, new { notes = "sec" }, "Customer", h)),
            new("customer profile get", [AR_VIEW, CREDIT_CHECK, CREDIT_MANAGE, FINANCE_AR_AP_ADMIN], h => o.Profile(c, "Customer", h)),
            new("vendor profile get", [AP_VIEW, AP_EXECUTE, FINANCE_AR_AP_ADMIN], h => o.Profile(v, "Vendor", h)),
        ];
    }

    [Fact]
    public async Task Each_D14_code_allows_exactly_its_actions()
    {
        var o = await _root.NewArOrgAsync("SEC", stock: 5m);
        await o.IssuedInvoiceAsync(o.Customer, 1m);
        await o.SetCreditLimitAsync(o.Customer, 1_000_000m);
        var (po, poLine) = await o.OrderedAsync(o.Vendor, 2m, 10m);
        var (grn, grnLine) = await o.ReceivedAsync(po, 2m);
        var invoice = await o.SupplierInvoiceAsync(o.Vendor, $"{o.K.Marker}-SEC", po, poLine, grn, grnLine, 2m, 11m);
        var actions = Actions(o, invoice);

        var users = new Dictionary<string, HttpClient>();
        foreach (var code in D14)
        {
            var tag = code.ToLowerInvariant().Replace("_", "");
            users[code] = await o.UserWithAsync(tag[..Math.Min(12, tag.Length)], code);
        }

        var wrong = new List<string>();
        foreach (var a in actions)
            foreach (var (code, client) in users)
            {
                var r = await a.Call(client);
                var forbidden = r.Status == HttpStatusCode.Forbidden;
                var shouldPass = a.Allowed.Contains(code);
                if (forbidden == shouldPass || r.Status == HttpStatusCode.InternalServerError || r.Status == HttpStatusCode.Unauthorized)
                    wrong.Add($"{a.Name} as {code}: {(shouldPass ? "expected allowed" : "expected 403")} — {r}");
            }
        foreach (var w in wrong) _out.WriteLine(w);
        Log("security.txt", $"matrix: {wrong.Count} wrong of {actions.Count * users.Count}{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}");
        wrong.Should().BeEmpty("D-14: each code allows exactly its actions");
    }

    [Fact]
    public async Task No_code_is_403_everywhere_admin_passes_everywhere_anonymous_is_401()
    {
        var o = await _root.NewArOrgAsync("SEC0", stock: 5m);
        var actions = Actions(o, Guid.NewGuid());
        var nobody = await o.UserWithAsync("noarap", "SALE_ORDER_VIEW");
        using var anonymous = _f.CreateAnonymousClient();
        var wrong = new List<string>();
        foreach (var a in actions)
        {
            var n = await a.Call(nobody);
            if (n.Status != HttpStatusCode.Forbidden) wrong.Add($"{a.Name} as nobody: {n}");
            var adm = await a.Call(o.K.Admin);
            if (adm.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.InternalServerError)
                wrong.Add($"{a.Name} as org admin: {adm}");
            var anon = await a.Call(anonymous);
            if (anon.Status != HttpStatusCode.Unauthorized) wrong.Add($"{a.Name} anonymous: {anon}");
        }
        foreach (var w in wrong) _out.WriteLine(w);
        wrong.Should().BeEmpty("§0.2: Organization Admin has every A48 code; a user without any has none");
    }
}
