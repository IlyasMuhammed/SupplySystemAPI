using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

// A44 CORE test kit — the posting gateway, the internal GL target and the posting log over an in-memory store, with a fake
// external target ("quickbooks"), a settable clock, an optional module gate and a tenant that follows HangfireTenantScope.

/// <summary>The ambient organization: HangfireTenantScope when set (jobs), else <see cref="Current"/>.</summary>
internal sealed class ScopeTenant : ITenantContext
{
    public Guid Current { get; set; }
    public Guid OrganizationId => HangfireTenantScope.OrganizationId ?? Current;
    public bool IsSuperAdmin => false;
}

internal sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeGate : IModuleGate
{
    public HashSet<(Guid, string)> On { get; } = [];
    public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
        Task.FromResult(On.Contains((organizationId, featureCode)));
}

/// <summary>An external target that records what it was sent and answers Queued (or what the test says).</summary>
internal sealed class FakeTarget : IFinancialPostingTarget
{
    private readonly ITenantContext _tenant;
    public FakeTarget(string code, ITenantContext tenant) { TargetCode = code; _tenant = tenant; }

    public string TargetCode { get; }
    /// <summary>Null = active for every organization.</summary>
    public HashSet<Guid>? ActiveOrgs { get; set; }
    public bool ThrowOnActive { get; set; }
    public Exception? Throw { get; set; }
    public Func<FinancialTransaction, TargetResult>? OnPost { get; set; }
    public List<FinancialTransaction> Posts { get; } = [];
    public List<VoidRequest> Voids { get; } = [];
    public List<VoidRequest> Reverses { get; } = [];

    public Task<bool> IsActiveAsync(CancellationToken ct = default) =>
        ThrowOnActive ? throw new InvalidOperationException("connection lookup failed")
            : Task.FromResult(ActiveOrgs is null || ActiveOrgs.Contains(_tenant.OrganizationId));

    public Task<TargetResult> PostAsync(FinancialTransaction tx, CancellationToken ct = default)
    {
        Posts.Add(tx);
        if (Throw is not null) throw Throw;
        return Task.FromResult(OnPost?.Invoke(tx) ?? new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Queued, InternalId = "map-1" });
    }

    public Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default)
    {
        Voids.Add(request);
        if (Throw is not null) throw Throw;
        return Task.FromResult(new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Queued });
    }

    public Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default)
    {
        Reverses.Add(request);
        if (Throw is not null) throw Throw;
        return Task.FromResult(new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Queued });
    }
}

/// <summary>The QuickBooks gateway's status read (D-49b): answers what the test put in <see cref="States"/>, counts calls per kind.</summary>
internal sealed class FakeQuickBooksStatus : IQuickBooksGateway
{
    public Dictionary<string, SyncStatus> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(SyncKind Kind, IReadOnlyCollection<string> Ids)> Calls { get; } = [];
    public bool Throw { get; set; }

    public void Set(SyncKind kind, string externalId, SyncState state, string? remoteId = null, string? docNumber = null, string? error = null) =>
        States[externalId] = new SyncStatus(kind, externalId, state, remoteId, docNumber, error is null ? null : "6000", error, null, null);

    public Task<IReadOnlyList<SyncStatus>> GetStatusAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        Calls.Add((kind, externalIds));
        if (Throw) throw new InvalidOperationException("integration down");
        return Task.FromResult<IReadOnlyList<SyncStatus>>(externalIds.Where(States.ContainsKey).Select(i => States[i]).Where(s => s.Kind == kind).ToList());
    }

    private static Task<GatewayResult> Disabled => Task.FromResult(GatewayResult.Disabled());
    public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default) => Disabled;
}

internal sealed class PostingHarness : IDisposable
{
    public Guid OrgA { get; } = Guid.NewGuid();
    public Guid OrgB { get; } = Guid.NewGuid();
    public ScopeTenant Tenant { get; } = new();
    public MutableClock Clock { get; } = new();
    public FakeTarget External { get; }
    public FakeGate? Gate { get; }

    private readonly ServiceProvider _root;
    private IServiceScope _scope;

