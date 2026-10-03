using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Connections;

// Shared building blocks for the connection, setup and auth tests. Everything external to this work
// package — Intuit, the accounting provider, the sync engine's outbox, Tenancy's snapshots — is faked
// here; everything inside it runs for real, wired through the module's own registration.

internal static class TestDb
{
    /// <summary>One root for every in-memory database here, so DI-built and hand-built contexts share data.</summary>
    public static readonly InMemoryDatabaseRoot Root = new();

    public static DbContextOptions<IntegrationDbContext> InMemory(string name) =>
        new DbContextOptionsBuilder<IntegrationDbContext>().UseInMemoryDatabase(name, Root).Options;

    public static IntegrationDbContext Open(string name, ITenantContext tenant) => new(InMemory(name), tenant);

    public static IntegrationDbContext OpenAs(string name, Guid organizationId, bool superAdmin = false) =>
        Open(name, new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = superAdmin });
}

/// <summary>
/// A throwaway LocalDB database (<c>SMS_QBI_&lt;guid&gt;</c>) for what the in-memory provider cannot show —
/// row versions, set-based updates, real concurrency. Never the shared database in appsettings.
/// </summary>
internal sealed class LocalDb : IAsyncDisposable
{
    public string Name             { get; }
    public string ConnectionString { get; }

    private LocalDb(string name)
    {
        Name             = name;
        ConnectionString = $@"Server=(localdb)\MSSQLLocalDB;Database={name};Trusted_Connection=True;TrustServerCertificate=True";
    }

    public static async Task<LocalDb> CreateAsync()
    {
        var db = new LocalDb("SMS_QBI_" + Guid.NewGuid().ToString("N"));
        await using var context = db.OpenAs(Guid.Empty, superAdmin: true);
        await context.Database.EnsureCreatedAsync();
        return db;
    }

    public DbContextOptions<IntegrationDbContext> Options() =>
        new DbContextOptionsBuilder<IntegrationDbContext>().UseSqlServer(ConnectionString).Options;

    public IntegrationDbContext Open(ITenantContext tenant) => new(Options(), tenant);

    public IntegrationDbContext OpenAs(Guid organizationId, bool superAdmin = false) =>
        Open(new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = superAdmin });

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var context = OpenAs(Guid.Empty, superAdmin: true);
        await context.Database.EnsureDeletedAsync();
    }
}

internal static class TestEncryption
{
    public static readonly IEncryptionService Instance =
        new AesEncryptionService(Microsoft.Extensions.Options.Options.Create(new AppSettings { AesEncryptionKey = "integration-tests-key-0123456789abcdef" }));
}

/// <summary>A scripted Intuit OAuth endpoint. Records every call.</summary>
internal sealed class FakeAuthProvider : IAccountingAuthProvider
{
    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    public Func<string, string>                                   ConsentUrl { get; set; } = s => $"https://appcenter.intuit.com/connect/oauth2?state={s}";
    public Func<string, CancellationToken, Task<TokenGrant>>      Exchange   { get; set; } = (code, _) => Task.FromResult(Grant("AT-" + code, "RT-" + code));
    public Func<string, CancellationToken, Task<TokenGrant>>      Refresh    { get; set; } = (_, _) => Task.FromResult(Grant("AT-refreshed", "RT-refreshed"));
    public Func<string, CancellationToken, Task>                  Revoke     { get; set; } = (_, _) => Task.CompletedTask;

    public ConcurrentQueue<string> ConsentStates  { get; } = new();
    public ConcurrentQueue<string> ExchangedCodes { get; } = new();
    public ConcurrentQueue<string> RefreshedWith  { get; } = new();
    public ConcurrentQueue<string> RevokedTokens  { get; } = new();

    public static TokenGrant Grant(string access, string refresh, TimeSpan? accessLife = null, TimeSpan? refreshLife = null) =>
        new(access, refresh,
            DateTime.UtcNow + (accessLife ?? TimeSpan.FromHours(1)),
            DateTime.UtcNow + (refreshLife ?? TimeSpan.FromDays(100)));

    public string BuildConsentUrl(string state)
    {
        ConsentStates.Enqueue(state);
        return ConsentUrl(state);
    }

    public Task<TokenGrant> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        ExchangedCodes.Enqueue(code);
        return Exchange(code, ct);
    }

    public Task<TokenGrant> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        RefreshedWith.Enqueue(refreshToken);
        return Refresh(refreshToken, ct);
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        RevokedTokens.Enqueue(refreshToken);
        return Revoke(refreshToken, ct);
    }
}

/// <summary>Stands in for the sync engine's outbox control.</summary>
internal sealed class RecordingOutbox : IOutboxControl
{
    public ConcurrentQueue<(int ConnectionId, string Reason)> Suspended { get; } = new();
    public ConcurrentQueue<int>                               Resumed   { get; } = new();

    public Task<int> SuspendAllAsync(int connectionId, string reason, CancellationToken ct = default)
    {
        Suspended.Enqueue((connectionId, reason));
        return Task.FromResult(1);
    }

