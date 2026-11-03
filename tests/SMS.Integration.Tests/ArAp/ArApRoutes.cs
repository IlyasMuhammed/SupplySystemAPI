using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.ArAp;

/// <summary>A48 routes, bodies and D-14 codes exactly as docs/ar-ap-subledgers/API-CONTRACT.md states them.</summary>
internal static partial class Ar
{
    // ── D-14 codes (§0.2) ────────────────────────────────────────────────────────
    public const string AR_VIEW = "AR_VIEW", AR_STATEMENT_SEND = "AR_STATEMENT_SEND", AR_CREDIT_MEMO = "AR_CREDIT_MEMO",
        CREDIT_CHECK = "CREDIT_CHECK", CREDIT_MANAGE = "CREDIT_MANAGE", CREDIT_OVERRIDE = "CREDIT_OVERRIDE",
        DUNNING_EXECUTE = "DUNNING_EXECUTE", DUNNING_ADMIN = "DUNNING_ADMIN", AP_VIEW = "AP_VIEW", AP_EXECUTE = "AP_EXECUTE",
        AP_DEBIT_MEMO = "AP_DEBIT_MEMO", MATCH_EXECUTE = "MATCH_EXECUTE", MATCH_OVERRIDE = "MATCH_OVERRIDE", MATCH_APPROVE = "MATCH_APPROVE",
        PAYMENT_RUN_PROPOSE = "PAYMENT_RUN_PROPOSE", PAYMENT_RUN_EDIT = "PAYMENT_RUN_EDIT", PAYMENT_RUN_APPROVE = "PAYMENT_RUN_APPROVE",
        PAYMENT_RUN_EXECUTE = "PAYMENT_RUN_EXECUTE", PAYMENT_RUN_ADMIN = "PAYMENT_RUN_ADMIN", FINANCE_AR_AP_ADMIN = "FINANCE_AR_AP_ADMIN";

    public static readonly string[] D14 =
    [
        AR_VIEW, AR_STATEMENT_SEND, AR_CREDIT_MEMO, CREDIT_CHECK, CREDIT_MANAGE, CREDIT_OVERRIDE, DUNNING_EXECUTE, DUNNING_ADMIN,
        AP_VIEW, AP_EXECUTE, AP_DEBIT_MEMO, MATCH_EXECUTE, MATCH_OVERRIDE, MATCH_APPROVE, PAYMENT_RUN_PROPOSE, PAYMENT_RUN_EDIT,
        PAYMENT_RUN_APPROVE, PAYMENT_RUN_EXECUTE, PAYMENT_RUN_ADMIN, FINANCE_AR_AP_ADMIN
    ];

    // ── §1 profiles + settings ───────────────────────────────────────────────────
    public static Task<Api> Profile(this ArOrg o, Guid partner, string role = "Customer", HttpClient? c = null) =>
        o.K.Get($"/api/finance/partner-profiles/{partner}?role={role}", c);

    public static Task<JsonElement> ProfileAsync(this ArOrg o, Guid partner, string role = "Customer") =>
        o.K.Ok(o.Profile(partner, role), $"{role} profile");

    public static Task<Api> PutProfile(this ArOrg o, Guid partner, object body, string role = "Customer", HttpClient? c = null) =>
        o.K.Put($"/api/finance/partner-profiles/{partner}?role={role}", body, c);

    public static Task<Api> PutSettings(this ArOrg o, string mode, HttpClient? c = null) =>
        o.K.Put("/api/finance/ar-ap/settings", new { creditControlMode = mode }, c);

    public static async Task CreditModeAsync(this ArOrg o, string mode)
    {
        var r = await o.K.Ok(o.PutSettings(mode), $"credit control {mode}");
        r.S("creditControlMode").Should().Be(mode);
    }

    // ── §2 credit ────────────────────────────────────────────────────────────────
    public static string Credit(Guid c) => $"/api/finance/credit/{c}";
    public static Task<Api> Exposure(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"{Credit(c)}/exposure", h);
    public static Task<JsonElement> ExposureAsync(this ArOrg o, Guid c) => o.K.Ok(o.Exposure(c), "exposure");
    public static Task<Api> Check(this ArOrg o, Guid c, decimal amount, HttpClient? h = null) => o.K.Post($"{Credit(c)}/check", new { amount }, h);
    public static Task<Api> Hold(this ArOrg o, Guid c, string reason = "A48 QA hold", HttpClient? h = null) => o.K.Post($"{Credit(c)}/hold", new { reason }, h);
    public static Task<Api> Release(this ArOrg o, Guid c, string reason = "A48 QA release", HttpClient? h = null) => o.K.Post($"{Credit(c)}/release", new { reason }, h);
    public static Task<Api> TempLimit(this ArOrg o, Guid c, decimal? amount, DateTime expiry, string reason = "A48 QA temp", HttpClient? h = null) =>
        o.K.Post($"{Credit(c)}/temporary-limit", new { amount, expiryDate = Iso(expiry), reason }, h);
    public static Task<Api> RiskTier(this ArOrg o, Guid c, string tier, string reason = "A48 QA tier", HttpClient? h = null) =>
        o.K.Put($"{Credit(c)}/risk-tier", new { riskTier = tier, reason }, h);
    public static Task<Api> CreditHistory(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"{Credit(c)}/history?page=1&pageSize=100", h);

