using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Jobs;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Fakes;

/// <summary>
/// The whole gateway + sync engine over an in-memory database, with the real registrations
/// (<c>AddIntegrationGateway</c>) and fakes for everything other work packages own: the accounting
/// provider, connection health, reference data, the caller and the SCM source.
/// Every call runs in a fresh DI scope, like a request or a job.
/// </summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    public const string Realm = "9130000000000001";

    public string                     DbName    { get; } = "qbi-" + Guid.NewGuid();
    /// <summary>One root for every harness (names are unique), so EF does not build a provider per test.</summary>
    private static readonly InMemoryDatabaseRoot SharedRoot = new();
    public InMemoryDatabaseRoot       Root      => SharedRoot;
    public Guid                       OrgId     { get; }
    public HarnessTenant              Tenant    { get; } = new();
    public MutableTimeProvider        Clock     { get; } = new(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
    public ScriptedAccountingProvider Provider  { get; } = new();
    public FakeConnectionHealth       Health    { get; } = new();
    public FakeReferenceDataReader    Reference { get; } = new();
    public FakeCallerContext          Caller    { get; } = new();
    public FakeSourceState            Source    { get; } = new();
    /// <summary>SMS's exchange rates (Finance's IExchangeRateProvider). Registered unless the harness is built without it.</summary>
    public FakeExchangeRates          Rates     { get; } = new();
    public IntegrationJobOptions      Jobs      { get; } = new();
    public ServiceProvider            Services  { get; }

    /// <summary>Set when the harness runs on a throwaway LocalDB database instead of the in-memory provider.</summary>
    public string?                    SqlConnectionString { get; }

    /// <param name="sqlConnectionString">
    /// A throwaway LocalDB database (created here, dropped on dispose) — never the shared database in
    /// appsettings. Null: EF in-memory.
    /// </param>
    /// <param name="withExchangeRates">False: no IExchangeRateProvider at all, as in a host without Finance.</param>
    /// <param name="configure">Runs after every other registration (last registration wins): extra services or replacements.</param>
    public SyncHarness(Guid? organizationId = null, bool withSource = true, string? sqlConnectionString = null, bool withExchangeRates = true,
                       Action<IServiceCollection>? configure = null)
    {
        OrgId = organizationId ?? Guid.NewGuid();
        Tenant.RequestOrganizationId = OrgId;
        SqlConnectionString = sqlConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(Tenant);
        services.AddDbContext<IntegrationDbContext>(o => Configure(o));
        services.AddSingleton<IOptions<IntegrationJobOptions>>(Options.Create(Jobs));
        services.AddSingleton<IOptions<QuickBooksOptions>>(Options.Create(new QuickBooksOptions()));
        services.AddScoped<IConnectionAccessor, ConnectionAccessor>();

        services.AddIntegrationGateway(new ConfigurationBuilder().Build());
        services.AddScoped<IQuickBooksGateway, QuickBooksGateway>();

        // Fakes for what other work packages own (registered last: last registration wins).
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IAccountingProviderRegistry>(new FakeAccountingProviderRegistry(Provider));
        services.AddSingleton<IConnectionHealth>(Health);
        services.AddSingleton<IReferenceDataReader>(Reference);
        services.AddSingleton<IGatewayCallerContext>(Caller);
        if (withSource)
            services.AddScoped<IQuickBooksSource>(sp => new FakeQuickBooksSource(Source, sp.GetRequiredService<IQuickBooksGateway>()));
        if (withExchangeRates)
            services.AddSingleton<IExchangeRateProvider>(Rates);
        configure?.Invoke(services);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        if (SqlConnectionString is not null)
        {
            using var db = SuperDb();
            db.Database.EnsureCreated();
        }

        // Like the real health service: a revoked connection stops being usable.
        Health.OnMarked = async (connectionId, status) =>
        {
            await using var db = SuperDb();
            var connection = await db.Connections.IgnoreQueryFilters().FirstAsync(c => c.Id == connectionId);
            connection.Status = status;
            await db.SaveChangesAsync();
        };
    }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    // ── Running things ───────────────────────────────────────────────────────

    /// <summary>A request of the current tenant, in its own scope.</summary>
    public async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public async Task Scoped(Func<IServiceProvider, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    public Task<GatewayResult> Gateway(Func<IQuickBooksGateway, Task<GatewayResult>> call) =>
        Scoped(sp => call(sp.GetRequiredService<IQuickBooksGateway>()));

    public Task<GatewayResult> Send(object payload) => Gateway(g => payload switch
    {
        CustomerPayload c     => g.UpsertCustomerAsync(c),
        VendorPayload v       => g.UpsertVendorAsync(v),
        ItemPayload i         => g.UpsertItemAsync(i),
        SalesInvoicePayload s => g.UpsertSalesInvoiceAsync(s),
        BillPayload b         => g.UpsertBillAsync(b),
        _ => throw new ArgumentException(payload.GetType().Name)
    });

    public Task<SyncExecutionResult> ExecuteAsync(int entryId) =>
        Scoped(sp => sp.GetRequiredService<ISyncExecutor>().ExecuteAsync(entryId));

    /// <summary>One run of the outbox job, as Hangfire runs it: no tenant of its own.</summary>
    public Task<int> RunOutboxAsync() => AsJob(sp => sp.GetRequiredService<SyncOutboxJob>().RunOnceAsync());

    public Task RunDependenciesAsync() => AsJob(async sp => { await sp.GetRequiredService<DependencyJob>().RunOnceAsync(); return 0; });
    public Task RunReconciliationAsync() => AsJob(async sp => { await sp.GetRequiredService<ReconciliationJob>().RunOnceAsync(); return 0; });
    public Task<(int expired, int requeued)> RunSweepAsync() => AsJob(sp => sp.GetRequiredService<SyncSweepJob>().SweepAsync());

    /// <summary>Outbox runs (and dependency pulls) until nothing more is due now.</summary>
    public async Task DrainAsync(int maxRounds = 10)
    {
        for (var i = 0; i < maxRounds; i++)
        {
            await RunDependenciesAsync();
            var executed = await RunOutboxAsync();
            if (executed == 0) return;
        }
    }

    public async Task<T> AsJob<T>(Func<IServiceProvider, Task<T>> work)
    {
        var request = Tenant.RequestOrganizationId;
        Tenant.RequestOrganizationId = null;
        try
        {
            await using var scope = Services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }
        finally
        {
            Tenant.RequestOrganizationId = request;
        }
    }

    // ── Setup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A live, fully set-up company for <paramref name="organizationId"/> (default: this harness's):
    /// accounts chosen, 0% and 17% mapped, Live mode. Adjust with <paramref name="configure"/>.
    /// </summary>
    public async Task<IntegrationConnection> ConnectAsync(
        Action<IntegrationSettings>? configure = null, ConnectionStatus status = ConnectionStatus.Live,
        Guid? organizationId = null, string realmId = Realm)
    {
        await using var db = DbAs(organizationId ?? OrgId);

        var connection = new IntegrationConnection
        {
            RealmId               = realmId,
            CompanyName           = "Sandbox Company",
            Status                = status,
            HomeCurrencyCode      = "PKR",
            MultiCurrencyEnabled  = false,
            Country               = "PK",
            EncryptedAccessToken  = "enc-access",
            EncryptedRefreshToken = "enc-refresh",
            AccessTokenExpiresAt  = Now.AddHours(1),
            RefreshTokenExpiresAt = Now.AddDays(100),
            ConnectedAt           = Now.AddDays(-1)
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();

        var settings = new IntegrationSettings
        {
            ConnectionId             = connection.Id,
            Mode                     = SyncMode.Live,
            DefaultIncomeAccountId   = "ACC-INC",
            DefaultExpenseAccountId  = "ACC-EXP",
            FreightExpenseAccountId  = "ACC-FRT",
            DiscountAccountId        = "ACC-DSC",
            DefaultPurchaseTaxCodeId = "TAX-PUR"
        };
        configure?.Invoke(settings);
        db.Settings.Add(settings);

        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = connection.Id, TaxPercent = 0m,  QboTaxCodeId = "TAX0" });
        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = connection.Id, TaxPercent = 17m, QboTaxCodeId = "TAX17" });
        await db.SaveChangesAsync();
        return connection;
    }

    public async Task UpdateSettingsAsync(Action<IntegrationSettings> change, Guid? organizationId = null)
    {
        await using var db = DbAs(organizationId ?? OrgId);
        change(await db.Settings.SingleAsync());
        await db.SaveChangesAsync();
    }

    // ── Reading state ────────────────────────────────────────────────────────

    /// <summary>A context that sees every organization — for assertions only.</summary>
    public IntegrationDbContext SuperDb() => Open(new StaticTenantContext { OrganizationId = OrgId, IsSuperAdmin = true });

    public IntegrationDbContext DbAs(Guid organizationId) => Open(new StaticTenantContext { OrganizationId = organizationId });

    private IntegrationDbContext Open(ITenantContext tenant)
    {
        var builder = new DbContextOptionsBuilder<IntegrationDbContext>();
        Configure(builder);
        return new IntegrationDbContext(builder.Options, tenant);
    }

    private void Configure(DbContextOptionsBuilder options)
    {
        if (SqlConnectionString is not null) options.UseSqlServer(SqlConnectionString);
        else options.UseInMemoryDatabase(DbName, Root);
        options.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    public async Task<EntityMap> MapAsync(SyncKind kind, string externalId, string sourceSystem = QuickBooksSourceSystems.Scm, Guid? organizationId = null)
    {
        await using var db = SuperDb();
        return await db.EntityMaps.AsNoTracking().SingleAsync(m => m.Kind == kind && m.ExternalId == externalId
            && m.SourceSystem == sourceSystem && m.OrganizationId == (organizationId ?? OrgId));
    }

    public async Task<EntityMap?> FindMapAsync(SyncKind kind, string externalId, string sourceSystem = QuickBooksSourceSystems.Scm)
    {
        await using var db = SuperDb();
        return await db.EntityMaps.AsNoTracking().SingleOrDefaultAsync(m => m.Kind == kind && m.ExternalId == externalId && m.SourceSystem == sourceSystem);
    }

    public async Task<List<SyncOutboxEntry>> EntriesAsync(SyncKind kind, string externalId, string sourceSystem = QuickBooksSourceSystems.Scm)
    {
        await using var db = SuperDb();
        return await db.Outbox.AsNoTracking().Include(e => e.EntityMap)
            .Where(e => e.EntityMap.Kind == kind && e.EntityMap.ExternalId == externalId && e.EntityMap.SourceSystem == sourceSystem)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    public async Task<SyncOutboxEntry> OpenEntryAsync(SyncKind kind, string externalId)
    {
        var entries = await EntriesAsync(kind, externalId);
        return entries.Last(e => e.Status is not (OutboxStatus.Done or OutboxStatus.Failed));
    }

    public async Task<List<SyncCommandClaim>> ClaimsAsync(int mapId)
    {
        await using var db = SuperDb();
        return await db.CommandClaims.AsNoTracking().Where(c => c.EntityMapId == mapId).OrderBy(c => c.Id).ToListAsync();
    }

    public async Task<List<SyncLogEntry>> LogAsync(int mapId)
    {
        await using var db = SuperDb();
        return await db.SyncLog.AsNoTracking().Where(l => l.EntityMapId == mapId).OrderBy(l => l.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (SqlConnectionString is not null)
        {
            await using var db = SuperDb();
            await db.Database.EnsureDeletedAsync();
        }
        await Services.DisposeAsync();
    }

    /// <summary>A throwaway LocalDB database for one harness (SMS_QBI_&lt;guid&gt;).</summary>
    public static string LocalDb() =>
        $"Server=(localdb)\\MSSQLLocalDB;Database=SMS_QBI_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=60";
}
