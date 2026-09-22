using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// Runs the real migrations against a throwaway LocalDB database. The shared database has drifted
/// from the migrations (finding F35: indexes the history says exist are absent), so a migration that
/// drops an index by name must survive the index not being there.
/// </summary>
public sealed class TimelineTraceIdMigrationTests
{
    private const string Before = "20260918142549_RepairTenantOrganizationIndexes";
    private const string Target = "20260919134220_TimelineTraceIdUniquePerOrganization";

    [SqlServerFact]
    public async Task Up_on_a_database_built_from_the_migrations_swaps_the_unique_index_to_the_tenant_pair()
    {
        await using var db = await ThrowawayDatabase.CreateAsync();

        await db.MigrateAsync(Before);
        (await db.IndexAsync("IX_document_timelines_TraceId")).Should().Be(IndexShape.Unique);

        await db.MigrateAsync(Target);

        (await db.IndexAsync("IX_document_timelines_OrganizationId_TraceId")).Should().Be(IndexShape.Unique);
        (await db.IndexAsync("IX_document_timelines_TraceId")).Should().Be(IndexShape.NonUnique);
    }

    [SqlServerFact]
    public async Task Up_on_a_drifted_database_where_the_trace_id_index_is_already_missing_still_succeeds()
    {
        await using var db = await ThrowawayDatabase.CreateAsync();

        await db.MigrateAsync(Before);
        await db.ExecuteAsync("DROP INDEX [IX_document_timelines_TraceId] ON [workflow_schema].[document_timelines]");
        (await db.IndexAsync("IX_document_timelines_TraceId")).Should().Be(IndexShape.Missing);

        await db.MigrateAsync(Target);

        (await db.IndexAsync("IX_document_timelines_OrganizationId_TraceId")).Should().Be(IndexShape.Unique);
        (await db.IndexAsync("IX_document_timelines_TraceId")).Should().Be(IndexShape.NonUnique);
    }

    [SqlServerFact]
    public async Task A_drifted_database_carries_on_to_the_latest_migration()
    {
        await using var db = await ThrowawayDatabase.CreateAsync();

        await db.MigrateAsync(Before);
        await db.ExecuteAsync("DROP INDEX [IX_document_timelines_TraceId] ON [workflow_schema].[document_timelines]");

        await db.MigrateAsync(null);

        (await db.PendingAsync()).Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task Down_restores_the_global_unique_trace_id_index()
    {
        await using var db = await ThrowawayDatabase.CreateAsync();

        await db.MigrateAsync(Target);
        await db.MigrateAsync(Before);

        (await db.IndexAsync("IX_document_timelines_TraceId")).Should().Be(IndexShape.Unique);
        (await db.IndexAsync("IX_document_timelines_OrganizationId_TraceId")).Should().Be(IndexShape.Missing);
    }

    private enum IndexShape { Missing, NonUnique, Unique }

    private sealed class ThrowawayDatabase : IAsyncDisposable
    {
        private readonly string _name;
        private readonly string _connectionString;

        private ThrowawayDatabase(string name, string connectionString)
        {
            _name = name;
            _connectionString = connectionString;
        }

        internal static string MasterConnectionString =>
            Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
            ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

        internal static async Task<ThrowawayDatabase> CreateAsync()
        {
            var name = $"SMS_WfMig_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };

            await using (var conn = new SqlConnection(builder.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{name}]";
                await cmd.ExecuteNonQueryAsync();
            }

            builder.InitialCatalog = name;
            var database = new ThrowawayDatabase(name, builder.ConnectionString);
            await database.ExecuteAsync(SiblingModuleTables);
            return database;
        }

        // 20260803132211_BackfillDocumentTimelineOrganizationId joins these other modules' tables; empty
        // stand-ins are enough for it, and a real database has the real ones by then.
        private const string SiblingModuleTables = """
            EXEC(N'CREATE SCHEMA demand');
            EXEC(N'CREATE SCHEMA warehouse');
            EXEC(N'CREATE SCHEMA material');
            EXEC(N'CREATE SCHEMA finance');
            CREATE TABLE demand.purchase_requisitions    (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            CREATE TABLE demand.quotations               (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            CREATE TABLE demand.purchase_orders          (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            CREATE TABLE warehouse.grns                  (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            CREATE TABLE material.material_issue_requests (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            CREATE TABLE finance.invoices                (TraceId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL);
            """;

        private WorkflowDbContext NewContext() =>
            new(new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlServer(_connectionString).Options,
                new StaticTenantContext());

        /// <summary>Migrates to <paramref name="target"/>, or to the latest when it is null.</summary>
        internal async Task MigrateAsync(string? target)
        {
            await using var context = NewContext();
            await context.GetService<IMigrator>().MigrateAsync(target);
        }

        internal async Task<IReadOnlyList<string>> PendingAsync()
        {
            await using var context = NewContext();
            return (await context.Database.GetPendingMigrationsAsync()).ToList();
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        internal async Task<IndexShape> IndexAsync(string indexName)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT i.is_unique
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'[workflow_schema].[document_timelines]') AND i.name = @name
                """;
            cmd.Parameters.AddWithValue("@name", indexName);
            var unique = await cmd.ExecuteScalarAsync();

            return unique is null ? IndexShape.Missing : (bool)unique ? IndexShape.Unique : IndexShape.NonUnique;
        }

        public async ValueTask DisposeAsync()
        {
            var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };

            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Skips when no SQL Server is reachable, so the suite still runs on a machine without LocalDB.</summary>
    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            try
            {
                using var conn = new SqlConnection(ThrowawayDatabase.MasterConnectionString);
                conn.Open();
            }
            catch
            {
                Skip = "No SQL Server / LocalDB available.";
            }
        }
    }
}
