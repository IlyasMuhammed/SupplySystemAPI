using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Material.Tests.ProductionDeliveryHandoffTests;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A34 PE-03 (analysis §4.7 step 4, the A30 P4-21 bug-3 lesson) on a real SQL Server (LocalDB, throwaway database):
/// when the handoff calls Logistics it holds <b>no</b> transaction and no lock on the production order — even straight
/// after the FGR-style cross-context commit on the same scoped context — and it refuses to call Logistics at all from
/// inside a caller's open transaction (it would deadlock against the sale order lock the creator takes).
/// </summary>
public sealed class ProductionDeliveryHandoffSqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A34_Handoff_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private MaterialDbContext NewContext() => new(
        new DbContextOptionsBuilder<MaterialDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<Guid> SeedAsync(MaterialDbContext db, string status)
    {
        var bom = new BillOfMaterial
        {
            OrganizationId = _org, BomNumber = "BOM-2026-00001", ProductUuid = Guid.NewGuid(), Version = 1,
            Status = "ACTIVE", BaseUom = "PCS", CreatedBy = 1
        };
        db.BillsOfMaterials.Add(bom);
        await db.SaveChangesAsync();

        var po = new ProductionOrder
        {
            OrganizationId = _org, ProductionNumber = "PROD-2026-00340", ProductUuid = bom.ProductUuid,
            ProductVariantUuid = Guid.NewGuid(), BomId = bom.Id, BomVersion = 1, PlannedQuantity = 10m,
            AcceptedQuantity = 0m, WarehouseUuid = Guid.NewGuid(), SourceType = ProductionSourceType.SalesOrder,
            SourceUuid = Guid.NewGuid(), SourceLineUuid = Guid.NewGuid(), SourceReference = "SO-2026-03400",
            FulfillmentRouteUuid = Guid.NewGuid(), RequiredDate = DateTime.UtcNow.Date.AddDays(5), Status = status,
            CreatedBy = 5
        };
        db.ProductionOrders.Add(po);
        await db.SaveChangesAsync();
        return po.UUID;
    }

    /// <summary>A second session asks for an update lock on the PO row; it times out if the handoff's session holds one.</summary>
    private async Task<bool> RowIsFreeAsync(Guid poUuid)
    {
        await using var probe = new SqlConnection(_connection);
        await probe.OpenAsync();
        await using var tx = (SqlTransaction)await probe.BeginTransactionAsync();
        await using var cmd = new SqlCommand(
            "SET LOCK_TIMEOUT 2000; SELECT COUNT(*) FROM material.production_orders WITH (UPDLOCK, ROWLOCK) WHERE UUID = @u;",
            probe, tx);
        cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.UniqueIdentifier) { Value = poUuid });
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            return false;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Right_after_an_fgr_style_commit_the_creator_is_called_with_no_transaction_and_no_lock_held()
    {
        await using var db = NewContext();
        var uuid = await SeedAsync(db, ProductionOrderStatus.QualityInspection);

        // What FinishedGoodsReceiptService.ConfirmCoreAsync does: a shared-connection transaction, UseTransaction, commit,
        // UseTransaction(null). The PO turns COMPLETED and gets its pending flag in that commit.
        await db.Database.OpenConnectionAsync();
        var conn = db.Database.GetDbConnection();
        await using (var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            db.Database.UseTransaction(sqlTx);
            var po = await db.ProductionOrders.SingleAsync(p => p.UUID == uuid);
            po.AcceptedQuantity = 10m;
            po.Status = ProductionOrderStatus.Completed;
            po.DeliveryCreationPendingSince = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await sqlTx.CommitAsync();
            db.Database.UseTransaction(null);
        }
        await db.Database.CloseConnectionAsync();

        var creator = new FakeCreator();
        bool? noTransaction = null, rowFree = null;
        creator.During = async () =>
        {
            noTransaction = db.Database.CurrentTransaction is null;
            rowFree       = await RowIsFreeAsync(uuid);
        };
        var handoff = new ProductionDeliveryHandoff(db, new StaticTenantContext { OrganizationId = _org }, creator);

        var result = await handoff.RunAsync(uuid, 7);

        result.Ran.Should().BeTrue();
        noTransaction.Should().BeTrue("the handoff must not call another module from inside a transaction");
        rowFree.Should().BeTrue("no lock on the production order may be held while Logistics takes the sale order lock");

        await using var check = NewContext();
        var stored = await check.ProductionOrders.AsNoTracking().SingleAsync(p => p.UUID == uuid);
        stored.DeliveryNumber.Should().Be("DLV-2026-00001");
        stored.DeliveryCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task Called_from_inside_an_open_transaction_it_refuses_and_keeps_the_po_pending()
    {
        await using var db = NewContext();
        var uuid = await SeedAsync(db, ProductionOrderStatus.Completed);
        var po = await db.ProductionOrders.SingleAsync(p => p.UUID == uuid);
        po.AcceptedQuantity = 10m;
        po.DeliveryCreationPendingSince = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var creator = new FakeCreator();
        var handoff = new ProductionDeliveryHandoff(db, new StaticTenantContext { OrganizationId = _org }, creator);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var result = await handoff.RunAsync(uuid, 7);
            result.Failed.Should().BeTrue();
            result.SkippedReason.Should().Contain("transaction");
            await tx.RollbackAsync();
        }

        creator.Calls.Should().BeEmpty();
        await using var check = NewContext();
        (await check.ProductionOrders.AsNoTracking().SingleAsync(p => p.UUID == uuid))
            .DeliveryCreationPendingSince.Should().NotBeNull("the sweep picks it up");
    }
}
