using DotNet.Testcontainers.Builders;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using Testcontainers.MsSql;
using Xunit;

namespace SMS.Integration.Tests.Workflow.Infrastructure;

public sealed class WorkflowWebApplicationFactory
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const string TestSecret  = "integration-test-jwt-secret-min-32-chars!";
    internal const int    User1       = 101;
    internal const int    User2       = 102;
    internal const int    User3       = 103;

    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Integration_Test1!")
        .Build();

    // Shared singleton clone tracker — injected as scoped in ConfigureTestServices,
    // but constructed once here so tests can read LastClonedId.
    internal readonly TrackingDocumentCloneService CloneService = new();

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();

        // Run auth migrations BEFORE host build so AuthDataSeeder finds its tables.
        var authOpts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(_sql.GetConnectionString())
            .Options;
        await using var authDb = new AuthDbContext(authOpts);
        await authDb.Database.MigrateAsync();

        // Build host (triggers ConfigureWebHost + UseAuthModule seeder + UseLookupsModule seeder).
        // Then create workflow schema (no migrations — uses EnsureCreated).
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await db.Database.EnsureCreatedAsync();
        await WorkflowDbSeed.SeedAsync(db);
    }

    public new async Task DisposeAsync()
    {
        Dispose(); // tears down the WebApplicationFactory host
        await _sql.DisposeAsync();
    }

    // ── WebApplicationFactory override ────────────────────────────────────────

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:mainOrg"]                = _sql.GetConnectionString(),
                ["AppSettings:Secret"]          = TestSecret,
                ["AppSettings:AppSupportEmail"] = "test@test.com",
                ["AppSettings:BaseUrl"]         = "http://localhost",
            }));

        builder.ConfigureTestServices(services =>
        {
            // Switch Hangfire to InMemory immediately so UseAuthModule()'s
            // RecurringJob.AddOrUpdate and all BackgroundJob.Schedule calls work.
            GlobalConfiguration.Configuration.UseInMemoryStorage(new InMemoryStorageOptions());

            // Replace cross-module services with lightweight stubs.
            services.RemoveAll<IUserQueryService>();
            services.AddSingleton<IUserQueryService>(new StubUserQueryService());

            services.RemoveAll<IOrgChartService>();
            services.AddSingleton<IOrgChartService>(new StubOrgChartService());

            services.RemoveAll<INotificationService>();
            services.AddSingleton<INotificationService>(new StubNotificationService());

            services.RemoveAll<IDocumentStatusService>();
            services.AddScoped<IDocumentStatusService>(_ => new NoOpDocumentStatusService());

            services.RemoveAll<IDocumentCloneService>();
            services.AddScoped<IDocumentCloneService>(_ => CloneService);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal HttpClient CreateClientFor(int userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtHelper.GenerateToken(userId, TestSecret));
        return client;
    }

    internal async Task<T?> DbQueryAsync<T>(Func<WorkflowDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return await query(db);
    }
}
