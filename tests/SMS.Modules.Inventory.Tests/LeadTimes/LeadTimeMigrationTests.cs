using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Migrations;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34-PB-01 on a real SQL Server (LocalDB): the <c>A34_LeadTimes</c> migration's own SQL, up, down and up again, and
/// a replay on an already-migrated database (every API start replays migrations on the drifted shared database). The
/// Inventory chain does not replay from empty (see <see cref="InventorySqlServerHarness"/>), so the schema comes from
/// the current model and the A34 part is taken away first.
/// </summary>
public class LeadTimeMigrationTests
{
    private static readonly string[] VariantColumns =
    [
        "ManufacturingLeadTimeDays", "ManufacturingBufferDays", "QualityInspectionDays", "InternalTransferDays",
        "PickPackDays", "ShippingLeadTimeDays", "SalesBufferDays"
    ];

    [SqlServerFact]
    public async Task PB_01_the_migration_is_additive_guarded_and_runs_up_down_up_and_replays()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 1);

        await harness.ExecuteAsync("DROP TABLE [inventory].[LeadTimeDefaults]");
        foreach (var column in VariantColumns)
            await harness.ExecuteAsync($"ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [{column}]");
        (await TableExistsAsync(harness)).Should().BeFalse();

        await harness.ApplyUpAsync(new A34_LeadTimes());
        await AssertMigratedAsync(harness);

        var replay = async () => await harness.ApplyUpAsync(new A34_LeadTimes());
        await replay.Should().NotThrowAsync("every statement is guarded: API start replays it on the drifted shared database");
        await AssertMigratedAsync(harness);

        // §5.4 column defaults: a row written with nothing but its keys reads 1/3/1/0/0/0.
        var org = Guid.NewGuid();
        await harness.ExecuteAsync(
            "INSERT INTO [inventory].[LeadTimeDefaults] ([Uuid], [OrganizationId], [CreatedBy], [CreatedDate]) "
          + $"VALUES (NEWID(), '{org}', 1, SYSUTCDATETIME())");
        await using (var db = harness.NewContext(org))
        {
            var row = await db.LeadTimeDefaults.SingleAsync(d => d.OrganizationId == org);
            new[] { row.PickPackDays, row.ShippingLeadTimeDays, row.SalesBufferDays, row.ManufacturingBufferDays,
                    row.QualityInspectionDays, row.InternalTransferDays }.Should().Equal(1, 3, 1, 0, 0, 0);
            row.RowVersion.Should().NotBeEmpty();
        }

        // One row per organization.
        var second = async () => await harness.ExecuteAsync(
            "INSERT INTO [inventory].[LeadTimeDefaults] ([Uuid], [OrganizationId], [CreatedBy], [CreatedDate]) "
          + $"VALUES (NEWID(), '{org}', 1, SYSUTCDATETIME())");
        (await second.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(2601, 2627);

        await ApplyDownAsync(harness);
        (await TableExistsAsync(harness)).Should().BeFalse();
        foreach (var column in VariantColumns)
            (await harness.ColumnExistsAsync("inventory.ProductVariants", column)).Should().BeFalse(column);
        var replayDown = async () => await ApplyDownAsync(harness);
        await replayDown.Should().NotThrowAsync();

        await harness.ApplyUpAsync(new A34_LeadTimes());
        await AssertMigratedAsync(harness);

        // The pre-existing variant survives with every override NULL, and the model reads and writes the new columns.
        await using (var db = harness.NewContext(stock.OrganizationId))
        {
            var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == stock.VariantUuid);
            new[] { variant.ManufacturingLeadTimeDays, variant.ManufacturingBufferDays, variant.QualityInspectionDays,
                    variant.InternalTransferDays, variant.PickPackDays, variant.ShippingLeadTimeDays, variant.SalesBufferDays }
                .Should().OnlyContain(v => v == null);
            variant.PickPackDays = 2;
            variant.ManufacturingLeadTimeDays = 0;
            // EF must send an explicit 0: a store default would turn "pick/pack 0" into 1 on the first save.
            db.LeadTimeDefaults.Add(new LeadTimeDefaults { PickPackDays = 0, ShippingLeadTimeDays = 5, CreatedBy = 7 });
            await db.SaveChangesAsync();
        }
        await using (var db = harness.NewContext(stock.OrganizationId))
        {
            var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == stock.VariantUuid);
            variant.PickPackDays.Should().Be(2);
            variant.ManufacturingLeadTimeDays.Should().Be(0);
            var row = await db.LeadTimeDefaults.SingleAsync();
            row.OrganizationId.Should().Be(stock.OrganizationId);
            row.PickPackDays.Should().Be(0);
            row.ShippingLeadTimeDays.Should().Be(5);
            row.SalesBufferDays.Should().Be(1);
        }
    }

    private static async Task AssertMigratedAsync(InventorySqlServerHarness harness)
    {
        foreach (var column in VariantColumns)
            (await harness.ColumnExistsAsync("inventory.ProductVariants", column)).Should().BeTrue(column);
        (await TableExistsAsync(harness)).Should().BeTrue();
        (await UniqueIndexExistsAsync(harness, "IX_LeadTimeDefaults_OrganizationId")).Should().BeTrue();
        (await UniqueIndexExistsAsync(harness, "IX_LeadTimeDefaults_Uuid")).Should().BeTrue();
    }

    private static async Task ApplyDownAsync(InventorySqlServerHarness harness)
    {
        foreach (var sql in new A34_LeadTimes().DownOperations.OfType<SqlOperation>())
            await harness.ExecuteAsync(sql.Sql);
    }

    private static async Task<bool> TableExistsAsync(InventorySqlServerHarness harness)
    {
        await using var db = harness.NewContext(Guid.NewGuid());
        return await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID('inventory.LeadTimeDefaults')")
            .SingleAsync() == 1;
    }

    private static async Task<bool> UniqueIndexExistsAsync(InventorySqlServerHarness harness, string name)
    {
        await using var db = harness.NewContext(Guid.NewGuid());
        return await db.Database.SqlQueryRaw<int>(
                $"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = '{name}' AND object_id = OBJECT_ID('inventory.LeadTimeDefaults') AND is_unique = 1")
            .SingleAsync() == 1;
    }
}
