using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>
/// A38 (INV) on SQL Server (LocalDB): the POS queries translate, the posting runs in its own transaction, and change
/// notifications wait for the commit (and are dropped on rollback). Organization explicit, as for a super admin.
/// </summary>
public class PosSqlServerTests
{
    private static InventoryDbContext Context(InventorySqlServerHarness harness, Guid org, IServiceProvider services, bool superAdmin = false)
    {
        // The harness builds a 2-argument context; POS needs the service provider for the listener.
        var plain = harness.NewContext(org, superAdmin: superAdmin);
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlServer(plain.Database.GetConnectionString()).Options;
        plain.Dispose();
        return new InventoryDbContext(options, new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin }, services);
    }

    [SqlServerFact]
    public async Task A_sale_posts_in_one_transaction_and_notifies_after_the_commit()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var seeded   = await harness.SeedStockAsync(onHand: 2m);
        var listener = new FakePosChangeListener();
        var services = new ServiceCollection().AddLogging().AddSingleton<IPosChangeListener>(listener).BuildServiceProvider();

        decimal? committedOnHand = null;
        listener.OnCall = async () =>
        {
            // A fresh connection: blocks on uncommitted rows, so a value here proves the commit happened first.
            await using var other = harness.NewContext(seeded.OrganizationId);
            committedOnHand = (await other.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId)).QtyOnHand;
        };

        await using (var db = Context(harness, seeded.OrganizationId, services, superAdmin: true))
        {
            var poster = new PosStockPoster(db, new InventoryLedgerService(db, NullLogger<InventoryLedgerService>.Instance));
            var sale = new PosStockPosting(seeded.OrganizationId, seeded.WarehouseUuid, Guid.NewGuid(), "POS-1", PosStockTransactionType.Sale,
                true, [new PosStockLine(seeded.VariantUuid, 3m)], 1);

            var result = await poster.PostAsync(sale);
            var again  = await poster.PostAsync(sale);

            result.NegativeVariantUuids.Should().Equal(seeded.VariantUuid);
            again.NegativeVariantUuids.Should().Equal(seeded.VariantUuid);
            (await poster.GetLevelsAsync(seeded.OrganizationId, seeded.WarehouseUuid, [seeded.VariantUuid]))
                .Should().Equal(new PosStockLevel(seeded.VariantUuid, -1m));
            (await db.InventoryLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.OrganizationId == seeded.OrganizationId)).Should().Be(1);
        }

        listener.StockCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(
            (seeded.OrganizationId, seeded.WarehouseUuid, (IReadOnlyCollection<Guid>)new[] { seeded.VariantUuid }));
        committedOnHand.Should().Be(-1m);
    }

    [SqlServerFact]
    public async Task Notifications_inside_a_rolled_back_transaction_are_dropped_and_sent_on_commit()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var seeded   = await harness.SeedStockAsync(onHand: 5m);
        var listener = new FakePosChangeListener();
        var services = new ServiceCollection().AddLogging().AddSingleton<IPosChangeListener>(listener).BuildServiceProvider();

        await using (var db = Context(harness, seeded.OrganizationId, services))
        {
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == seeded.VariantUuid);
                variant.SellingPrice = 4m;
                await db.SaveChangesAsync();
                listener.Total.Should().Be(0, "not before the commit");
                await tx.RollbackAsync();
            }
            listener.Total.Should().Be(0, "a rolled-back change never happened");

            db.ChangeTracker.Clear();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == seeded.VariantUuid);
                variant.SellingPrice = 6m;
                await db.SaveChangesAsync();
                listener.Total.Should().Be(0);
                await tx.CommitAsync();
            }
        }

        listener.PriceCalls.Should().Equal((seeded.OrganizationId, seeded.VariantUuid, (decimal?)null, (decimal?)6m));
    }

    [SqlServerFact]
    public async Task The_catalog_queries_translate()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var seeded   = await harness.SeedStockAsync(onHand: 1m);
        var services = new ServiceCollection().BuildServiceProvider();

        await using var db = Context(harness, seeded.OrganizationId, services, superAdmin: true);
        var catalog = new PosCatalogSource(db);

        var changed = await catalog.GetChangedAsync(seeded.OrganizationId, DateTime.UtcNow.AddHours(-1), 1);
        changed.Should().ContainSingle(i => i.VariantUuid == seeded.VariantUuid);
        (await catalog.GetChangedAsync(seeded.OrganizationId, changed[0].ModifiedAt, 10)).Should().BeEmpty();
        (await catalog.GetByVariantUuidsAsync(seeded.OrganizationId, [seeded.VariantUuid])).Should().ContainSingle();
        (await catalog.GetByProductUuidsAsync(seeded.OrganizationId, [changed[0].ProductUuid])).Should().ContainSingle();
        (await catalog.GetCategoriesAsync(seeded.OrganizationId)).Should().ContainSingle(c => c.Name == "Cable");
        (await catalog.GetChangedAsync(Guid.NewGuid(), null, 10)).Should().BeEmpty();
    }
}