    public Task<int> ResumeAllAsync(int connectionId, CancellationToken ct = default)
    {
        Resumed.Enqueue(connectionId);
        return Task.FromResult(1);
    }
}

/// <summary>Tenancy's snapshot cache: every organization active with the integration on, unless told otherwise.</summary>
internal sealed class FakeSnapshots : ITenantSnapshotProvider
{
    private readonly ConcurrentDictionary<Guid, TenantSnapshot?> _overrides = new();

    public Exception? ThrowOnGet { get; set; }

    public void Set(Guid organizationId, TenantSnapshot? snapshot) => _overrides[organizationId] = snapshot;

    public static TenantSnapshot Active(params string[] features) =>
        new(true, new HashSet<string>(features.Length == 0 ? ["MODULE_INTEGRATION"] : features));

    public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId)
    {
        if (ThrowOnGet is not null) throw ThrowOnGet;
        return Task.FromResult(_overrides.TryGetValue(organizationId, out var snapshot) ? snapshot : Active());
    }

    public void Invalidate(Guid organizationId) { }
}

/// <summary>Keeps every log line (formatted, with its exception) so a test can prove what never reaches a log.</summary>
internal sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    public string All => string.Join("\n", Lines);

    private sealed class Logger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;
        public Logger(CapturingLoggerProvider owner, string category) { _owner = owner; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _owner.Lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} {exception}");
    }
}

internal static class SampleReference
{
    public static RemoteReferenceData Data(
        string homeCurrency = "PKR", bool multiCurrency = false, bool customTxnNumbers = true, string country = "PK",
        string companyName = "Sandbox Company PK") => new()
    {
        Accounts =
        [
            new RemoteAccount("1",  "Sales",              "Income",             "SalesOfProductIncome", "Revenue", homeCurrency, true),
            new RemoteAccount("2",  "Other Income",       "Other Income",       null,                   "Revenue", homeCurrency, true),
            new RemoteAccount("3",  "Office Expenses",    "Expense",            null,                   "Expense", homeCurrency, true),
            new RemoteAccount("4",  "Cost of Sales",      "Cost of Goods Sold", null,                   "Expense", homeCurrency, true),
            new RemoteAccount("5",  "Freight",            "Other Expense",      null,                   "Expense", homeCurrency, true),
            new RemoteAccount("6",  "Bank",               "Bank",               null,                   "Asset",   homeCurrency, true),
            new RemoteAccount("7",  "Old Sales",          "Income",             null,                   "Revenue", homeCurrency, false),
            new RemoteAccount("8",  "Purchases (enum)",   "CostofGoodsSold",    null,                   "Expense", homeCurrency, true)
        ],
        TaxCodes =
        [
            new RemoteTaxCode("10", "GST 17%", "Standard", true,  17m, true),
            new RemoteTaxCode("11", "Exempt",  null,       false, 0m,  true),
            new RemoteTaxCode("12", "Old 16%", null,       true,  16m, false)
        ],
        Terms =
        [
            new RemoteTerm("20", "Net 30", 30, true),
            new RemoteTerm("21", "Net 60", 60, true),
            new RemoteTerm("22", "Old",    90, false)
        ],
        Currencies  = [new RemoteCurrency("PKR", "Pakistani Rupee"), new RemoteCurrency("USD", "US Dollar")],
        Preferences = new RemotePreferences(homeCurrency, multiCurrency, customTxnNumbers, true) { DiscountsEnabled = true },
        CompanyInfo = new RemoteCompanyInfo(companyName, companyName + " Ltd", country, "books@example.com")
    };
}

/// <summary>
/// A DI container wired through the module's own <c>AddIntegrationConnections</c>, with the externals
/// replaced by the fakes above. Scopes are created per organization (or anonymous, with the real
/// <c>TenantContext</c>).
/// </summary>
internal sealed class ConnectionsHarness : IAsyncDisposable
{
    public string                   DbName     { get; } = Guid.NewGuid().ToString();
    public ServiceProvider          Root       { get; }
    public FakeAuthProvider         Auth       { get; } = new();
    public Mock<IAccountingProvider> Provider  { get; } = new();
    public RecordingOutbox          Outbox     { get; } = new();
    public FakeSnapshots            Snapshots  { get; } = new();
    public bool                     RealTenant { get; }
    public LocalDb?                 Sql        { get; }

    private ConnectionsHarness(
        bool realTenant, Action<QuickBooksOptions>? qbo, Action<IntegrationJobOptions>? jobs,
        Action<IServiceCollection>? extra, LocalDb? sql, bool registerOutbox)
    {
        RealTenant = realTenant;
        Sql        = sql;

        Provider.SetupGet(p => p.ProviderKey).Returns(ProviderKeys.QuickBooksOnline);
        Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderResult<RemoteReferenceData>.Ok(SampleReference.Data()));
        Provider.Setup(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderResult<RemoteCompanyInfo>.Ok(new RemoteCompanyInfo("Sandbox Company PK", null, "PK", null)));

        var registry = new Mock<IAccountingProviderRegistry>();
        registry.Setup(r => r.Get(It.IsAny<string>())).Returns(() => Provider.Object);

        var services = new ServiceCollection();
        services.AddLogging();

