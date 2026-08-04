using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace SMS.Integration.Tests.MultiTenancy;

// MT-008 — "query filter on organization_id uses the index and does not degrade query
// performance vs pre-migration baseline". Two complementary checks, since the dev DB's tables are
// only tens to low-hundreds of rows: at that volume, SQL Server's optimizer can legitimately
// prefer a full scan over a seek regardless of what indexes exist (a scan is cheaper for tiny
// tables), so asserting "the plan uses Index Seek" would be either false or a flaky over-fit to
// today's row counts. What's both meaningful and stable regardless of data volume is:
//
//   1. Structural: every tenant-scoped table's OrganizationId column actually has a supporting
//      index (so the optimizer *can* seek once row counts grow — this is the real, durable
//      guarantee the ticket is asking to verify).
//   2. A smoke-level timing sanity check against a real representative query, as a coarse
//      regression guard rather than a rigorous benchmark.
public class QueryFilterPerformanceTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private string _connectionString = null!;

    public QueryFilterPerformanceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public Task InitializeAsync()
    {
        var config = _factory.Services.GetService(typeof(IConfiguration)) as IConfiguration;
        _connectionString = config!["Data:mainOrg"]!;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("inventory", "Products")]
    [InlineData("suppliers", "Suppliers")]
    [InlineData("demand", "purchase_orders")]
    [InlineData("finance", "invoices")]
    [InlineData("workflow_schema", "workflow_definitions")]
    [InlineData("auth", "UserAccounts")]
    public async Task TenantScopedTable_HasASupportingIndexOnOrganizationId(string schema, string table)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sys.index_columns ic
            JOIN sys.indexes i        ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t         ON t.object_id = ic.object_id
            JOIN sys.schemas s        ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table AND c.name = 'OrganizationId'
              AND ic.key_ordinal = 1  -- OrganizationId is the leading column of the index
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var indexCount = (int)(await cmd.ExecuteScalarAsync())!;
        indexCount.Should().BeGreaterThan(0,
            $"{schema}.{table} is queried by OrganizationId on every request (tenant query filter) " +
            "and must have a supporting index for that to scale past today's row counts");
    }

    [Fact]
    public async Task RepresentativeTenantScopedQuery_CompletesWellWithinASanityBound()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var scmDemoOrgId = await GetScmDemoOrgIdAsync(conn);

        // Not a rigorous benchmark (shared dev DB, tiny data volume) — a coarse smoke check that
        // the additional WHERE organization_id = @org clause hasn't introduced anything
        // pathological (e.g. a missing index forcing a scan-and-filter across every schema).
        var sw = Stopwatch.StartNew();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM inventory.Products WHERE OrganizationId = @org";
            cmd.Parameters.AddWithValue("@org", scmDemoOrgId);
            await cmd.ExecuteScalarAsync();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "a single indexed-column filter on a small table should never take anywhere near this long");
    }

    private static async Task<Guid> GetScmDemoOrgIdAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM tenant.Organizations WHERE OrgCode = 'SCM-DEMO'";
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}
