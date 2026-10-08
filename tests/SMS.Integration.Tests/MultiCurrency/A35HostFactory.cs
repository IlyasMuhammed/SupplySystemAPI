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
using SMS.Modules.Inventory.Data;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35 (QA) — the real Program.cs host on a LocalDB database whose name the test owns, so the same database can be booted
/// several times (deploy, roll the A35 migrations back, boot again = a pre-A35 database with real data upgraded by the real
/// startup). <see cref="CreateDatabaseAsync"/> builds the empty database the way <c>SapWebApplicationFactory</c> does (model
/// tables + stamped history for the modules whose chains do not replay); <see cref="StartAsync"/> boots the host, which runs
/// every module's Migrate() and startup seeders/bootstrappers. Dispose stops the host; <see cref="DropDatabaseAsync"/> drops.
/// </summary>
internal sealed class A35HostFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@sms.local";
    public const string AdminPassword = "Admin@12345";
    private const string TestSecret = "a35-migration-integration-test-jwt-secret-min-32!";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const string Master = "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    public string DbName { get; }
    public string Cs => $"Server=(localdb)\\mssqllocaldb;Database={DbName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

    public A35HostFactory(string dbName)
    {
        DbName = dbName;
        Environment.SetEnvironmentVariable("Data__mainOrg", Cs);
        Environment.SetEnvironmentVariable("AppSettings__Secret", TestSecret);
        Environment.SetEnvironmentVariable("AppSettings__AppSupportEmail", "test@test.com");
        Environment.SetEnvironmentVariable("AppSettings__BaseUrl", "http://localhost");
    }

    public string AdminToken { get; private set; } = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(new NoMail());
            // Hangfire's static log provider still points at the previous (disposed) host's LoggerFactory when this test boots
            // a second host in the same process; reset it before Hangfire builds its storage.
            Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);
            GlobalConfiguration.Configuration.UseInMemoryStorage(new InMemoryStorageOptions());
        });

    public static async Task CreateDatabaseAsync(string dbName)
    {
        await using (var master = new SqlConnection(Master))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{dbName}]";
            await create.ExecuteNonQueryAsync();
        }

        var cs = $"Server=(localdb)\\mssqllocaldb;Database={dbName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
        async Task Ensure<TContext>(Func<DbContextOptions<TContext>, TContext> factory) where TContext : DbContext
        {
            await using var db = factory(new DbContextOptionsBuilder<TContext>().UseSqlServer(cs).Options);
            await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL " +
                "CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL, [ProductVersion] nvarchar(32) NOT NULL, " +
                "CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));");
            var version = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) " +
                    "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, {1})", id, version);
        }

        await Ensure<LookupsDbContext>(o => new LookupsDbContext(o, new StaticTenantContext()));
        await Ensure<InventoryDbContext>(o => new InventoryDbContext(o, new StaticTenantContext()));
        await Ensure<DemandDbContext>(o => new DemandDbContext(o, new StaticTenantContext()));
        await Ensure<WarehouseDbContext>(o => new WarehouseDbContext(o, new StaticTenantContext()));
        await Ensure<MaterialDbContext>(o => new MaterialDbContext(o, new StaticTenantContext()));
        await Ensure<FinanceDbContext>(o => new FinanceDbContext(o, new StaticTenantContext()));
        await Ensure<SuppliersDbContext>(o => new SuppliersDbContext(o, new StaticTenantContext()));
        await Ensure<ReportsDbContext>(o => new ReportsDbContext(o, new StaticTenantContext()));
    }

    /// <summary>Boots the host (every module's Migrate + seeders) and logs the seeded admin in.</summary>
    public async Task StartAsync()
    {
        _ = Server;
        using var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { Email = AdminEmail, Password = AdminPassword });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        AdminToken = body.GetProperty("result").GetProperty("accessToken").GetString()!;
    }

    public HttpClient Admin()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return c;
    }

    public DbContextOptions<T> Options<T>() where T : DbContext => new DbContextOptionsBuilder<T>().UseSqlServer(Cs).Options;


    public static async Task<List<Dictionary<string, object?>>> QueryAsync(string cs, string sql, params (string, object)[] ps)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static async Task<int> ExecuteAsync(string cs, string sql, params (string, object)[] ps)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string dbName)
    {
        try
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(Master);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best effort
        }
    }

    private sealed class NoMail : IEmailService
    {
        public void SendActivationEmail(string email, string name, string token) { }
        public void SendPasswordResetEmail(string email, string code) { }
        public void SendTemporaryPasswordEmail(string email, string name, string tempPassword) { }
        public void SendOrgAdminInviteEmail(string email, string name, string orgName, string token) { }
    }
}