    public static async Task<List<JsonElement>> CreditHistoryAsync(this ArOrg o, Guid c) => Rows(await o.K.Ok(o.CreditHistory(c), "credit history"));

    public static Task<Api> ConfirmWithOverride(this ArOrg o, Guid so, string? reason, HttpClient? h = null) =>
        o.K.Post($"/api/sale-orders/{so}/confirm", new { creditOverrideReason = reason }, h);

    // ── §3 dunning ───────────────────────────────────────────────────────────────
    public const string Dunning = "/api/finance/dunning";

    public static object Level(int n, int days, decimal? charge = null, string action = "Email") => new
    {
        levelNumber = n, daysAfterPrevious = days, subjectTemplate = $"Reminder {n} for {{CustomerName}}",
        bodyTemplate = "Dear {CustomerName} ({CustomerCode}), {TotalOverdue} is overdue since {OldestDueDate}. Level {DunningLevel}.\n{InvoiceList}",
        includeInvoiceList = true, includeStatement = false, chargeAmount = charge, actionType = action, escalationNote = (string?)null
    };

    public static Task<Api> CreateProcedure(this ArOrg o, string code, int grace, decimal minimum, int? blockAt, object[] levels, bool isDefault = false,
        HttpClient? h = null) =>
        o.K.Post($"{Dunning}/procedures", new
        {
            code, name = $"QA {code}", description = "A48 QA", isDefault, isActive = true, graceDays = grace, minimumAmount = minimum,
            blockSalesAtLevel = blockAt, levels
        }, h);

