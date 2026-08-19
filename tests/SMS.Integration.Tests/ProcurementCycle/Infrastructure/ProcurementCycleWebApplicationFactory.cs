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
using Microsoft.EntityFrameworkCore.Migrations;
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
using Xunit;

namespace SMS.Integration.Tests.ProcurementCycle.Infrastructure;

// PV-009 — full procurement-to-issue cycle at variant level, driven entirely over real HTTP
// against the real Program.cs pipeline (every UseXModule() migration/seeder runs exactly as in
// production). Docker/Testcontainers is not available in this environment (confirmed — `docker`
// isn't on PATH), so unlike WorkflowWebApplicationFactory this points Data:mainOrg at a
// throwaway, uniquely-named LocalDB database instead of a SQL Server container. Torn down in
// DisposeAsync.
//
// Deliberately does NOT stub IUserQueryService/IOrgChartService/IDocumentStatusService the way
// WorkflowWebApplicationFactory does for its narrower workflow-engine-only tests — this test
// exercises the real PoStatusHandler/GrnStatusHandler/GrnQcStatusHandler/MirProjectStatusHandler
// chain that actually posts stock and flips document status, so those must resolve to their
// production implementations.
//
// Uses process environment variables — not WebApplicationFactory.ConfigureWebHost's
// ConfigureAppConfiguration — to redirect Data:mainOrg/AppSettings:Secret. Program.cs reads both
// eagerly into local variables (`connString` at line ~90 for Hangfire's UseSqlServerStorage,
// `appSettings.Secret` at line ~142 for JWT bearer's IssuerSigningKey) BEFORE builder.Build() —
// WebApplicationFactory's deferred-host config injection only takes effect at/around Build()
// itself, too late for those two eager reads (confirmed the hard way: with the config-builder
// approach, Hangfire tried to reach the *real* appsettings.json server, not this throwaway DB).
// Environment variables are visible from the very first line of WebApplication.CreateBuilder(args)
// onward, so they reach both the eager and the DI-lazy reads uniformly. Same technique used
// manually all session for `dotnet run`/`dotnet ef` against this repo's real dev API.
//
// Known limitation: these are process-wide env vars, restored (removed) in DisposeAsync, but not
// safe against true concurrent execution with another test in the same process that also boots a
// plain WebApplicationFactory<Program> (AuthIntegrationTests/SystemAdminAuthorizationTests, in
// this same test project) — xUnit runs different test classes' collections in parallel by default.
// Not addressed here (would require assembly-wide DisableTestParallelization, a bigger-footprint
// change than this ticket asked for); run this test class in isolation if that's a concern.
public sealed class ProcurementCycleWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string AdminEmail    = "admin@sms.local";
    private const string AdminPassword = "Admin@12345";
    private const string TestSecret    = "pv009-integration-test-jwt-secret-min-32-chars-long!";

    private readonly string _dbName = $"SMS_PV009_{Guid.NewGuid():N}";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string DbConnectionString =>
        $"Server=(localdb)\\mssqllocaldb;Database={_dbName};Trusted_Connection=True;MultipleActiveResultSets=true";

    private const string MasterConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    // Populated in InitializeAsync — the single admin JWT reused for every call in the test
    // (workflow-engine's SystemAdmin override lets one user singlehandedly submit + approve every
    // tier of every PO/GRN/MIR workflow, sidestepping org-chart/role-membership setup entirely).
    public string AdminAccessToken { get; private set; } = string.Empty;
    public int    AdminUserId      { get; private set; }

    public ProcurementCycleWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Data__mainOrg", DbConnectionString);
        Environment.SetEnvironmentVariable("AppSettings__Secret", TestSecret);
        Environment.SetEnvironmentVariable("AppSettings__AppSupportEmail", "test@test.com");
        Environment.SetEnvironmentVariable("AppSettings__BaseUrl", "http://localhost");
    }

    public async Task InitializeAsync()
    {
        // EF Core's own Database.Migrate() calls (fired later, from the UseXModule() calls below)
        // auto-create a missing target database. Hangfire's SqlServerStorage does not — it opens a
        // connection straight to the named database and fails immediately (error 4060) if it
        // doesn't exist yet — and Program.cs's AddHangfire(...).UseSqlServerStorage(...) runs
        // before any UseXModule() call. Create the (empty) database explicitly first.
        await using (var masterConn = new SqlConnection(MasterConnectionString))
        {
            await masterConn.OpenAsync();
            await using var createCmd = masterConn.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE [{_dbName}]";
            await createCmd.ExecuteNonQueryAsync();
        }

        // Five modules' migration histories turn out not to be cleanly replayable from a
        // genuinely empty database (only discovered by actually trying — SMS_Dev, the only
        // database anyone has run this app against, was built up incrementally, one migration at a
        // time, over months, so gaps like these were never exercised there):
        //   - Lookups: has migrations, but LookupsModuleExtensions.UseLookupsModule() never calls
        //     Database.Migrate() at all (only seeds) — yet Program.cs calls it unconditionally and
        //     it immediately queries lookups.LookupTypes.
        //   - Warehouse: its earliest migration in source control, 20260608104012_AddSroSchema,
        //     assumes the `warehouse` schema plus grns/grn_lines tables already exist ("grns and
        //     grn_lines already exist from InitialWarehouseSchema" per its own comment) — but no
        //     InitialWarehouseSchema migration exists anywhere in this module's Migrations folder.
        //     That foundational migration was evidently deleted/squashed from source control at
        //     some point after being applied to SMS_Dev; only the *file* is gone, not its effect on
        //     that one already-migrated database.
        //   - Material: at least one migration (20260729091255_AddOrganizationIdTenantScoping)
        //     drops an index that a similarly-missing earlier migration was supposed to have
        //     created.
        //   - Demand/Finance: migrate cleanly on their own, but SMS.WorkflowEngine's own
        //     20260803132211_BackfillDocumentTimelineOrganizationId migration cross-schema JOINs
        //     demand.purchase_requisitions/quotations/purchase_orders, warehouse.grns,
        //     material.material_issue_requests, and finance.invoices — and UseWorkflowEngineModule()
        //     runs *before* UseDemandModule/UseWarehouseModule/UseMaterialModule/UseFinanceModule in
        //     Program.cs, so that migration fails with "Invalid object name" on a fresh database
        //     for all four, regardless of whether each one's own migrations are individually sound.
        //
        // All of this is directly relevant to the Azure deployment this user is separately mid-way
        // through this session — a fresh Azure SQL database would hit the exact same walls.
        //
        // One uniform workaround sidesteps every case: build each context's schema straight from
        // the *current* EF model (EnsureCreated — same technique this test project's own
        // WorkflowWebApplicationFactory already uses for WorkflowDbContext, for likely the same
        // underlying reason), then hand-stamp every migration id EF knows about into
        // __EFMigrationsHistory (a single table shared by every DbContext on this connection) so
        // each module's own later Use*Module() → Database.Migrate() call sees nothing pending
        // instead of re-running (and CREATE TABLE-colliding with) migrations whose tables already
        // exist. Do this for all five modules uniformly rather than debugging each specific
        // migration bug — new ones could easily surface the same way case by case.
        async Task EnsureCreatedAndStampHistoryAsync<TContext>(Func<DbContextOptions<TContext>, TContext> factory) where TContext : DbContext
        {
            var opts = new DbContextOptionsBuilder<TContext>().UseSqlServer(DbConnectionString).Options;
            await using var db = factory(opts);

            // NOT Database.EnsureCreatedAsync() — its "is this database already initialized" guard
            // checks for the presence of *any* tables anywhere in the database, not just this
            // context's own schema, so the second and later calls here would silently no-op once
            // the first module's tables exist. IRelationalDatabaseCreator.CreateTablesAsync() is
            // the same underlying primitive without that whole-database guard — it just creates
            // whatever this context's own model declares.
            var creator = db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync();

            await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL " +
                "CREATE TABLE [__EFMigrationsHistory] (" +
                "  [MigrationId] nvarchar(150) NOT NULL," +
                "  [ProductVersion] nvarchar(32) NOT NULL," +
                "  CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));");

            var productVersion = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
            foreach (var migrationId in db.Database.GetMigrations())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) " +
                    "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, {1})",
                    migrationId, productVersion);
            }
        }

        await EnsureCreatedAndStampHistoryAsync<LookupsDbContext>(o => new LookupsDbContext(o, new StaticTenantContext()));
        // Inventory: UseInventoryModule()'s own Database.Migrate() call gets most of the way there
        // on its own, but the current model has at least one column (ProductVariant.IsDirectConsumption)
        // with no corresponding migration file yet — same underlying "migration history doesn't
        // fully match the current model" issue as the others, just smaller in scope here.
        await EnsureCreatedAndStampHistoryAsync<InventoryDbContext>(o => new InventoryDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<DemandDbContext>(o => new DemandDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<WarehouseDbContext>(o => new WarehouseDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<MaterialDbContext>(o => new MaterialDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<FinanceDbContext>(o => new FinanceDbContext(o, new StaticTenantContext()));
        await EnsureCreatedAndStampHistoryAsync<SuppliersDbContext>(o => new SuppliersDbContext(o, new StaticTenantContext()));
        // Reports: has migrations (reports.audit_logs among them — written to by a cross-module
        // audit hook fired on PO creation) but, unlike every other module, Program.cs never calls
        // any Use*ReportsModule()-equivalent at all — only AddReportsModule() (DI registration).
        // Nothing self-migrates this schema on startup, ever, on any database.
        await EnsureCreatedAndStampHistoryAsync<ReportsDbContext>(o => new ReportsDbContext(o, new StaticTenantContext()));

        // Accessing Server forces WebApplicationFactory to build (and, via Program.cs's
        // app.UseXModule() calls) migrate + seed every module's schema against the fresh LocalDB
        // database above — no manual migration step needed here.
        _ = Server;

        // UserAccount.SupervisorId has no HTTP-settable path anywhere in the app (confirmed —
        // neither CreateUserRequest nor PatchUserRequest exposes it), but MIR's "Line Manager
        // Approval" step resolves the *submitter's* SupervisorId at submit time regardless of who
        // later approves. Self-reference the seeded admin to itself so submission never throws
        // ApproverResolutionException.
        await using (var conn = new SqlConnection(DbConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE auth.UserAccounts SET SupervisorId = UserID WHERE Email = @email";
            cmd.Parameters.AddWithValue("@email", AdminEmail);
            await cmd.ExecuteNonQueryAsync();
        }

        // Real login (not a hand-rolled JWT) so the token carries every claim TokenService
        // actually mints — organizationId, roleId, permission-per-code, is_super_admin — exactly
        // as production traffic would.
        using var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { Email = AdminEmail, Password = AdminPassword });
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        AdminAccessToken = body.GetProperty("result").GetProperty("accessToken").GetString()!;
        AdminUserId = int.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(AdminAccessToken).Claims.First(c => c.Type == "sub").Value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Placeholder-role-user creation (POST /api/users) enqueues a real
            // SendTemporaryPasswordEmail Hangfire job — swap the concrete sender for a no-op so it
            // doesn't really hit Gmail SMTP with the credentials from appsettings.json.
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(new NoOpEmailService());

            // Program.cs's own AddHangfire(...).UseSqlServerStorage(Data:mainOrg, ...) call still
            // runs (and, now that Data:mainOrg correctly points at this throwaway LocalDB via the
            // env vars above rather than an unreachable server, succeeds) — but BackgroundJobServer
            // resolves JobStorage.Current from this static config at *start* time, not at that
            // registration time, so swapping it here — same technique as
            // WorkflowWebApplicationFactory — still redirects real background processing
            // (search-index rebuilds, notifications, etc.) away from competing with this test's own
            // ~50 sequential HTTP calls for the same LocalDB's I/O.
            GlobalConfiguration.Configuration.UseInMemoryStorage(new InMemoryStorageOptions());
        });
    }

    // Returns an HttpClient pre-authenticated as the seeded SystemAdmin — every step of the
    // cycle (product/attribute/PO/GRN/MIR/MIV/reports) runs as this one user.
    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminAccessToken);
        return client;
    }

    public async Task<T> ReadResultAsync<T>(HttpResponseMessage resp)
    {
        var json    = await resp.Content.ReadAsStringAsync();
        var wrapped = JsonSerializer.Deserialize<ApiEnvelope<T>>(json, Json)
            ?? throw new InvalidOperationException($"Could not deserialize response: {json}");
        if (!wrapped.Success)
            throw new InvalidOperationException($"API call failed: {wrapped.Message}\nRaw: {json}");
        return wrapped.Result!;
    }

    // Raw ADO.NET query against the throwaway database — used for the couple of assertions/facts
    // (ProductSearchIndex row count, IsFullTextInstalled) that have no dedicated HTTP endpoint.
    public async Task<T> QueryScalarAsync<T>(string sql, Action<SqlCommand>? configure = null)
    {
        await using var conn = new SqlConnection(DbConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    public new async Task DisposeAsync()
    {
        Dispose(); // tears down the WebApplicationFactory host

        Environment.SetEnvironmentVariable("Data__mainOrg", null);
        Environment.SetEnvironmentVariable("AppSettings__Secret", null);
        Environment.SetEnvironmentVariable("AppSettings__AppSupportEmail", null);
        Environment.SetEnvironmentVariable("AppSettings__BaseUrl", null);

        try
        {
            await using var conn = new SqlConnection(MasterConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup — a leftover throwaway LocalDB database from a crashed run
            // isn't worth failing test teardown over.
        }
    }

    private sealed class ApiEnvelope<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public T? Result { get; set; }
    }

    private sealed class NoOpEmailService : IEmailService
    {
        public void SendActivationEmail(string email, string name, string token) { }
        public void SendPasswordResetEmail(string email, string code) { }
        public void SendTemporaryPasswordEmail(string email, string name, string tempPassword) { }
        public void SendOrgAdminInviteEmail(string email, string name, string orgName, string token) { }
    }
}
