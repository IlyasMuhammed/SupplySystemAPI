using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Migrations;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// The lost-update guard on <see cref="InventoryItem"/>.
/// <para>
/// These tests cannot run in memory: the in-memory provider ignores concurrency tokens, so a race
/// there would "pass" against a counter with no protection at all. They run against SQL Server
/// (LocalDB by default) and skip themselves where none is reachable — same arrangement as the
/// document-number race test in Logistics.
/// </para>
/// </summary>
public class InventoryItemConcurrencyTests
{
    private const int User = 7;

    // ── The token itself ──────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task Two_writers_that_read_the_same_row_cannot_both_save()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 10);

        await using var first  = harness.NewContext(stock.OrganizationId);
        await using var second = harness.NewContext(stock.OrganizationId);

        var rowInFirst  = await first.InventoryItems.SingleAsync(i => i.Id == stock.ItemId);
        var rowInSecond = await second.InventoryItems.SingleAsync(i => i.Id == stock.ItemId);

        rowInFirst.QtyOnHand  -= 3;
        rowInSecond.QtyOnHand -= 4;

        await first.SaveChangesAsync();

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the second writer's read is stale; saving it would silently discard the first");

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyOnHand
            .Should().Be(7, "only the first write may land");
    }

    // ── The reservation retry ─────────────────────────────────────────────────

    [SqlServerFact]
    public async Task A_reservation_that_lost_the_race_re_plans_and_refuses_what_is_no_longer_free()
    {
        // Two sale orders confirm at the same moment for 6 of the 10 on hand. Before the token,
        // both were promised the stock: 12 reserved of 10. Now the loser reruns against the
        // counter the winner left behind and is refused.
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 10);

        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            var winner = await new StockReservationService(other)
                .ReserveAsync("SALES_ORDER", Guid.NewGuid(), [Request(stock, 6)], User);
            winner.Succeeded.Should().BeTrue();
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);

        var loser = await new StockReservationService(db)
            .ReserveAsync("SALES_ORDER", Guid.NewGuid(), [Request(stock, 6)], User);

        loser.Succeeded.Should().BeFalse();
        loser.Lines.Single().Reason.Should().Be("Only 4 available.");
        competitor.Fired.Should().Be(1, "the competing write happens exactly once, on the first attempt");

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyReserved
            .Should().Be(6, "only the winner's hold is on the counter");
        (await check.StockReservations.CountAsync(r => r.Status == StockReservation.StatusActive))
            .Should().Be(1);
    }

    [SqlServerFact]
    public async Task A_reservation_that_lost_the_race_succeeds_on_retry_when_stock_remains()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 10);

        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            await new StockReservationService(other)
                .ReserveAsync("MIR", Guid.NewGuid(), [Request(stock, 2)], User);
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);

        var result = await new StockReservationService(db)
            .ReserveAsync("SALES_ORDER", Guid.NewGuid(), [Request(stock, 6)], User);

        result.Succeeded.Should().BeTrue("8 were still free after the competing hold of 2");
        competitor.Fired.Should().Be(1);

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyReserved
            .Should().Be(8, "both holds are on the counter, neither overwrote the other");
        (await check.StockReservations.CountAsync(r => r.Status == StockReservation.StatusActive))
            .Should().Be(2);
    }

    [SqlServerFact]
    public async Task A_release_that_lost_the_race_still_takes_exactly_its_own_hold_off_the_counter()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 10);

        var mine   = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await using (var setup = harness.NewContext(stock.OrganizationId))
        {
            var reservations = new StockReservationService(setup);
            (await reservations.ReserveAsync("SALES_ORDER", mine,   [Request(stock, 3)], User)).Succeeded.Should().BeTrue();
            (await reservations.ReserveAsync("SALES_ORDER", theirs, [Request(stock, 4)], User)).Succeeded.Should().BeTrue();
        }

        // The other document is released between this one's read and its write.
        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            await new StockReservationService(other).ReleaseBySourceAsync("SALES_ORDER", theirs, "Cancelled.", User);
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);

        var released = await new StockReservationService(db).ReleaseBySourceAsync("SALES_ORDER", mine, "Cancelled.", User);

        released.Should().Be(1);
        competitor.Fired.Should().Be(1);

        await using var check = harness.NewContext(stock.OrganizationId);
        (await check.InventoryItems.SingleAsync(i => i.Id == stock.ItemId)).QtyReserved
            .Should().Be(0, "3 + 4 were held and both were released; a lost update would have left 3 or 4 behind");
    }

    [SqlServerFact]
    public async Task Inside_a_callers_transaction_the_conflict_is_not_retried_but_surfaces()
    {
        // A document that posts its own stock movement and consumes its hold in one transaction
        // owns that transaction. The service must not retry inside it — the caller's other
        // changes are part of the same unit of work — so the conflict reaches the caller and,
        // through it, the API's 409.
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 10);

        var competitor = new ConflictInjector(async () =>
        {
            await using var other = harness.NewContext(stock.OrganizationId);
            await new StockReservationService(other)
                .ReserveAsync("MIR", Guid.NewGuid(), [Request(stock, 1)], User);
        });

        await using var db = harness.NewContext(stock.OrganizationId, competitor);
        await using var callerOwned = await db.Database.BeginTransactionAsync();

        var act = async () => await new StockReservationService(db)
            .ReserveAsync("SALES_ORDER", Guid.NewGuid(), [Request(stock, 6)], User);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        competitor.Fired.Should().Be(1, "no second attempt was made");
    }

    // ── The migration ─────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task The_migration_adds_the_column_once_and_is_safe_to_replay()
    {
        // The shared dev database replays every migration on each API start, so Up must be a
        // no-op the second time. The harness builds the schema from the model, which already has
        // the column — drop it first to stand in for a database that predates this migration.
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        await harness.ExecuteAsync("ALTER TABLE [inventory].[InventoryItems] DROP COLUMN [RowVersion]");
        (await harness.ColumnExistsAsync("inventory.InventoryItems", "RowVersion")).Should().BeFalse();

        await harness.ApplyUpAsync(new AddInventoryItemRowVersion());
        (await harness.ColumnExistsAsync("inventory.InventoryItems", "RowVersion")).Should().BeTrue();

        var replay = async () => await harness.ApplyUpAsync(new AddInventoryItemRowVersion());
        await replay.Should().NotThrowAsync("the column already exists and the guard must skip it");

        // And the model is usable through it: an existing row gets a version and the guard works.
        var stock = await harness.SeedStockAsync(onHand: 5);
        await using var db = harness.NewContext(stock.OrganizationId);
        var row = await db.InventoryItems.SingleAsync(i => i.Id == stock.ItemId);
        row.RowVersion.Should().NotBeEmpty();
    }

    private static ReservationRequest Request(SeededStock stock, decimal quantity) =>
        new(stock.VariantUuid, stock.WarehouseUuid, quantity);
}