    public static Task<Api> Procedures(this ArOrg o, HttpClient? h = null) => o.K.Get($"{Dunning}/procedures", h);
    public static Task<Api> Preview(this ArOrg o, DateTime? asOf = null, HttpClient? h = null) =>
        o.K.Post($"{Dunning}/runs/preview", new { asOfDate = Iso(asOf ?? Today) }, h);
    public static Task<Api> ExecuteDunning(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Post($"{Dunning}/runs/{run}/execute", null, h);
    public static Task<Api> CancelDunning(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Post($"{Dunning}/runs/{run}/cancel", null, h);
    public static Task<Api> DunningRun(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Get($"{Dunning}/runs/{run}", h);
    public static Task<Api> DunningHistory(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"{Dunning}/customers/{c}/history", h);
    public static Task<Api> DunningReset(this ArOrg o, Guid c, string reason = "A48 QA reset", int? level = null, HttpClient? h = null) =>
        o.K.Post($"{Dunning}/customers/{c}/reset", new { reason, level }, h);

    /// <summary>Assigns a procedure to the customer's profile (FINANCE_AR_AP_ADMIN).</summary>
    public static async Task AssignProcedureAsync(this ArOrg o, Guid customer, Guid procedure, bool blockSales = false) =>
        await o.K.Ok(o.PutProfile(customer, new { dunningProcedureUuid = procedure, blockSalesAtDunning = blockSales }), "assign dunning procedure");

    /// <summary>Puts the customer at a dunning level, last dunned <paramref name="daysAgo"/> days ago (what earlier runs would have left).</summary>
    public static async Task SetDunningStateAsync(this ArOrg o, Guid customer, int level, int daysAgo)
    {
        await o.ProfileAsync(customer); // get-or-create
        (await o.F.ExecuteAsync(
            "UPDATE finance.partner_financial_profiles SET CurrentDunningLevel = @l, LastDunningDate = @d WHERE OrganizationId = @o AND PartnerUuid = @p AND PartnerRole = 'Customer'",
            ("@l", level), ("@d", Today.AddDays(-daysAgo)), ("@o", o.OrgId), ("@p", customer))).Should().Be(1);
    }

    /// <summary>The run's item for a customer, or default when it has none.</summary>
    public static JsonElement ItemFor(this JsonElement run, Guid customer) =>
        run.A("items").FirstOrDefault(i => (i.Has("partnerUuid") ? i.NG("partnerUuid") : i.NG("customerUuid")) == customer);

    // ── §4 credit memos ──────────────────────────────────────────────────────────
    public static Task<Api> CreditMemo(this ArOrg o, Guid customer, decimal amount, Guid? original = null, string currency = "PKR", HttpClient? h = null) =>
        o.K.Post("/api/finance/ar/credit-memo", new
        {
            customerUuid = customer, originalInvoiceUuid = original, date = Iso(Today), currencyCode = currency, reason = "A48 QA credit",
            lines = new[] { new { description = "Price adjustment", amount, taxCodeUuid = (Guid?)null } }
        }, h);

    public static Task<Api> ApplyCreditMemo(this ArOrg o, Guid note, IEnumerable<(Guid Invoice, decimal Amount)> apps, HttpClient? h = null) =>
        o.K.Post($"/api/finance/ar/credit-memo/{note}/apply", new { applications = apps.Select(a => new { invoiceUuid = a.Invoice, amount = a.Amount }).ToArray() }, h);

    // ── §5 inquiry / aging / statements ──────────────────────────────────────────
    public static Task<Api> ArAging(this ArOrg o, string query = "", HttpClient? h = null) => o.K.Get($"/api/finance/ar/aging?{query}", h);
    public static Task<Api> ApAging(this ArOrg o, string query = "", HttpClient? h = null) => o.K.Get($"/api/finance/ap/aging?{query}", h);
    public static Task<Api> Dso(this ArOrg o, int periodDays, HttpClient? h = null) => o.K.Get($"/api/finance/ar/dso?periodDays={periodDays}", h);
    public static Task<Api> Dpo(this ArOrg o, int periodDays, HttpClient? h = null) => o.K.Get($"/api/finance/ap/dpo?periodDays={periodDays}", h);
    public static Task<Api> ArSummary(this ArOrg o, HttpClient? h = null) => o.K.Get("/api/finance/ar/summary", h);
    public static Task<Api> ApSummary(this ArOrg o, HttpClient? h = null) => o.K.Get("/api/finance/ap/summary", h);
    public static Task<Api> CustomerBalance(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"/api/finance/ar/customers/{c}/balance", h);
    public static Task<Api> CustomerOpenItems(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"/api/finance/ar/customers/{c}/open-items?page=1&pageSize=200", h);
    public static Task<Api> CustomerTransactions(this ArOrg o, Guid c, HttpClient? h = null) => o.K.Get($"/api/finance/ar/customers/{c}/transactions?page=1&pageSize=200", h);
    public static Task<Api> VendorBalance(this ArOrg o, Guid v, HttpClient? h = null) => o.K.Get($"/api/finance/ap/vendors/{v}/balance", h);
    public static Task<Api> VendorOpenItems(this ArOrg o, Guid v, HttpClient? h = null) => o.K.Get($"/api/finance/ap/vendors/{v}/open-items?page=1&pageSize=200", h);
    public static Task<Api> VendorTransactions(this ArOrg o, Guid v, HttpClient? h = null) => o.K.Get($"/api/finance/ap/vendors/{v}/transactions?page=1&pageSize=200", h);

    public static Task<Api> Statement(this ArOrg o, Guid c, DateTime from, DateTime to, string type = "Activity", HttpClient? h = null) =>
        o.K.Get($"/api/finance/ar/customers/{c}/statement?from={Iso(from)}&to={Iso(to)}&type={type}&format=json", h);

    public static Task<Api> EmailStatement(this ArOrg o, Guid c, DateTime from, DateTime to, string? emailTo = null, HttpClient? h = null) =>
        o.K.Post($"/api/finance/ar/customers/{c}/statement/email", new { from = Iso(from), to = Iso(to), type = "Activity", emailTo }, h);

    public static Task<Api> BatchStatements(this ArOrg o, DateTime from, DateTime to, decimal? min, bool email = false, HttpClient? h = null) =>
        o.K.Post("/api/finance/ar/statements/batch", new { from = Iso(from), to = Iso(to), type = "Activity", minimumBalance = min, sendEmail = email }, h);

    public static Task<Api> VendorReconciliation(this ArOrg o, Guid v, decimal claimed, object[]? items = null, HttpClient? h = null) =>
        o.K.Post($"/api/finance/ap/vendors/{v}/reconciliation", new { asOfDate = Iso(Today), vendorClaimedBalance = claimed, vendorItems = items }, h);

    /// <summary>The aging line of one partner (default when absent).</summary>
    public static JsonElement LineOf(this JsonElement aging, Guid partner) =>
        aging.A("lines").FirstOrDefault(l => l.G("partnerId") == partner);

    public static decimal[] Buckets(this JsonElement line) => line.A("bucketAmounts").Select(b => b.GetDecimal()).ToArray();

    // ── §6 matching ──────────────────────────────────────────────────────────────
    public const string Matching = "/api/finance/matching";
    public static Task<Api> Match(this ArOrg o, Guid invoice, string? type = null, HttpClient? h = null) =>
        o.K.Post($"{Matching}/invoices/{invoice}/match", new { matchType = type }, h);
    public static Task<Api> MatchDetail(this ArOrg o, Guid invoice, HttpClient? h = null) => o.K.Get($"{Matching}/invoices/{invoice}", h);
    public static Task<Api> Override(this ArOrg o, Guid invoice, string? reason, HttpClient? h = null) =>
        o.K.Post($"{Matching}/invoices/{invoice}/override", new { reason }, h);
    public static Task<Api> ApproveMatch(this ArOrg o, Guid invoice, HttpClient? h = null) => o.K.Post($"{Matching}/invoices/{invoice}/approve", null, h);
    public static Task<Api> Tolerances(this ArOrg o, HttpClient? h = null) => o.K.Get($"{Matching}/tolerances", h);

    public static Task<Api> CreateTolerance(this ArOrg o, string name, decimal pricePct, decimal qtyPct = 0m, bool autoApprove = false,
        Guid? vendor = null, decimal priceAmount = 0m, bool isDefault = false, HttpClient? h = null) =>
        o.K.Post($"{Matching}/tolerances", new
        {
            name, vendorUuid = vendor, priceTolerancePercent = pricePct, priceToleranceAmount = priceAmount, quantityTolerancePercent = qtyPct,
            totalTolerancePercent = 0m, totalToleranceAmount = 0m, autoApproveWithin = autoApprove, isDefault, isActive = true
        }, h);

    /// <summary>Makes the org's DEFAULT rule what the test needs (PUT on the seeded default).</summary>
    public static async Task<JsonElement> SetDefaultToleranceAsync(this ArOrg o, decimal pricePct, decimal qtyPct = 0m, bool autoApprove = false)
    {
        var rules = Rows(await o.K.Ok(o.Tolerances(), "tolerances"));
        var def = rules.Single(r => r.B("isDefault"));
        return await o.K.Ok(o.K.Put($"{Matching}/tolerances/{def.G("uuid")}", new
        {
            name = def.S("name"), vendorUuid = (Guid?)null, priceTolerancePercent = pricePct, priceToleranceAmount = 0m, quantityTolerancePercent = qtyPct,
            totalTolerancePercent = 0m, totalToleranceAmount = 0m, autoApproveWithin = autoApprove, isDefault = true, isActive = true
        }), "update default tolerance");
    }

    // ── §7 schedules / terms ─────────────────────────────────────────────────────
    public const string Schedules = "/api/finance/payment-schedules";
    public static Task<Api> ScheduleList(this ArOrg o, string query = "", HttpClient? h = null) => o.K.Get($"{Schedules}?page=1&pageSize=200&{query}", h);

    public static async Task<JsonElement> ScheduleOfAsync(this ArOrg o, Guid invoice)
    {
        var rows = Rows(await o.K.Ok(o.ScheduleList(), "payment schedules"));
        return rows.FirstOrDefault(r => r.G("invoiceUuid") == invoice);
    }

    public static Task<Api> Block(this ArOrg o, Guid schedule, string reason = "A48 QA block", HttpClient? h = null) =>
        o.K.Post($"{Schedules}/{schedule}/block", new { reason }, h);
    public static Task<Api> Unblock(this ArOrg o, Guid schedule, HttpClient? h = null) => o.K.Post($"{Schedules}/{schedule}/unblock", null, h);
    public static Task<Api> Discounts(this ArOrg o, int lookAhead = 30, HttpClient? h = null) =>
        o.K.Get($"{Schedules}/discounts?asOfDate={Iso(Today)}&lookAheadDays={lookAhead}", h);
    public static Task<Api> CashFlow(this ArOrg o, HttpClient? h = null) => o.K.Get($"{Schedules}/cash-flow?from={Iso(Today)}&weeks=8", h);
    public static Task<Api> Upcoming(this ArOrg o, HttpClient? h = null) => o.K.Get($"{Schedules}/upcoming?from={Iso(Today)}&to={Iso(Today.AddDays(60))}", h);

    public static Task<Api> CreateTerm(this ArOrg o, string code, decimal pct, int discountDays, int netDays, HttpClient? h = null) =>
        o.K.Post("/api/finance/early-payment-terms", new { code, name = $"{pct}/{discountDays} net {netDays}", discountPercent = pct, discountDays, netDays, isActive = true }, h);

    public static Task<Api> SetScheduleTerm(this ArOrg o, Guid schedule, Guid? term, HttpClient? h = null) =>
        o.K.Post($"{Schedules}/{schedule}/early-payment-term", new { earlyPaymentTermUuid = term }, h);

    // ── §8 payment runs ──────────────────────────────────────────────────────────
    public const string Runs = "/api/finance/payment-runs";
    public static Task<Api> Propose(this ArOrg o, DateTime cutoff, Guid? vendor = null, bool discounts = true, DateTime? paymentDate = null,
        string method = "BANK_TRANSFER", HttpClient? h = null) =>
        o.K.Post($"{Runs}/propose", new
        {
            paymentDate = Iso(paymentDate ?? Today), dueDateCutoff = Iso(cutoff), paymentMethod = method, vendorUuid = vendor, currencyCode = (string?)null,
            bankAccount = "QA-BANK-0001", includeDiscountItems = discounts
        }, h);
    public static Task<Api> Run(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Get($"{Runs}/{run}", h);
    public static Task<JsonElement> RunAsync(this ArOrg o, Guid run) => o.K.Ok(o.Run(run), "payment run");
    public static Task<Api> RemoveItem(this ArOrg o, Guid run, Guid item, string? reason = "A48 QA remove", HttpClient? h = null) =>
        o.K.Post($"{Runs}/{run}/items/{item}/remove", new { reason }, h);
    public static Task<Api> ApproveRun(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Post($"{Runs}/{run}/approve", null, h);
    public static Task<Api> ExecuteRun(this ArOrg o, Guid run, HttpClient? h = null) => o.K.Post($"{Runs}/{run}/execute", null, h);
    public static Task<Api> CancelRun(this ArOrg o, Guid run, string reason = "A48 QA cancel", HttpClient? h = null) => o.K.Post($"{Runs}/{run}/cancel", new { reason }, h);

    /// <summary>Every item of a run detail, wherever the contract nests them (flat <c>items</c> or under each batch).</summary>
    public static List<JsonElement> RunItems(this JsonElement run) =>
        run.Has("items") && run.P("items").ValueKind == JsonValueKind.Array
            ? run.A("items")
            : RunBatches(run).SelectMany(b => b.A("items")).ToList();

    public static List<JsonElement> RunBatches(this JsonElement run) =>
        run.A(run.Has("vendorGroups") ? "vendorGroups" : run.Has("batches") ? "batches" : "groups");

    /// <summary>The run header (PaymentRunDetailModel nests it as <c>run</c>).</summary>
    public static JsonElement Head(this JsonElement run) => run.Has("run") && run.P("run").ValueKind == JsonValueKind.Object ? run.P("run") : run;

    public static Guid RunUuid(this JsonElement run) => run.Head().G("uuid");
    public static string RunStatus(this JsonElement run) => run.Head().S("status")!;

    // ── §9 debit memos ───────────────────────────────────────────────────────────
    public static Task<Api> DebitMemo(this ArOrg o, Guid vendor, decimal amount, Guid? original = null, HttpClient? h = null) =>
        o.K.Post("/api/finance/ap/debit-memo", new
        {
            vendorUuid = vendor, originalInvoiceUuid = original, date = Iso(Today), reason = "PRICE_DIFFERENCE", reasonDetail = "A48 QA", amount, notes = "A48 QA"
        }, h);

    public static Task<Api> ApplyDebitMemo(this ArOrg o, Guid note, IEnumerable<(Guid Invoice, decimal Amount)> apps, HttpClient? h = null) =>
        o.K.Post($"/api/finance/ap/debit-memo/{note}/apply", new { applications = apps.Select(a => new { invoiceUuid = a.Invoice, amount = a.Amount }).ToArray() }, h);

    // ── assertions ───────────────────────────────────────────────────────────────

    /// <summary>A refusal: the status and the message (contained, case-insensitive, in message or result.errors).</summary>
    public static void ShouldRefuse(this Api api, HttpStatusCode status, string message, string because) =>
        SMS.Integration.Tests.JournalEngine.Je.ShouldRefuse(api, status, null, message, because);

    public static Guid UuidOf(this JsonElement r) =>
        r.ValueKind == JsonValueKind.String ? r.GetGuid() : r.G(r.Has("uuid") ? "uuid" : r.Has("invoiceUuid") ? "invoiceUuid" : "id");
}