        if (realTenant)
        {
            services.AddTenantContext();
        }
        else
        {
            services.AddScoped<StaticTenantContext>();
            services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<StaticTenantContext>());
        }

        services.AddDbContext<IntegrationDbContext>(o =>
        {
            if (sql is not null) o.UseSqlServer(sql.ConnectionString);
            else                 o.UseInMemoryDatabase(DbName, TestDb.Root);
        });

        services.Configure<QuickBooksOptions>(o =>
        {
            o.ClientId          = "test-client-id";
            o.ClientSecret      = "test-client-secret";
            o.RedirectUri       = "https://localhost:7001/api/integrations/quickbooks/callback";
            o.FrontendReturnUrl = "http://localhost:4200/portal/pages/integrations/quickbooks";
            o.Environment       = "Sandbox";
            o.MinorVersion      = "75";
            qbo?.Invoke(o);
        });
        services.Configure<IntegrationJobOptions>(o => jobs?.Invoke(o));

        services.AddSingleton(TestEncryption.Instance);
        services.AddScoped<IConnectionAccessor, ConnectionAccessor>();
        services.AddIntegrationConnections(new ConfigurationBuilder().Build());

        services.RemoveAll<IAccountingAuthProvider>();
        services.AddSingleton<IAccountingAuthProvider>(Auth);
        services.AddSingleton(registry.Object);
        if (registerOutbox) services.AddSingleton<IOutboxControl>(Outbox);
        services.AddSingleton<ITenantSnapshotProvider>(Snapshots);

        extra?.Invoke(services);

        Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static ConnectionsHarness Create(
        bool realTenant = false, Action<QuickBooksOptions>? qbo = null, Action<IntegrationJobOptions>? jobs = null,
        Action<IServiceCollection>? extra = null, LocalDb? sql = null, bool registerOutbox = true) =>
        new(realTenant, qbo, jobs, extra, sql, registerOutbox);

    /// <summary>A scope acting as a signed-in user of this organization.</summary>
    public AsyncServiceScope Scope(Guid organizationId)
    {
        var scope = Root.CreateAsyncScope();
        if (RealTenant)
        {
            var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("organizationId", organizationId.ToString()), new System.Security.Claims.Claim("sub", "7")],
                "Test"));
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = user };
        }
        else
        {
            scope.ServiceProvider.GetRequiredService<StaticTenantContext>().OrganizationId = organizationId;
        }
        return scope;
    }

    /// <summary>A scope for an anonymous HTTP request (the OAuth callback): no user, the real TenantContext.</summary>
    public AsyncServiceScope AnonymousScope()
    {
        if (!RealTenant) throw new InvalidOperationException("Anonymous scopes need the real TenantContext.");
        var scope = Root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        return scope;
    }

    /// <summary>A context outside DI, as that organization (or across all of them).</summary>
    public IntegrationDbContext OpenAs(Guid organizationId, bool superAdmin = false) =>
        Sql is not null ? Sql.OpenAs(organizationId, superAdmin) : TestDb.OpenAs(DbName, organizationId, superAdmin);

    public IntegrationDbContext OpenAll() => OpenAs(Guid.Empty, superAdmin: true);

    /// <summary>A connection with real encrypted tokens.</summary>
    public async Task<IntegrationConnection> SeedConnectionAsync(
        Guid organizationId,
        ConnectionStatus status     = ConnectionStatus.Live,
        string?          realmId    = "9130000000000001",
        string           access     = "AT-seed",
        string           refresh    = "RT-seed",
        DateTime?        accessExpiresAt  = null,
        DateTime?        refreshExpiresAt = null,
        bool             withTokens = true,
        int?             connectedBy = 7)
    {
        await using var db = OpenAs(organizationId);
        var connection = new IntegrationConnection
        {
            OrganizationId    = organizationId,
            RealmId           = realmId,
            CompanyName       = "Seeded Company",
            Status            = status,
            HomeCurrencyCode  = "PKR",
            Country           = "PK",
            ConnectedAt       = DateTime.UtcNow.AddDays(-10),
            ConnectedByUserId = connectedBy
        };
        if (withTokens)
            new CredentialVault(TestEncryption.Instance).Store(connection, new TokenGrant(
                access, refresh,
                accessExpiresAt  ?? DateTime.UtcNow.AddHours(1),
                refreshExpiresAt ?? DateTime.UtcNow.AddDays(90)));

        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    public async Task<IntegrationSettings> SeedSettingsAsync(IntegrationConnection connection, Action<IntegrationSettings>? configure = null)
    {
        await using var db = OpenAs(connection.OrganizationId);
        var settings = new IntegrationSettings { OrganizationId = connection.OrganizationId, ConnectionId = connection.Id };
        configure?.Invoke(settings);
        db.Settings.Add(settings);
        await db.SaveChangesAsync();
        return settings;
    }

    public async Task<IntegrationConnection> ReloadConnectionAsync(int id)
    {
        await using var db = OpenAll();
        return await db.Connections.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
    }

    public async ValueTask DisposeAsync()
    {
        await Root.DisposeAsync();
    }
}