    public PostingHarness(bool withExternal = true, FakeGate? gate = null, Action<DbContextOptionsBuilder>? store = null,
        IDocumentNumberGenerator? numbers = null, IQuickBooksGateway? quickBooks = null)
    {
        Tenant.Current = OrgA;
        External = new FakeTarget(PostingTargetCodes.QuickBooks, Tenant);
        Gate = gate;
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(Tenant);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDbContext<AccountingDbContext>(o => { if (store is null) o.UseInMemoryDatabase(dbName); else store(o); });
        if (numbers is not null) services.AddSingleton(numbers);
        if (quickBooks is not null) services.AddSingleton(quickBooks);
        // A caller module's TryAdd before Accounting: the real gateway must win.
        services.AddScoped<IFinancialPostingGateway, NullFinancialPostingGateway>();
        AccountingModuleExtensions.AddAccountingServices(services);
        if (withExternal) services.AddSingleton<IFinancialPostingTarget>(External);
        if (gate is not null) services.AddSingleton<IModuleGate>(gate);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();
    }

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public AccountingDbContext Db => Get<AccountingDbContext>();
    public IFinancialPostingGateway Gateway => Get<IFinancialPostingGateway>();
    public IPostingLogService Log => Get<IPostingLogService>();

    /// <summary>An independent request scope (own DbContext) — for concurrency tests.</summary>
    public IServiceScope CreateScope() => _root.CreateScope();

    public void NewScope()
    {
        _scope.Dispose();
        _scope = _root.CreateScope();
    }

    public AccountingCaller Admin(Guid? org = null) => new(org ?? OrgA, 1, [P.GL_VIEW, P.GL_SETUP_MANAGE], "Ada Admin");
    public AccountingCaller Viewer(Guid? org = null) => new(org ?? OrgA, 2, [P.GL_VIEW], "Vic Viewer");
    public AccountingCaller IntegrationViewer(Guid? org = null) => new(org ?? OrgA, 3, [P.INTEGRATION_VIEW], "Ivy");
    public AccountingCaller IntegrationAdmin(Guid? org = null) => new(org ?? OrgA, 4, [P.INTEGRATION_VIEW, P.INTEGRATION_MANAGE], "Ian Integrator");
    public AccountingCaller Both(Guid? org = null) => new(org ?? OrgA, 5, [P.GL_VIEW, P.GL_SETUP_MANAGE, P.INTEGRATION_MANAGE], "Bo Both");

    public static readonly (string Code, string Root, string Type)[] Leaves =
    [
        ("1111", RootTypes.Asset, AccountTypes.Cash), ("1112", RootTypes.Asset, AccountTypes.Bank),
        ("1113", RootTypes.Asset, AccountTypes.Receivable), ("1114", RootTypes.Asset, AccountTypes.OtherCurrentAsset),
        ("2111", RootTypes.Liability, AccountTypes.Payable), ("2112", RootTypes.Liability, AccountTypes.Tax),
        ("3111", RootTypes.Equity, AccountTypes.Equity), ("3900", RootTypes.Equity, AccountTypes.Equity),
        ("4111", RootTypes.Revenue, AccountTypes.Revenue), ("5111", RootTypes.Expense, AccountTypes.CostOfGoodsSold),
        ("5112", RootTypes.Expense, AccountTypes.OtherExpense),
    ];

    private static readonly Dictionary<string, string> DefaultCodes = new()
    {
        [DefaultKeys.Receivable] = "1113", [DefaultKeys.Payable] = "2111", [DefaultKeys.Cash] = "1111", [DefaultKeys.Bank] = "1112",
        [DefaultKeys.Revenue] = "4111", [DefaultKeys.Cogs] = "5111", [DefaultKeys.RetainedEarnings] = "3111",
        [DefaultKeys.OpeningBalance] = "3900", [DefaultKeys.TaxPayable] = "2112", [DefaultKeys.Rounding] = "5112",
    };

    /// <summary>Chart, FY 2026 (12 open periods), defaults, setup Complete and (optionally) internal accounting on.</summary>
    public async Task SetupGlAsync(Guid? org = null, bool enable = true, bool autoPost = true, bool rounding = true)
    {
        var o = org ?? OrgA;
        var byCode = new Dictionary<string, ChartOfAccount>();
        foreach (var root in RootTypes.All)
        {
            var c1 = RootTypes.RootCode[root];
            var l1 = new ChartOfAccount { OrganizationId = o, AccountCode = c1, AccountName = root, Level = 1, RootType = root, IsGroup = true,
                IsSystemAccount = true, MaterializedPath = $"/{c1}" };
            var l2 = new ChartOfAccount { OrganizationId = o, AccountCode = c1 + "1", AccountName = root + " L2", Level = 2, RootType = root,
                IsGroup = true, Parent = l1, MaterializedPath = $"/{c1}/{c1}1" };
            var l3 = new ChartOfAccount { OrganizationId = o, AccountCode = c1 + "11", AccountName = root + " L3", Level = 3, RootType = root,
                IsGroup = true, Parent = l2, MaterializedPath = $"/{c1}/{c1}1/{c1}11" };
            Db.ChartOfAccounts.AddRange(l1, l2, l3);
            byCode[c1 + "11"] = l3;
        }
        foreach (var (code, root, type) in Leaves)
        {
            var parent = byCode[RootTypes.RootCode[root] + "11"];
            var leaf = new ChartOfAccount { OrganizationId = o, AccountCode = code, AccountName = $"Account {code}", Level = 4, RootType = root,
                AccountType = type, Parent = parent, MaterializedPath = $"{parent.MaterializedPath}/{code}" };
            Db.ChartOfAccounts.Add(leaf);
            byCode[code] = leaf;
        }
        var year = new FiscalYear { OrganizationId = o, FiscalYearName = "FY 2026", StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31), ReportingCurrencyCode = "PKR" };
        for (byte m = 1; m <= 12; m++)
            year.Periods.Add(new FiscalPeriod { OrganizationId = o, PeriodNumber = m, PeriodName = $"P{m}",
                StartDate = new DateOnly(2026, m, 1), EndDate = new DateOnly(2026, m, 1).AddMonths(1).AddDays(-1) });
        Db.FiscalYears.Add(year);
        await Db.SaveChangesAsync();

