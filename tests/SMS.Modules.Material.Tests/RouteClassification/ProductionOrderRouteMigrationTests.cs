using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Material.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 PE-01 — <c>A34_ProductionOrderRouteAndDelivery</c>: four nullable columns on production_orders plus two filtered
/// indexes, guarded so the startup migrate can run it against the drifted shared database (which may already have some
/// or all of it). No backfill (C-4): no UPDATE may ride along. The LocalDB test runs Down → Up → Up (re-run) → Down → Up
/// on a database built from the current model (the Material history does not replay from empty — memory A31 PF-03).
/// </summary>
public class ProductionOrderRouteMigrationTests
{
    private const string MigrationName = "A34_ProductionOrderRouteAndDelivery";

    private static readonly string[] Columns =
        ["FulfillmentRouteUuid", "DeliveryOrderUuid", "DeliveryNumber", "DeliveryCreationPendingSince"];

    private static MaterialDbContext SqlServerContext(string connection = "Server=none;Database=none;Trusted_Connection=True;") =>
        new(new DbContextOptionsBuilder<MaterialDbContext>().UseSqlServer(connection).Options, new StaticTenantContext());

    private static Migration A34Migration()
    {
        using var db = SqlServerContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(m => m.Key.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        return assembly.CreateMigration(entry.Value, "Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void The_migration_is_guarded_sql_only_and_adds_the_four_columns_and_two_filtered_indexes()
    {
        var up = A34Migration().UpOperations;

        up.Should().OnlyContain(o => o is SqlOperation, "every step is guarded SQL (the shared DB has drifted)");
        var sql = string.Join("\n", up.OfType<SqlOperation>().Select(o => o.Sql));

        foreach (var column in Columns)
            sql.Should().Contain($"COL_LENGTH(N'material.production_orders', N'{column}') IS NULL");
        sql.Should().Contain("[DeliveryNumber] nvarchar(50) NULL");
        sql.Should().Contain("IX_production_orders_FulfillmentRouteUuid").And.Contain("WHERE [FulfillmentRouteUuid] IS NOT NULL");
        sql.Should().Contain("IX_production_orders_DeliveryCreationPendingSince")
           .And.Contain("WHERE [DeliveryCreationPendingSince] IS NOT NULL");
        sql.Should().NotContainEquivalentOf("UPDATE ", "C-4: no backfill of existing production orders");
        sql.Should().NotContain("NOT NULL;", "every new column is nullable");
    }

    [Fact]
    public void Rolling_back_drops_exactly_what_it_added_and_is_guarded_too()
    {
        var down = A34Migration().DownOperations;
        down.Should().OnlyContain(o => o is SqlOperation);
        var sql = string.Join("\n", down.OfType<SqlOperation>().Select(o => o.Sql));

        foreach (var column in Columns)
            sql.Should().Contain($"DROP COLUMN [{column}]");
        sql.Should().Contain("DROP INDEX [IX_production_orders_FulfillmentRouteUuid]")
           .And.Contain("DROP INDEX [IX_production_orders_DeliveryCreationPendingSince]");
    }

    [Fact]
    public void There_are_no_model_changes_still_waiting_for_a_migration()
    {
        using var db = SqlServerContext();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        snapshot.Should().NotBeNull();

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable) snapshotModel = mutable.FinalizeModel();
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel, designTime: true, validationLogger: null);

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        differences.Should().BeEmpty("the entities and the migration snapshot have drifted — run 'dotnet ef migrations add'");
    }

    // ── LocalDB: up / down / up and a re-run on an already-migrated database ──────────────────────────────

    private static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
        ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    private static bool LocalDbAvailable()
    {
        try { using var c = new SqlConnection(MasterConnectionString); c.Open(); return true; }
        catch { return false; }
    }

    [Fact]
    public async Task Up_down_up_and_a_rerun_work_on_sql_server()
    {
        if (!LocalDbAvailable()) return; // same skip-when-unreachable arrangement as InventoryItemConcurrencyTests

        var database = $"SMS_MatA34_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };
        await ExecAsync(builder.ConnectionString, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        var connection = builder.ConnectionString;

        try
        {
            // Built from the current model (includes the four columns), then rolled back to the pre-A34 shape.
            await using (var db = SqlServerContext(connection))
                await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();

            var migration = A34Migration();
            await RunAsync(connection, migration.DownOperations);
            (await ExistingColumnsAsync(connection)).Should().BeEmpty("Down removes every column");
            (await ExistingIndexesAsync(connection)).Should().BeEmpty();

            await RunAsync(connection, migration.UpOperations);
            (await ExistingColumnsAsync(connection)).Should().BeEquivalentTo(Columns);
            (await ExistingIndexesAsync(connection)).Should().BeEquivalentTo(
                ["IX_production_orders_FulfillmentRouteUuid", "IX_production_orders_DeliveryCreationPendingSince"]);

            // Re-run on an already-migrated database (the drifted shared DB case): nothing fails, nothing doubles.
            await RunAsync(connection, migration.UpOperations);
            (await ExistingColumnsAsync(connection)).Should().BeEquivalentTo(Columns);

            await RunAsync(connection, migration.DownOperations);
            await RunAsync(connection, migration.DownOperations); // Down is guarded too
            (await ExistingColumnsAsync(connection)).Should().BeEmpty();

            await RunAsync(connection, migration.UpOperations);
            (await ExistingColumnsAsync(connection)).Should().BeEquivalentTo(Columns);

            // The model can read and write through the migrated shape.
            await using (var db = SqlServerContext(connection))
            {
                var count = await db.ProductionOrders.IgnoreQueryFilters()
                    .CountAsync(p => p.FulfillmentRouteUuid != null || p.DeliveryCreationPendingSince != null);
                count.Should().Be(0);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            builder.InitialCatalog = "master";
            await ExecAsync(builder.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    private static async Task RunAsync(string connection, IReadOnlyList<MigrationOperation> operations)
    {
        foreach (var op in operations.OfType<SqlOperation>())
            await ExecAsync(connection, op.Sql);
    }

    private static async Task ExecAsync(string connection, string sql)
    {
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ExistingColumnsAsync(string connection)
    {
        var found = new List<string>();
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        foreach (var column in Columns)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COL_LENGTH(N'material.production_orders', N'{column}')";
            if (await cmd.ExecuteScalarAsync() is not (null or DBNull)) found.Add(column);
        }
        return found;
    }

    private static async Task<List<string>> ExistingIndexesAsync(string connection)
    {
        var found = new List<string>();
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'material.production_orders') AND has_filter = 1
                            AND name IN (N'IX_production_orders_FulfillmentRouteUuid', N'IX_production_orders_DeliveryCreationPendingSince')";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));
        return found;
    }
}
