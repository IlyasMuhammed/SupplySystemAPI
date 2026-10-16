using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.QuickBooks;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Jobs;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>One A44 organization: the org, its admin's kit and (when connected) its QuickBooks realm.</summary>
internal sealed record FpOrg(ModOrg O, string? Realm)
{
    public SapKit K => O.K;
    public Guid OrgId => O.OrgId;
    public SapWebApplicationFactory F => O.K.F;
}

/// <summary>A journal entry of a source document, with its lines as "accountCode:debit:credit".</summary>
internal sealed record Journal(int Id, string Number, string Type, string Status, DateTime Date, decimal Debit, decimal Credit,
    string? Reference, string? Key, List<(string Code, decimal Debit, decimal Credit)> Lines)
{
    public bool Balanced => Math.Abs(Lines.Sum(l => l.Debit) - Lines.Sum(l => l.Credit)) <= 0.001m;
    public decimal Net(string code) => Lines.Where(l => l.Code == code).Sum(l => l.Debit - l.Credit);
    public override string ToString() =>
        $"{Number} {Type} {Status} {Date:yyyy-MM-dd} key={Key} [{string.Join(", ", Lines.Select(l => $"{l.Code}:{l.Debit:0.##}:{l.Credit:0.##}"))}]";
}

/// <summary>An integration.EntityMaps row.</summary>
internal sealed record MapRow(string Kind, string State, string? RemoteId, string? LastErrorCode, string? LastError, string? Warning, string? PayloadJson)
{
    public override string ToString() => $"{Kind} {State} remote={RemoteId} {LastErrorCode}: {LastError} warn={Warning}";
}

/// <summary>
/// A44 (QA) — what the FinancialPosting E2E classes share on top of <see cref="SapKit"/>, <see cref="Mods"/>, <see cref="Gl"/> and
/// the A35 kit: a GL-ready organization, the posting config, a QuickBooks connection per organization (each its own realm, so the
/// shared fake company never mixes them up), the jobs, the in-process gateway, and SQL / fake reads. Routes and JSON follow
/// docs/financial-posting/API-CONTRACT.md; property names are read case-insensitively.
/// </summary>
internal static class Fp
{
    public const string Qb      = "/api/integrations/quickbooks";
    public const string Posting = "/api/finance/posting";

    private static int _realmSeq;

    public static DateTime Today => DateTime.UtcNow.Date;

    // ── Organizations ────────────────────────────────────────────────────────────

    /// <summary>A new ENTERPRISE organization (PKR base, GST17/PGST17, approver placeholders) — nothing of A44 switched on.</summary>
    public static async Task<FpOrg> NewOrgAsync(this SapKit root, string prefix)
    {
        var o = await root.OrgAsync(prefix);
        await o.K.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await o.K.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await o.K.CreateApproverPlaceholdersAsync();
        return new FpOrg(o, null);
    }

    /// <summary>A42 setup to Complete: US_GENERAL, the fiscal year <see cref="Gl.Y"/>, opening balances skipped, complete.</summary>
    public static async Task CompleteGlSetupAsync(this SapKit k)
    {
        await k.ApplyAsync();
        await k.CreateYearOk(Gl.Y);
        await k.Ok(k.PostOpening(skip: true), "skip opening balances");
        var done = await k.Ok(k.Post("/api/finance/setup/complete"), "complete the GL setup");
        done.S("status").Should().Be("Complete", J.Short(done));
    }

    public static Task<Api> GetConfig(this SapKit k, HttpClient? c = null) => k.Get($"{Posting}/config", c);

    public static Task<Api> PutConfig(this SapKit k, bool enabled, bool autoPost = true, HttpClient? c = null) =>
        k.Put($"{Posting}/config", new { internalAccountingEnabled = enabled, autoPostToGL = autoPost }, c);

    public static async Task<List<JsonElement>> SyncControlAllAsync(this SapKit k, HttpClient? c = null) =>
        (await k.Ok(k.Get($"{Qb}/sync-control", c), "sync control")).A("rows");

    /// <summary>GL setup Complete and InternalAccountingEnabled on (D-8).</summary>
    public static async Task EnableGlAsync(this SapKit k, bool autoPost = true)
    {
        await k.CompleteGlSetupAsync();
        var cfg = await k.Ok(k.PutConfig(true, autoPost), "enable internal accounting");
        cfg.B("internalAccountingEnabled").Should().BeTrue(J.Short(cfg));
    }

    // ── QuickBooks per organization ──────────────────────────────────────────────

    public static string NextRealm() => $"9130{Interlocked.Increment(ref _realmSeq) + Environment.ProcessId % 1000 * 1000:D12}";

    /// <summary>Connect (fake Intuit), settings with the fake's four accounts, tax by rate, matching confirmed, Live.</summary>
    public static async Task<FpOrg> ConnectLiveAsync(this FpOrg o)
    {
        var realm = NextRealm();
        var k = o.K;
        var consent = await k.Ok(k.Post($"{Qb}/connect"), "connect");
        var state = Uri.UnescapeDataString(new Uri(consent.S("consentUrl")!).Query.TrimStart('?').Split('&')
            .Single(p => p.StartsWith("state=")).Substring("state=".Length));
        using (var anonymous = o.F.CreateAnonymousClient())
        {
            var cb = await anonymous.GetAsync($"{Qb}/callback?code=fp-{realm}&state={Uri.EscapeDataString(state)}&realmId={realm}");
            cb.Headers.Location!.ToString().Should().EndWith("result=connected");
        }

        await k.Ok(k.Put($"{Qb}/settings", new
        {
            DefaultIncomeAccountId = "1", DefaultExpenseAccountId = "3", FreightExpenseAccountId = "4", DiscountAccountId = "2",
            DefaultPurchaseTaxCodeId = "NON", ItemTypeDefault = "NonInventory", PartnerScope = "OnlyWhenReferenced"
        }), "QuickBooks settings");
        await k.Ok(k.Put($"{Qb}/tax-mappings", new
        {
            Mappings = new object[]
            {
                new { SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = "PGST17", TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = (string?)null, TaxPercent = 17m, QboTaxCodeId = "STD" },
                new { SourceTaxCode = (string?)null, TaxPercent = 0m, QboTaxCodeId = "NON" }
            }
        }), "tax mappings");
        await k.Ok(k.Post($"{Qb}/matching/complete", new { Confirmed = true }), "matching complete");
        var live = await k.Post($"{Qb}/mode", new { Mode = "Live" });
        if (live.Status != HttpStatusCode.OK)
        {
            var preflight = await k.Get($"{Qb}/preflight");
            throw new InvalidOperationException($"Live refused — {live}\npreflight: {preflight}");
        }
        return o with { Realm = realm };
    }

    // ── Sync control / defaults (CTRL, API-CONTRACT §4) ──────────────────────────

    public static Task<Api> SetSyncControl(this SapKit k, string documentType, bool enabled, bool hold = false, bool confirmRelease = true,
        HttpClient? c = null) =>
        k.Put($"{Qb}/sync-control/{documentType}", new { isEnabled = enabled, holdWhenDisabled = hold, confirmRelease }, c);

    public static async Task<List<JsonElement>> SyncControlRowsAsync(this SapKit k, HttpClient? c = null)
    {
        var r = await k.Ok(k.Get($"{Qb}/sync-control", c), "sync control");
        return r.ValueKind == JsonValueKind.Array ? r.Items() : r.A(r.Has("rows") ? "rows" : "items");
    }

    public static async Task<JsonElement> SyncControlRowAsync(this SapKit k, string documentType) =>
        (await k.SyncControlRowsAsync()).Single(r => string.Equals(r.S("documentType"), documentType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Provider defaults (SaveProviderDefaultsRequest: { values: [{ settingKey, settingValue }] }).</summary>
    public static Task<Api> PutDefaults(this SapKit k, params (string Key, string? Value)[] items) =>
        k.Put($"{Qb}/provider-defaults", new { values = items.Select(i => new { settingKey = i.Key, settingValue = i.Value }).ToArray() });

    /// <summary>Document overrides (SaveDocumentOverridesRequest: { overrides: [{ documentType, settingKey, settingValue }] }).</summary>
    public static Task<Api> PutOverrides(this SapKit k, params (string DocumentType, string Key, string? Value)[] items) =>
        k.Put($"{Qb}/document-overrides", new
        {
            overrides = items.Select(i => new { documentType = i.DocumentType, settingKey = i.Key, settingValue = i.Value }).ToArray()
        });

    /// <summary>QuickBooks accounts of a type (Bank, Credit Card, Income, Expense…) from GET reference, as (id, name).</summary>
    public static async Task<List<(string Id, string Name)>> QboAccountsAsync(this SapKit k, string type)
    {
        var reference = await k.Ok(k.Get($"{Qb}/reference"), "reference data");
        var accounts = reference.A("accounts");
        return accounts.Where(a => string.Equals((a.Has("accountType") ? a.S("accountType") : a.S("type"))?.Replace(" ", ""), type.Replace(" ", ""),
                StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.S("id")!, a.S("name")!)).ToList();
    }

    /// <summary>The first two Bank accounts the (fake) company has — FakeQuickBooks (QBO) must report some for payments.</summary>
    public static async Task<(string First, string Second)> BankAccountsAsync(this SapKit k)
    {
        var banks = await k.QboAccountsAsync("Bank");
        banks.Should().HaveCountGreaterThanOrEqualTo(2,
            "FakeQuickBooksCompany must report at least two Bank accounts for deposit / bank defaults (QBO owns the fake)");
        return (banks[0].Id, banks[1].Id);
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────────

    /// <summary>DependencyJob + SyncOutboxJob + the Accounting GL posting retry job (when built), <paramref name="rounds"/> times.</summary>
    public static async Task RunJobsAsync(this SapWebApplicationFactory f, int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
        {
            await f.RunInScopeAsync<DependencyJob>(job => job.RunOnceAsync());
            await f.RunInScopeAsync<SyncOutboxJob>(job => job.RunOnceAsync());
            await f.RunGlRetryJobAsync();
        }
    }

    public static Task RunReconciliationAsync(this SapWebApplicationFactory f) =>
        f.RunInScopeAsync<ReconciliationJob>(job => job.RunOnceAsync());

    /// <summary>
    /// CORE's GL posting retry job (D-9), found by reflection so this compiles before it exists: a type in SMS.Modules.Accounting
    /// whose name ends in "Job" and mentions "Posting", run through RunOnceAsync / RunAsync / ExecuteAsync from a fresh scope.
    /// Returns false when there is none yet.
    /// </summary>
    public static async Task<bool> RunGlRetryJobAsync(this SapWebApplicationFactory f)
    {
        var type = typeof(SMS.Modules.Accounting.IAccountingModule).Assembly.GetTypes()
            .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Job", StringComparison.Ordinal)
                                 && t.Name.Contains("Posting", StringComparison.OrdinalIgnoreCase));
        if (type is null) return false;

        await using var scope = f.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetService(type) ?? ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "RunOnceAsync" or "RunAsync" or "ExecuteAsync" && typeof(Task).IsAssignableFrom(m.ReturnType))
            .OrderBy(m => m.GetParameters().Length)
            .First();
        var args = method.GetParameters().Select(p => p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None
            : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        await (Task)method.Invoke(job, args)!;
        return true;
    }

    // ── The gateway, in-process (T-01..T-08) ─────────────────────────────────────

    /// <summary>Runs <paramref name="run"/> in a fresh scope as <paramref name="org"/> (no HttpContext: HangfireTenantScope).</summary>
    public static async Task<T> AsOrgAsync<T>(this SapWebApplicationFactory f, Guid org, Func<IServiceProvider, Task<T>> run)
    {
        var previous = HangfireTenantScope.OrganizationId;
        HangfireTenantScope.OrganizationId = org;
        try
        {
            await using var scope = f.Services.CreateAsyncScope();
            return await run(scope.ServiceProvider);
        }
        finally
        {
            HangfireTenantScope.OrganizationId = previous;
        }
    }

    public static Task<PostingResult> PostDirectAsync(this SapWebApplicationFactory f, Guid org, FinancialTransaction tx) =>
        f.AsOrgAsync(org, sp => sp.GetRequiredService<IFinancialPostingGateway>().PostAsync(tx));

    public static Task<PostingResult> VoidDirectAsync(this SapWebApplicationFactory f, Guid org, VoidRequest r) =>
        f.AsOrgAsync(org, sp => sp.GetRequiredService<IFinancialPostingGateway>().VoidAsync(r));

    /// <summary>A synthetic, balanced document-free transaction (what a caller would build), in PKR.</summary>
    public static FinancialTransaction Synthetic(FinDocType type, decimal amount, Guid? id = null, DateTime? date = null,
        params (string Role, decimal Debit, decimal Credit)[] lines) => new()
    {
        DocumentType = type, SourceDocumentId = id ?? Guid.NewGuid(), SourceDocumentNumber = $"QA-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
        SourceModule = PostingSourceModules.Finance, TransactionDate = date ?? Today, CurrencyCode = "PKR",
        Description = "A44 QA synthetic",
        Lines = (lines.Length > 0 ? lines : [("DEFAULT_RECEIVABLE", amount, 0m), ("DEFAULT_REVENUE", 0m, amount)])
            .Select((l, i) => new FinancialTransactionLine { LineNumber = i + 1, AccountRole = l.Role, Debit = l.Debit, Credit = l.Credit })
            .ToList()
    };

    public static TargetResult? Target(this PostingResult r, string code) => r.TargetResults.SingleOrDefault(t => t.TargetCode == code);

    public static string Describe(this PostingResult r) =>
        $"{r.Status} '{r.Message}' [{string.Join(", ", r.TargetResults.Select(t => $"{t.TargetCode}:{t.Status}:{t.Error}"))}]";

    // ── SQL reads ────────────────────────────────────────────────────────────────

    /// <summary>The org's A42 default key → account code (GET accounting-defaults).</summary>
    public static async Task<Dictionary<string, string>> DefaultCodesAsync(this SapKit k)
    {
        var d = await k.Ok(k.Get("/api/finance/accounting-defaults"), "accounting defaults");
        return d.A("categories").SelectMany(c => c.A("items"))
            .Where(i => i.P("account").ValueKind == JsonValueKind.Object)
            .ToDictionary(i => i.S("defaultKey")!, i => i.P("account").S("accountCode")!);
    }

    /// <summary>Journals whose SourceDocumentUuid is <paramref name="source"/> (A44 columns, API-CONTRACT §3), oldest first.</summary>
    public static async Task<List<Journal>> JournalsAsync(this SapWebApplicationFactory f, Guid org, Guid source)
    {
        var heads = await f.QueryAsync(
            "SELECT Id, EntryNumber, EntryType, Status, EntryDate, TotalDebit, TotalCredit, Reference, IdempotencyKey FROM finance.JournalEntries " +
            "WHERE OrganizationId = @o AND SourceDocumentUuid = @s ORDER BY Id", ("@o", org), ("@s", source));
        return await ReadJournalsAsync(f, heads);
    }

    /// <summary>Every journal of the organization that is not the opening balance.</summary>
    public static async Task<List<Journal>> AllJournalsAsync(this SapWebApplicationFactory f, Guid org)
    {
        var heads = await f.QueryAsync(
            "SELECT Id, EntryNumber, EntryType, Status, EntryDate, TotalDebit, TotalCredit, Reference, IdempotencyKey FROM finance.JournalEntries " +
            "WHERE OrganizationId = @o AND EntryType <> 'OPENING_BALANCE' ORDER BY Id", ("@o", org));
        return await ReadJournalsAsync(f, heads);
    }

    private static async Task<List<Journal>> ReadJournalsAsync(SapWebApplicationFactory f, List<Dictionary<string, object?>> heads)
    {
        var result = new List<Journal>();
        foreach (var h in heads)
        {
            var lines = await f.QueryAsync(
                "SELECT a.AccountCode, l.DebitAmount, l.CreditAmount FROM finance.JournalEntryLines l JOIN finance.ChartOfAccounts a ON a.Id = l.AccountId " +
                "WHERE l.JournalEntryId = @j ORDER BY l.LineNumber", ("@j", h["Id"]!));
            result.Add(new Journal(Convert.ToInt32(h["Id"]), (string)h["EntryNumber"]!, (string)h["EntryType"]!, (string)h["Status"]!,
                Convert.ToDateTime(h["EntryDate"]), Convert.ToDecimal(h["TotalDebit"]), Convert.ToDecimal(h["TotalCredit"]),
                h["Reference"] as string, h["IdempotencyKey"] as string,
                lines.Select(l => ((string)l["AccountCode"]!, Convert.ToDecimal(l["DebitAmount"]), Convert.ToDecimal(l["CreditAmount"]))).ToList()));
        }
        return result;
    }

    /// <summary>Net movement (debit − credit) on an account code over the org's non-opening posted journals.</summary>
    public static async Task<decimal> NetAsync(this SapWebApplicationFactory f, Guid org, string code) =>
        (await f.AllJournalsAsync(org)).Where(j => j.Status == "Posted").Sum(j => j.Net(code));

    /// <summary>The posting log rows of a source document through the API (GET posting/log, D-12), as JSON.</summary>
    public static async Task<List<JsonElement>> PostingRowsAsync(this SapKit k, Guid source)
    {
        var page = await k.Ok(k.Get($"{Posting}/log?page=1&pageSize=100"), "posting log");
        var rows = page.ValueKind == JsonValueKind.Array ? page.Items() : page.A(page.Has("data") ? "data" : "items");
        return rows.Where(r => r.Has("sourceDocumentUuid") && r.NG("sourceDocumentUuid") == source).ToList();
    }

    /// <summary>A posting row's status at one target ("internal_gl" | "quickbooks"), or null when the target is not on the row.</summary>
    public static string? TargetStatus(this JsonElement row, string target) =>
        row.A("targets").Where(t => t.S("targetCode") == target).Select(t => t.S("status")).FirstOrDefault();

    public static string? TargetError(this JsonElement row, string target) =>
        row.A("targets").Where(t => t.S("targetCode") == target).Select(t => t.S("lastError")).FirstOrDefault();

    /// <summary>The posting log detail (GET posting/log/{uuid}).</summary>
    public static Task<JsonElement> PostingDetailAsync(this SapKit k, JsonElement row) =>
        k.Ok(k.Get($"{Posting}/log/{row.G("uuid")}"), "posting detail");

    public static Task<Api> RetryPosting(this SapKit k, JsonElement row, string? target = null, HttpClient? c = null) =>
        k.Post($"{Posting}/log/{row.G("uuid")}/retry", new { targetCode = target }, c);

    public static Task<Api> SkipPosting(this SapKit k, JsonElement row, string? target = null, string reason = "a44 qa", HttpClient? c = null) =>
        k.Post($"{Posting}/log/{row.G("uuid")}/skip", new { targetCode = target, reason }, c);

    /// <summary>The posting row of a source document from SQL (finance.FinancialPostings), as a dictionary of its columns.</summary>
    public static async Task<List<Dictionary<string, object?>>> PostingRowsSqlAsync(this SapWebApplicationFactory f, Guid org, Guid source) =>
        await f.QueryAsync("SELECT * FROM finance.FinancialPostings WHERE OrganizationId = @o AND IdempotencyKey LIKE @k",
            ("@o", org), ("@k", $"%{source}%"));

    public static async Task<MapRow?> MapOrNullAsync(this SapWebApplicationFactory f, Guid org, string externalId, string? kind = null)
    {
        var rows = await f.QueryAsync(
            "SELECT m.Kind, m.State, m.RemoteId, m.LastErrorCode, m.LastError, m.Warning, m.PayloadJson FROM integration.EntityMaps m " +
            "JOIN integration.Connections c ON c.Id = m.ConnectionId WHERE c.OrganizationId = @o AND m.ExternalId = @e" +
            (kind is null ? "" : " AND m.Kind = @k"),
            kind is null ? [("@o", org), ("@e", externalId)] : [("@o", org), ("@e", externalId), ("@k", kind)]);
        if (rows.Count == 0) return null;
        rows.Should().HaveCount(1, $"one map for {kind} {externalId}");
        var r = rows[0];
        return new MapRow((string)r["Kind"]!, (string)r["State"]!, r["RemoteId"] as string, r["LastErrorCode"] as string,
            r["LastError"] as string, r["Warning"] as string, r["PayloadJson"] as string);
    }

    public static async Task<MapRow> MapAsync(this SapWebApplicationFactory f, Guid org, Guid externalId, string? kind = null) =>
        await f.MapOrNullAsync(org, externalId.ToString(), kind) ?? throw new InvalidOperationException($"No map for {kind} {externalId}");

    // ── The fake company ─────────────────────────────────────────────────────────

    /// <summary>The fake's record of <paramref name="kind"/> whose display name / doc number is <paramref name="name"/>.</summary>
    public static FakeQuickBooksCompany.Record? Remote(this SapWebApplicationFactory f, SyncKind kind, string? remoteId) =>
        remoteId is null ? null : f.Company.Records(kind).SingleOrDefault(r => r.Id == remoteId);

    /// <summary>The entity QuickBooks received, as JSON (the builders' Remote* types are read by name, so this compiles before they exist).</summary>
    public static JsonElement Json(this FakeQuickBooksCompany.Record r) =>
        r.Entity is null ? default : JsonSerializer.SerializeToElement(r.Entity, r.Entity.GetType());

    /// <summary>Every write to the fake for this kind since the last reset, as "Operation:Kind:Name".</summary>
    public static List<string> WritesOf(this SapWebApplicationFactory f, SyncKind kind) =>
        f.Company.Writes.Where(w => w.Kind == kind).Select(w => $"{w.Operation}:{w.Kind}:{w.Name}").ToList();

    // ── SCM documents (thin wrappers over the existing kits) ──────────────────────

    public static async Task<(Warehouse Wh, Partner Vendor, Partner Customer, Product Item)> StockedWorldAsync(this SapKit k, string label,
        decimal price = 100m, decimal cost = 60m, decimal qty = 50m)
    {
        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.CreateVendorAsync($"{label} Vendor");
        var customer = await k.CreateCustomerAsync($"{label} Customer");
        var item = await k.CreateProductAsync($"{label} Item", purchasePrice: cost, sellingPrice: price);
        await k.StockUpAsync(vendor, wh, (item, qty, cost));
        return (wh, vendor, customer, item);
    }

    /// <summary>SO → delivered → invoice issued (17 % GST by code). Returns the issued invoice (detail JSON).</summary>
    public static async Task<JsonElement> IssuedInvoiceAsync(this SapKit k, Partner customer, Product item, decimal qty, Guid pkr, Guid? gst = null)
    {
        var (_, _, invoice) = await k.SellAsync(customer, pkr, SapKit.Line(item, qty, taxCode: gst));
        return await k.GetInvoiceAsync(invoice);
    }

    public static async Task<JsonElement> RecordPaymentAsync(this SapKit k, Partner customer, decimal amount, string method,
        params (Guid Invoice, decimal Amount)[] allocations)
    {
        var recorded = await k.Ok(k.Post("/api/customer-payments", new
        {
            PartnerId = customer.Uuid, Amount = amount, Method = method, CurrencyCode = "PKR", PaymentDate = Today,
            ChequeNumber = method == "CHEQUE" ? $"CHQ{Guid.NewGuid():N}"[..10] : null,
            Allocations = allocations.Length == 0 ? null : allocations.Select(a => new { InvoiceUuid = a.Invoice, a.Amount }).ToArray()
        }), $"customer payment {amount} {method}");
        return recorded;
    }

    public static async Task<Guid> ApprovedBillAsync(this SapKit k, Partner vendor, decimal subtotal, decimal tax = 0m, string? number = null)
    {
        var uuid = (await k.Ok(k.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = number ?? $"{k.Marker}-{Guid.NewGuid():N}"[..20], SupplierId = vendor.Uuid, InvoiceDate = Today, ReceivedDate = Today,
            DueDate = Today.AddDays(30), Currency = "PKR", Subtotal = subtotal, TaxAmount = tax
        }), "supplier invoice")).GetGuid();
        await k.Ok(k.Post($"/api/finance/invoices/{uuid}/approve", new { Notes = "a44 qa" }), "approve supplier invoice");
        return uuid;
    }

    /// <summary>Draft → approve → post a supplier payment against one bill. Returns its uuid.</summary>
    public static async Task<Guid> PostedSupplierPaymentAsync(this SapKit k, Partner vendor, Guid bill, decimal amount, string method = "BANK_TRANSFER")
    {
        var p = (await k.Ok(k.Post("/api/supplier-payments", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = Today, PaymentMethod = method, TotalAmount = amount,
            CurrencyCode = "PKR", BankAccount = method == "CASH" ? null : "QA-BANK-0001",
            ChequeNo = method == "CHEQUE" ? "CHQ-QA-1" : null, ChequeDate = method == "CHEQUE" ? Today : (DateTime?)null,
            Lines = new[] { new { InvoiceUuid = bill, AllocatedAmount = amount } }
        }), "draft supplier payment")).GetGuid();
        await k.Ok(k.Post($"/api/supplier-payments/{p}/approve"), "approve supplier payment");
        await k.Ok(k.Post($"/api/supplier-payments/{p}/post"), "post supplier payment");
        return p;
    }

    /// <summary>A manual supplier return order (no GRN, no warehouse) taken to SUPPLIER_RECEIVED — what a note is raised from.</summary>
    public static async Task<Guid> ReadySroAsync(this SapKit k, Partner vendor, decimal qty, decimal unitCost)
    {
        var sro = (await k.Ok(k.Post("/api/sros", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, SroType = "POST_RECEIPT_DEFECT", OriginalGrnId = (Guid?)null, OriginalPoId = (Guid?)null,
            WarehouseUuid = (Guid?)null, ReturnReason = "DAMAGED", Notes = "a44 qa",
            Lines = new[]
            {
                new
                {
                    GrnLineUuid = (Guid?)null, PoLineUuid = (Guid?)null, ProductUuid = (Guid?)null, ItemDescription = k.Next("Returned goods"),
                    UnitOfMeasure = "PCS", QtyToReturn = qty, ReturnReason = "DAMAGED", Condition = "DAMAGED", UnitCost = (decimal?)unitCost
                }
            }
        }), "create SRO")).GetGuid();
        await k.Ok(k.Post($"/api/sros/{sro}/approve", new { Notes = "ok" }), "approve SRO");
        await k.Ok(k.Post($"/api/sros/{sro}/dispatch", new
        {
            RmaNumber = $"RMA-{Guid.NewGuid():N}"[..12], DispatchDate = DateTime.UtcNow, DispatchCarrier = "QA Courier", DispatchTrackingRef = "TRK"
        }), "dispatch SRO");
        await k.Ok(k.Post($"/api/sros/{sro}/confirm-receipt", new { Notes = "received" }), "supplier received the return");
        return sro;
    }

    public static async Task<Guid> SupplierCreditNoteAsync(this SapKit k, Guid sro, decimal amount, Guid? invoice = null) =>
        (await k.Ok(k.Post("/api/credit-notes", new
        {
            SroId = sro, SupplierCreditNoteNo = $"SCN-{Guid.NewGuid():N}"[..16], CreditDate = Today, CreditAmount = amount, InvoiceUuid = invoice, Notes = "a44 qa"
        }), "supplier credit note")).GetGuid();

    public static async Task<Guid> DebitNoteAsync(this SapKit k, Guid sro, decimal amount, Guid? invoice = null) =>
        (await k.Ok(k.Post("/api/debit-notes", new
        {
            SroId = sro, DebitReason = "DAMAGED_GOODS", DebitReasonDetail = "a44 qa", DebitAmount = amount, InvoiceUuid = invoice, Notes = "a44 qa"
        }), "debit note")).GetGuid();

    public static Task<Api> DebitNoteStatus(this SapKit k, Guid note, string status) =>
        k.Patch($"/api/debit-notes/{note}/status", new { NewStatus = status, Notes = "a44 qa" });

    /// <summary>The fiscal period (of the first fiscal year) that contains <paramref name="day"/>.</summary>
    public static async Task<Guid> PeriodOfAsync(this SapKit k, DateTime day)
    {
        var years = await k.Ok(k.Get("/api/finance/fiscal-years"), "fiscal years");
        foreach (var y in years.Items())
        {
            var periods = await k.Ok(k.Get($"/api/finance/fiscal-years/{y.G("uuid")}/periods"), "periods");
            foreach (var p in periods.Items())
                if (!(p.Has("isAdjustment") && p.B("isAdjustment"))
                    && DateTime.Parse(p.S("startDate")!) <= day.Date && DateTime.Parse(p.S("endDate")!) >= day.Date)
                    return p.G("uuid");
        }
        throw new InvalidOperationException($"No fiscal period holds {day:yyyy-MM-dd}");
    }

    public static Task<Api> SetPeriodStatus(this SapKit k, Guid period, string status) =>
        k.Put($"/api/finance/fiscal-periods/{period}", new { status });

    public static void ShouldNotBeForbidden(this Api api, string because) =>
        api.Status.Should().NotBe(HttpStatusCode.Forbidden, $"{because} — {api}").And.NotBe(HttpStatusCode.Unauthorized);
}