/// <summary>
/// Runs a competing write once, from inside the first <c>SaveChanges</c> of the context it is
/// attached to — after that context has read its rows and planned against them, before it writes.
/// That is the exact window a lost update needs.
/// </summary>
internal sealed class ConflictInjector : SaveChangesInterceptor
{
    private readonly Func<Task> _competingWrite;

    public ConflictInjector(Func<Task> competingWrite) => _competingWrite = competingWrite;

    public int Fired { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Fired == 0)
        {
            Fired++;
            await _competingWrite();
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

internal sealed record SeededStock(Guid OrganizationId, int ItemId, Guid VariantUuid, Guid WarehouseUuid);

/// <summary>
/// A throwaway SQL Server database with the Inventory schema migrated, dropped after the test.
/// Uses LocalDB by default; override with <c>SMS_TEST_SQLSERVER</c>.
/// </summary>
internal sealed class InventorySqlServerHarness : IAsyncDisposable
{
    private readonly string _database;
    private readonly string _connectionString;

    private InventorySqlServerHarness(string database, string connectionString)
    {
        _database         = database;
        _connectionString = connectionString;
    }

    internal static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
        ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    internal static bool IsAvailable
    {
        get
        {
            try
            {
                using var conn = new SqlConnection(MasterConnectionString);
                conn.Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static async Task<InventorySqlServerHarness> CreateAsync()
    {
        var database = $"SMS_InvTest_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };

        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{database}]";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;
        var harness = new InventorySqlServerHarness(database, builder.ConnectionString);

        // Built from the current model, not by replaying migrations: the Inventory history is
        // not replayable on an empty database (20260706090000_AddDirectConsumptionFlag has no
        // Designer file, so EF never discovers it and Products.IsDirectConsumption never gets
        // created). Same workaround, for the same reason, as ProcurementCycleWebApplicationFactory.
        await using var db = harness.NewContext(Guid.NewGuid());
        await db.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTablesAsync();

        return harness;
    }

    /// <summary>Runs the raw SQL of a migration's <c>Up</c> against this database.</summary>
    internal async Task ApplyUpAsync(Migration migration)
    {
        await using var db = NewContext(Guid.NewGuid());
        foreach (var sql in migration.UpOperations.OfType<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(sql.Sql);
    }

    internal async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var db = NewContext(Guid.NewGuid());
        return await db.Database
            .SqlQueryRaw<int?>($"SELECT CAST(COL_LENGTH('{table}', '{column}') AS int) AS [Value]")
            .SingleAsync() is not null;
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var db = NewContext(Guid.NewGuid());
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    internal InventoryDbContext NewContext(Guid organizationId, IInterceptor? interceptor = null, bool superAdmin = false)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(_connectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);

        return new InventoryDbContext(options.Options,
            new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = superAdmin });
    }

    internal async Task<SeededStock> SeedStockAsync(decimal onHand)
    {
        var organizationId = Guid.NewGuid();
        await using var db = NewContext(organizationId);

        var category = new ProductCategory { Name = "Cable", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = $"4mm cable {Guid.NewGuid():N}"[..20], Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        db.ProductVariants.Add(variant);

        // Qualified: the bare name collides with the SMS.Modules.Warehouse namespace.
        var warehouse = new Domain.Warehouse
        {
            Uuid = Guid.NewGuid(), Name = "Main", Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();

        var item = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id,
            QtyOnHand = onHand, UnitCost = 5m
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        return new SeededStock(organizationId, item.Id, variant.Uuid, warehouse.Uuid);
    }

    public async ValueTask DisposeAsync()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A fact that skips itself when no SQL Server is reachable, so a machine without one still gets
/// a green suite while the test stays real everywhere a database exists.
/// </summary>
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!InventorySqlServerHarness.IsAvailable)
            Skip = "No SQL Server reachable. Set SMS_TEST_SQLSERVER to run the concurrency tests.";
    }
}
