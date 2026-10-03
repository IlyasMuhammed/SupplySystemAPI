using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Auth.Services;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.QuickBooks;

/// <summary>
/// The real Program.cs host — every module, real HTTP, real SCM triggers, the real Integration module
/// and its own migration — against a throwaway LocalDB database, with only the Intuit boundary faked
/// (<see cref="FakeQuickBooksCompany"/>, <see cref="FakeQuickBooksAuth"/>).
/// <para>
/// Same database technique as <c>ProcurementCycleWebApplicationFactory</c> (read its comments): env vars
/// for the eagerly-read settings, and EnsureCreated + stamped history for the modules whose migration
/// chains don't replay from empty. The Integration module is deliberately NOT stamped — its own
/// migration runs for real on startup, which is part of what this proves.
/// </para>
/// <para>
/// Every QuickBooks recurring job is scheduled for 31 February (never), so nothing runs behind the
/// test's back; the tests run the jobs directly, in the order they choose.
/// </para>
/// </summary>
public sealed class QuickBooksWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string AdminEmail    = "admin@sms.local";
    private const string AdminPassword = "Admin@12345";
    private const string TestSecret    = "qbi-e2e-integration-test-jwt-secret-min-32-chars!";
    private const string Never         = "0 0 31 2 *";

    public const string ReturnUrl = "http://localhost:4200/portal/pages/integrations/quickbooks";

    private readonly string _dbName = $"SMS_QBI_E2E_{Guid.NewGuid():N}";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] EnvVars =
    [
        "Data__mainOrg", "AppSettings__Secret", "AppSettings__AppSupportEmail", "AppSettings__BaseUrl",
        "QuickBooks__ClientId", "QuickBooks__ClientSecret", "QuickBooks__RedirectUri", "QuickBooks__FrontendReturnUrl",
        "QuickBooks__Environment",
        "Integration__Jobs__OutboxCron", "Integration__Jobs__SweepCron", "Integration__Jobs__ReconciliationCron",
        "Integration__Jobs__TokenRefreshCron", "Integration__Jobs__ReferenceRefreshCron", "Integration__Jobs__CleanupCron"
    ];

    internal FakeQuickBooksCompany Company { get; } = new();
    internal FakeQuickBooksAuth    Auth    { get; } = new();

    public string AdminAccessToken { get; private set; } = string.Empty;
    public Guid   OrganizationId   { get; private set; }

    private string DbConnectionString =>
        $"Server=(localdb)\\mssqllocaldb;Database={_dbName};Trusted_Connection=True;MultipleActiveResultSets=true";

    private const string MasterConnectionString = "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    public QuickBooksWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Data__mainOrg", DbConnectionString);
        Environment.SetEnvironmentVariable("AppSettings__Secret", TestSecret);
        Environment.SetEnvironmentVariable("AppSettings__AppSupportEmail", "test@test.com");
        Environment.SetEnvironmentVariable("AppSettings__BaseUrl", "http://localhost");

        // App keys present (IsConfigured), Sandbox, and the screen the callback returns to.
        Environment.SetEnvironmentVariable("QuickBooks__ClientId", "e2e-client-id");
        Environment.SetEnvironmentVariable("QuickBooks__ClientSecret", "e2e-client-secret");
        Environment.SetEnvironmentVariable("QuickBooks__RedirectUri", "http://localhost/api/integrations/quickbooks/callback");
        Environment.SetEnvironmentVariable("QuickBooks__FrontendReturnUrl", ReturnUrl);
        Environment.SetEnvironmentVariable("QuickBooks__Environment", "Sandbox");

        foreach (var cron in new[] { "OutboxCron", "SweepCron", "ReconciliationCron", "TokenRefreshCron", "ReferenceRefreshCron", "CleanupCron" })
            Environment.SetEnvironmentVariable($"Integration__Jobs__{cron}", Never);
    }

    public async Task InitializeAsync()
    {
        await using (var master = new SqlConnection(MasterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_dbName}]";
            await create.ExecuteNonQueryAsync();
        }

        async Task EnsureCreatedAndStampHistoryAsync<TContext>(Func<DbContextOptions<TContext>, TContext> factory) where TContext : DbContext
        {
            var opts = new DbContextOptionsBuilder<TContext>().UseSqlServer(DbConnectionString).Options;
            await using var db = factory(opts);
            await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL " +
                "CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL, [ProductVersion] nvarchar(32) NOT NULL, " +
                "CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));");
            var productVersion = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
            foreach (var migrationId in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) " +
                    "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, {1})",
                    migrationId, productVersion);
        }

        await EnsureCreatedAndStampHistoryAsync<LookupsDbContext>(o => new LookupsDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<InventoryDbContext>(o => new InventoryDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<DemandDbContext>(o => new DemandDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<WarehouseDbContext>(o => new WarehouseDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<MaterialDbContext>(o => new MaterialDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<FinanceDbContext>(o => new FinanceDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<SuppliersDbContext>(o => new SuppliersDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<ReportsDbContext>(o => new ReportsDbContext(o, new StaticTenantContext()));

        _ = Server; // builds the host: every Use*Module() migrates/seeds, UseIntegrationModule() runs its migration

        using var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { Email = AdminEmail, Password = AdminPassword });
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        AdminAccessToken = body.GetProperty("result").GetProperty("accessToken").GetString()!;
        OrganizationId = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(AdminAccessToken).Claims.First(c => c.Type == "organizationId").Value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(new NoOpEmailService());
            // The other mail senders (NotificationService, EmailSender) are disarmed assembly-wide: see NoRealMail.

            // The Intuit boundary — the only fakes in the stack.
            services.RemoveAll<IAccountingProvider>();
            services.AddSingleton<IAccountingProvider>(Company);
            services.RemoveAll<IAccountingAuthProvider>();
            services.AddSingleton<IAccountingAuthProvider>(Auth);

            GlobalConfiguration.Configuration.UseInMemoryStorage(new InMemoryStorageOptions());
        });
    }

    public HttpClient CreateAdminClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminAccessToken);
        return client;
    }

    /// <summary>No JWT, no redirects followed — what Intuit's redirect and an external system look like.</summary>
    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<T> ReadResultAsync<T>(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {json}");
        var wrapped = JsonSerializer.Deserialize<Envelope<T>>(json, Json)
            ?? throw new InvalidOperationException($"Could not deserialize: {json}");
        if (!wrapped.Success) throw new InvalidOperationException($"API call failed: {wrapped.Message}\nRaw: {json}");
        return wrapped.Result!;
    }

    /// <summary>Runs a job (or anything else) from a fresh DI scope, as Hangfire would.</summary>
    internal async Task RunInScopeAsync<T>(Func<T, Task> run) where T : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        await run(scope.ServiceProvider.GetRequiredService<T>());
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public new async Task DisposeAsync()
    {
        Dispose();
        foreach (var name in EnvVars) Environment.SetEnvironmentVariable(name, null);

        try
        {
            await using var conn = new SqlConnection(MasterConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort, as in ProcurementCycleWebApplicationFactory.
        }
    }

    private sealed class Envelope<T>
    {
        public bool    Success { get; set; }
        public string  Message { get; set; } = string.Empty;
        public T?      Result  { get; set; }
    }

    private sealed class NoOpEmailService : IEmailService
    {
        public void SendActivationEmail(string email, string name, string token) { }
        public void SendPasswordResetEmail(string email, string code) { }
        public void SendTemporaryPasswordEmail(string email, string name, string tempPassword) { }
        public void SendOrgAdminInviteEmail(string email, string name, string orgName, string token) { }
    }
}