        await Get<IAccountingInitializer>().EnsureAsync(o);
        foreach (var d in await Db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == o).ToListAsync())
            if (DefaultCodes.TryGetValue(d.DefaultKey, out var code) && (rounding || d.DefaultKey != DefaultKeys.Rounding))
                d.AccountId = byCode[code].Id;
        var status = await Db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == o);
        status.Status = SetupStatuses.Complete;
        await Db.SaveChangesAsync();

        if (enable)
        {
            var previous = Tenant.Current;
            Tenant.Current = o;
            await Get<ITenantFinanceConfigService>().UpdateAsync(Admin(o),
                new UpdatePostingConfigRequest { InternalAccountingEnabled = true, AutoPostToGL = autoPost });
            Tenant.Current = previous;
        }
    }

    public async Task<decimal> BalanceAsync(string code, Guid? org = null)
    {
        var o = org ?? OrgA;
        return (await Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.OrganizationId == o && a.AccountCode == code))
            .CurrentBalance;
    }

    public Task<List<JournalEntry>> JournalsAsync(Guid? org = null)
    {
        var o = org ?? OrgA;
        return Db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.Lines).Where(j => j.OrganizationId == o)
            .OrderBy(j => j.Id).ToListAsync();
    }

    public Task<FinancialPosting> PostingAsync(string key, Guid? org = null)
    {
        var o = org ?? OrgA;
        return Db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Include(p => p.Targets)
            .SingleAsync(p => p.OrganizationId == o && p.IdempotencyKey == key);
    }

    public async Task SetPeriodStatusAsync(int month, string status, Guid? org = null)
    {
        var o = org ?? OrgA;
        var p = await Db.FiscalPeriods.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == o && x.PeriodNumber == month);
        p.Status = status;
        await Db.SaveChangesAsync();
    }

    // ── transactions ───────────────────────────────────────────────────────────────────────────────────────────────

    public static FinancialTransactionLine L(int n, string role, decimal debit, decimal credit) =>
        new() { LineNumber = n, AccountRole = role, Debit = debit, Credit = credit };

    public static FinancialTransaction Invoice(Guid id, decimal net = 100m, decimal tax = 18m, DateTime? date = null, string currency = "PKR",
        decimal? rate = null, IAccountingDocument? document = null, string number = "SI-2026-00001") => new()
    {
        DocumentType = FinDocType.SalesInvoice,
        SourceDocumentId = id,
        SourceDocumentNumber = number,
        SourceModule = PostingSourceModules.Finance,
        TransactionDate = date ?? new DateTime(2026, 10, 15),
        PartnerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        PartnerType = PartnerType.Customer,
        PartnerName = "Acme",
        CurrencyCode = currency,
        ExchangeRate = rate,
        Description = "Invoice " + number,
        Lines = [L(1, DefaultKeys.Receivable, net + tax, 0), L(2, DefaultKeys.Revenue, 0, net), L(3, DefaultKeys.TaxPayable, 0, tax)],
        Metadata = document is null ? null : new FinancialDocumentMetadata { Document = document },
    };

    public static VoidRequest VoidOf(Guid id, FinDocType type = FinDocType.SalesInvoice, string module = PostingSourceModules.Finance) =>
        new() { SourceDocumentId = id, SourceModule = module, DocumentType = type, SourceDocumentNumber = "SI-2026-00001", Reason = "cancelled",
            VoidDate = new DateTime(2026, 10, 16) };

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
    }
}
