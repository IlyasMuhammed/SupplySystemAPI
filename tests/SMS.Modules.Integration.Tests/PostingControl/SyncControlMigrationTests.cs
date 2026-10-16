using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Migrations;
using SMS.Modules.Integration.Tests.Connections;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>
/// A44 D-14 migration (LocalDB, never the shared database): the five AutoPush* switches are copied onto sync-control rows,
/// POS sales follow "Sync POS sales" and sales invoices, a re-run changes nothing, and a row an admin already changed stays.
/// </summary>
public class SyncControlMigrationTests
{
    private static async Task<Dictionary<string, (bool Enabled, bool Hold)>> RowsAsync(LocalDb sql, int connectionId)
    {
        await using var db = sql.OpenAs(Guid.Empty, superAdmin: true);
        return (await db.SyncControlConfigs.IgnoreQueryFilters().AsNoTracking().Where(c => c.ConnectionId == connectionId).ToListAsync())
            .ToDictionary(c => c.DocumentType, c => (c.IsEnabled, c.HoldWhenDisabled));
    }

    [Fact]
    public async Task Existing_switches_are_copied_once_and_an_admins_row_is_never_overwritten()
    {
        await using var sql = await LocalDb.CreateAsync();
        var org = Guid.NewGuid();

        await using (var db = sql.OpenAs(org))
        {
            db.Settings.Add(new IntegrationSettings { ConnectionId = 1, AutoPushBills = false, AutoPushPosSales = true });   // invoices on
            db.Settings.Add(new IntegrationSettings { ConnectionId = 2, AutoPushSalesInvoices = false, AutoPushPosSales = true, AutoPushVendors = false });
            db.SyncControlConfigs.Add(new SyncControlConfig { ConnectionId = 1, DocumentType = "Payment", IsEnabled = false, HoldWhenDisabled = true, DisplayLabel = "Customer payments" });
            await db.SaveChangesAsync();
        }

        await using (var db = sql.OpenAs(Guid.Empty, superAdmin: true))
        {
            // Twice, as an API restart would: every DDL is guarded and the copy only fills gaps.
            for (var run = 0; run < 2; run++)
            {
                await db.Database.ExecuteSqlRawAsync(A44_SyncControlAndDefaults.CreateTablesSql);
                await db.Database.ExecuteSqlRawAsync(A44_SyncControlAndDefaults.CopyAutoPushSql);
            }
        }

        var a = await RowsAsync(sql, 1);
        a.Should().HaveCount(11);
        a["Customer"].Should().Be((true, false));
        a["SalesInvoice"].Should().Be((true, false));
        a["PurchaseBill"].Should().Be((false, false));
        a["Payment"].Should().Be((false, true), "the admin's row is left as it is");
        a["CreditNote"].Should().Be((false, false), "types SCM never sent start off for existing companies, sales invoices on or not (D-61)");
        a["POSSale"].Should().Be((true, true));
        a["POSReturn"].Should().Be((true, true));
        a["BillPayment"].Should().Be((false, false));
        a["VendorCredit"].Should().Be((false, false));

        var b = await RowsAsync(sql, 2);
        b["Vendor"].Should().Be((false, false));
        b["SalesInvoice"].Should().Be((false, false));
        b["Payment"].Should().Be((false, false));
        b["POSSale"].Should().Be((false, false), "POS sales were skipped, not held, while sales invoices were off (REV-14)");

        await using var check = sql.OpenAs(Guid.Empty, superAdmin: true);
        (await check.SyncControlConfigs.IgnoreQueryFilters().CountAsync()).Should().Be(22);
        (await check.SyncControlConfigs.IgnoreQueryFilters().Select(c => c.OrganizationId).Distinct().ToListAsync()).Should().Equal(org);
    }

    [Fact]
    public async Task The_whole_migration_chain_applies_on_an_empty_database_and_matches_the_model()
    {
        var name = "SMS_QBI_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<Data.IntegrationDbContext>()
            .UseSqlServer($@"Server=(localdb)\MSSQLLocalDB;Database={name};Trusted_Connection=True;TrustServerCertificate=True").Options;
        await using var db = new Data.IntegrationDbContext(options, new SMS.Shared.Common.StaticTenantContext { IsSuperAdmin = true });
        try
        {
            await db.Database.MigrateAsync();
            db.Database.GetPendingMigrations().Should().BeEmpty();
            db.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");

            db.SyncControlConfigs.Add(new SyncControlConfig { ConnectionId = 1, DocumentType = "Payment", DisplayLabel = "x" });
            db.RemoteEntities.Add(new RemoteEntityRecord { ConnectionId = 1, RemoteId = "1", Name = "x" });
            db.ProviderDefaults.Add(new ProviderDefault { ConnectionId = 1, SettingKey = "ClassRef", SettingValue = "1" });
            db.DocumentOverrides.Add(new DocumentOverride { ConnectionId = 1, DocumentType = "Payment", SettingKey = "ClassRef", SettingValue = "1" });
            await db.SaveChangesAsync();
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task A_settings_table_from_before_Sync_POS_sales_copies_POS_as_off()
    {
        await using var sql = await LocalDb.CreateAsync();

        await using var db = sql.OpenAs(Guid.Empty, superAdmin: true);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [integration].[Settings] ([OrganizationId],[ConnectionId],[Mode],[AutoPushCustomers],[AutoPushVendors],[AutoPushItems],"
          + "[AutoPushSalesInvoices],[AutoPushBills],[AutoPushPosSales],[ItemTypeDefault],[PartnerScope]) "
          + "VALUES (NEWID(), 7, 'Live', 1, 1, 1, 1, 1, 1, 'NonInventory', 'OnlyWhenReferenced');");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE [integration].[Settings] DROP COLUMN [AutoPushPosSales];");

        await db.Database.ExecuteSqlRawAsync(A44_SyncControlAndDefaults.CopyAutoPushSql);

        var rows = await RowsAsync(sql, 7);
        rows.Should().HaveCount(11);
        rows["POSSale"].Should().Be((false, true));
        rows["SalesInvoice"].Should().Be((true, false));
    }
}
