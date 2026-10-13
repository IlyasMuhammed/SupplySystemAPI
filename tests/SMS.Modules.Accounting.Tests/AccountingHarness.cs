using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>Records every published event, then runs the real handlers (the history writer) like the module's publisher.</summary>
internal sealed class RecordingEvents : IAccountingEventPublisher
{
    private readonly IServiceProvider _services;
    public RecordingEvents(IServiceProvider services) => _services = services;
    public List<IAccountingEvent> Published { get; } = [];
    public IEnumerable<T> Of<T>() where T : IAccountingEvent => Published.OfType<T>();

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IAccountingEvent
    {
        Published.Add(evt);
        foreach (var h in _services.GetServices<IAccountingEventHandler<TEvent>>()) await h.HandleAsync(evt, ct);
    }
}

/// <summary>The Accounting services over an in-memory store (one database per harness, shared by its scopes).</summary>
internal sealed class AccountingHarness : IDisposable
{
    public Guid Org { get; } = Guid.NewGuid();
    public StaticTenantContext Tenant { get; }

    private readonly ServiceProvider _root;
    private IServiceScope _scope;

    public AccountingHarness()
    {
        Tenant = new StaticTenantContext { OrganizationId = Org };
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(Tenant);
        services.AddDbContext<AccountingDbContext>(o => o.UseInMemoryDatabase(dbName));
        AccountingModuleExtensions.AddAccountingServices(services);
        services.AddScoped<IAccountingEventPublisher, RecordingEvents>();
        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();
    }

    public IServiceProvider Services => _scope.ServiceProvider;
    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public AccountingDbContext Db => Get<AccountingDbContext>();
    public RecordingEvents Events => (RecordingEvents)Get<IAccountingEventPublisher>();
    public ISetupStatusService Setup => Get<ISetupStatusService>();

    /// <summary>A fresh request scope (new DbContext, new initializer cache) over the same store.</summary>
    public void NewScope()
    {
        _scope.Dispose();
        _scope = _root.CreateScope();
    }

    public AccountingCaller Admin(Guid? org = null) => new(org ?? Org, 1, [P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE], "Ada Admin");
    public AccountingCaller Viewer(Guid? org = null) => new(org ?? Org, 2, [P.GL_VIEW], "Vic Viewer");
    public AccountingCaller Nobody(Guid? org = null) => new(org ?? Org, 3, [], "No One");

    /// <summary>The 5 roots, one L2/L3 group per root and the given leaves (code, root, type); returns the accounts by code.</summary>
    public async Task<Dictionary<string, ChartOfAccount>> SeedChartAsync(Guid? org = null, params (string Code, string Root, string Type)[] leaves)
    {
        var o = org ?? Org;
        var byCode = new Dictionary<string, ChartOfAccount>();
        foreach (var root in RootTypes.All)
        {
            var c1 = RootTypes.RootCode[root];
            var l1 = new ChartOfAccount { OrganizationId = o, AccountCode = c1, AccountName = SystemAccounts.RootNames[root], Level = 1, RootType = root,
                IsGroup = true, IsSystemAccount = true, MaterializedPath = $"/{c1}" };
            var l2 = new ChartOfAccount { OrganizationId = o, AccountCode = c1 + "1", AccountName = root + " L2", Level = 2, RootType = root, IsGroup = true,
                Parent = l1, MaterializedPath = $"/{c1}/{c1}1" };
            var l3 = new ChartOfAccount { OrganizationId = o, AccountCode = c1 + "11", AccountName = root + " L3", Level = 3, RootType = root, IsGroup = true,
                Parent = l2, MaterializedPath = $"/{c1}/{c1}1/{c1}11" };
            Db.ChartOfAccounts.AddRange(l1, l2, l3);
            byCode[l1.AccountCode] = l1; byCode[l2.AccountCode] = l2; byCode[l3.AccountCode] = l3;
        }
        foreach (var (code, root, type) in leaves)
        {
            var parent = byCode[RootTypes.RootCode[root] + "11"];
            var leaf = new ChartOfAccount { OrganizationId = o, AccountCode = code, AccountName = $"Account {code}", Level = 4, RootType = root, IsGroup = false,
                AccountType = type, Parent = parent, MaterializedPath = $"{parent.MaterializedPath}/{code}" };
            Db.ChartOfAccounts.Add(leaf);
            byCode[code] = leaf;
        }
        await Db.SaveChangesAsync();
        return byCode;
    }

    public static readonly (string, string, string)[] StandardLeaves =
    [
        ("1111", RootTypes.Asset, AccountTypes.Cash), ("1112", RootTypes.Asset, AccountTypes.Bank),
        ("1113", RootTypes.Asset, AccountTypes.Receivable), ("2111", RootTypes.Liability, AccountTypes.Payable),
        ("3111", RootTypes.Equity, AccountTypes.Equity), ("3900", RootTypes.Equity, AccountTypes.Equity),
        ("4111", RootTypes.Revenue, AccountTypes.Revenue), ("5111", RootTypes.Expense, AccountTypes.CostOfGoodsSold),
    ];

    public async Task AddFiscalYearAsync(Guid? org = null)
    {
        var year = new FiscalYear { OrganizationId = org ?? Org, FiscalYearName = "FY 2026", StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31), ReportingCurrencyCode = "PKR" };
        for (byte m = 1; m <= 12; m++)
            year.Periods.Add(new FiscalPeriod { OrganizationId = year.OrganizationId, PeriodNumber = m, PeriodName = $"P{m}",
                StartDate = new DateOnly(2026, m, 1), EndDate = new DateOnly(2026, m, 1).AddMonths(1).AddDays(-1) });
        Db.FiscalYears.Add(year);
        await Db.SaveChangesAsync();
    }

    /// <summary>Assigns every required default to the standard leaf of its expected type.</summary>
    public async Task AssignRequiredDefaultsAsync(Dictionary<string, ChartOfAccount> chart, Guid? org = null)
    {
        var o = org ?? Org;
        await Get<IAccountingInitializer>().EnsureAsync(o);
        var code = new Dictionary<string, string>
        {
            [DefaultKeys.Receivable] = "1113", [DefaultKeys.Payable] = "2111", [DefaultKeys.Cash] = "1111", [DefaultKeys.Bank] = "1112",
            [DefaultKeys.Revenue] = "4111", [DefaultKeys.Cogs] = "5111", [DefaultKeys.RetainedEarnings] = "3111", [DefaultKeys.OpeningBalance] = "3900",
        };
        foreach (var d in await Db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == o && d.IsRequired).ToListAsync())
            d.AccountId = chart[code[d.DefaultKey]].Id;
        await Db.SaveChangesAsync();
    }

    /// <summary>What the opening balance service (BE2) does when it posts or skips.</summary>
    public async Task MarkOpeningBalancesPostedAsync(bool balanced = true, Guid? org = null)
    {
        var o = org ?? Org;
        var s = await Db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == o);
        s.OpeningBalancesEntered = true;
        s.OpeningBalancesBalanced = balanced;
        s.OpeningBalancesPostedAt = DateTime.UtcNow;
        s.ConversionDate = new DateOnly(2025, 12, 31);
        await Db.SaveChangesAsync();
    }

    /// <summary>Everything up to BalancesEntered.</summary>
    public async Task<Dictionary<string, ChartOfAccount>> ReadyToCompleteAsync()
    {
        var chart = await SeedChartAsync(null, StandardLeaves);
        await AddFiscalYearAsync();
        await AssignRequiredDefaultsAsync(chart);
        await MarkOpeningBalancesPostedAsync();
        return chart;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
    }
}
