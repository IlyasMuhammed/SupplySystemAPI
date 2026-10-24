using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A45 M1/M2/M3 / D-14 on real SQL Server (LocalDB): A45_SupplierPricelists creates the table, its constraints and indexes and the
/// four PO line columns, goes down and up, re-runs cleanly when the shared database already has it, keeps existing PO lines as
/// Manual, and the constraints / filtered unique index / rowversion really hold.
/// </summary>
public sealed class A45SupplierPricelistMigrationTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A45_DEM_Migr_{Guid.NewGuid():N};";

    private DemandDbContext NewContext(Guid? org = null) => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static async Task<int> CountAsync(DemandDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static Task<int> Shape(DemandDbContext db) => CountAsync(db,
        "SELECT (SELECT COUNT(*) FROM sys.tables WHERE name = 'SupplierPricelists' AND schema_id = SCHEMA_ID('demand'))" +
        " + (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'demand' AND TABLE_NAME = 'purchase_order_lines' " +
        "      AND COLUMN_NAME IN ('PriceSource','SystemResolvedPrice','SupplierPricelistUuid','PriceOverrideReason'))" +
        " + (SELECT COUNT(*) FROM sys.check_constraints WHERE name IN ('CK_POLines_PriceSource','CK_SupplierPricelists_Price'," +
        "      'CK_SupplierPricelists_MinQty','CK_SupplierPricelists_Dates','CK_SupplierPricelists_DateBasis'))" +
        " + (SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_SupplierPricelists_Lookup','IX_SupplierPricelists_Supplier'," +
        "      'IX_SupplierPricelists_Variant','UQ_SupplierPricelists_NoDuplicate','UX_SupplierPricelists_UUID'))");

    private const int FullShape = 1 + 4 + 5 + 5;

    [Fact]
    public async Task Goes_up_down_up_and_reruns_on_a_database_that_already_has_it()
    {
        await using var db = NewContext();
        var a45 = db.Database.GetMigrations().Single(m => m.EndsWith("_A45_SupplierPricelists", StringComparison.Ordinal));
        var before = db.Database.GetMigrations().TakeWhile(m => m != a45).Last();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(a45);
        (await Shape(db)).Should().Be(FullShape);

        await migrator.MigrateAsync(before);
        (await Shape(db)).Should().Be(0);

        await migrator.MigrateAsync(a45);
        // Drift: the history forgets A45 but everything it made is there. The guarded Up runs again without error.
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetDeleteScript(a45));
        await migrator.MigrateAsync(a45);
        (await Shape(db)).Should().Be(FullShape);
    }

    [Fact]
    public async Task Existing_PO_lines_become_Manual_and_the_model_matches_the_database()
    {
        await using var db = NewContext();
        var a45 = db.Database.GetMigrations().Single(m => m.EndsWith("_A45_SupplierPricelists", StringComparison.Ordinal));
        var before = db.Database.GetMigrations().TakeWhile(m => m != a45).Last();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(before);

        var org = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO demand.purchase_orders (UUID, OrganizationId, PoNumber, SupplierId, SupplierName, Status, TotalAmount, IsActive, IsDelete, CreatedBy, CreatedDate, Source) " +
            "VALUES (NEWID(), {0}, 'PO-OLD-1', NEWID(), 'Old', 'DRAFT', 10, 1, 0, 1, SYSUTCDATETIME(), 'MANUAL')", org);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO demand.purchase_order_lines (UUID, OrganizationId, PurchaseOrderId, [LineNo], ItemDescription, Quantity, UnitPrice, LineTotal, QtyReceived, QtyInvoiced, RequiresInspection) " +
            "SELECT NEWID(), {0}, Id, 1, 'Old line', 1, 10, 10, 0, 0, 1 FROM demand.purchase_orders WHERE PoNumber = 'PO-OLD-1'", org);

        await migrator.MigrateAsync(a45);

        // At A45 the later addenda's columns do not exist yet, so the line is read with a projection of A45's own columns.
        await using var check = NewContext(org);
        var line = await check.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.ItemDescription == "Old line")
            .Select(l => new { l.PriceSource, l.SystemResolvedPrice, l.SupplierPricelistUuid }).SingleAsync();
        line.PriceSource.Should().Be(PriceSource.Manual, "BR-18");
        line.SystemResolvedPrice.Should().BeNull();
        line.SupplierPricelistUuid.Should().BeNull();

        await migrator.MigrateAsync(); // ...and on to the latest, whose model is what the code maps
        check.GetService<IMigrationsAssembly>().ModelSnapshot!.Should().NotBeNull();
        check.Database.HasPendingModelChanges().Should().BeFalse("the hand-written SQL must produce the model the code maps");
    }

    [Fact]
    public async Task Constraints_the_filtered_unique_index_and_the_row_version_hold()
    {
        var org = Guid.NewGuid();
        await using var db = NewContext(org);
        await db.Database.MigrateAsync();

        SupplierPricelist Row(decimal price = 5m, DateTime? to = null, bool active = true, Guid? variant = null, decimal minQty = 1m) => new()
        {
            OrganizationId = org, ProductUuid = ProductId, VariantUuid = variant, SupplierUuid = SupplierId, MinQuantity = minQty, UnitPrice = price,
            CurrencyId = Guid.NewGuid(), ValidFrom = new DateTime(2026, 1, 1), ValidTo = to ?? new DateTime(2026, 12, 31), IsActive = active, CreatedBy = 1
        };

        // CK: price > 0, valid to >= valid from.
        db.SupplierPricelists.Add(Row(price: 0m));
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();
        db.SupplierPricelists.Add(Row(to: new DateTime(2025, 1, 1)));
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();

        // Filtered unique index: an exact duplicate active row is refused (a NULL variant counts), an inactive one is not.
        db.SupplierPricelists.Add(Row());
        await db.SaveChangesAsync();
        db.SupplierPricelists.Add(Row());
        (await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<SqlException>().WithMessage("*UQ_SupplierPricelists_NoDuplicate*");
        db.ChangeTracker.Clear();
        db.SupplierPricelists.Add(Row(active: false));
        db.SupplierPricelists.Add(Row(variant: Guid.NewGuid()));
        db.SupplierPricelists.Add(Row(minQty: 100m));
        await db.SaveChangesAsync();

        // The rowversion is generated and changes with every update, so a stale writer loses.
        db.ChangeTracker.Clear();
        var first = await db.SupplierPricelists.IgnoreQueryFilters().FirstAsync(r => r.MinQuantity == 1m && r.VariantUuid == null && r.IsActive);
        first.RowVersion.Should().HaveCount(8);
        var original = first.RowVersion.ToArray();
        await using var other = NewContext(org);
        var same = await other.SupplierPricelists.IgnoreQueryFilters().FirstAsync(r => r.Id == first.Id);
        same.UnitPrice = 6m;
        await other.SaveChangesAsync();
        same.RowVersion.Should().NotEqual(original);

        first.UnitPrice = 7m;
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private static readonly Guid ProductId = Guid.NewGuid();
    private static readonly Guid SupplierId = Guid.NewGuid();
}

