using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Migrations;
using SMS.Modules.Integration.Tests.Connections;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A49 G-5 / G-15 (tasks 323, 328, 344) on LocalDB only: every existing connection gets its HRM document types off + held; a re-run changes nothing; a row an accountant enabled stays enabled.</summary>
public class A49CostCenterMigrationTests
{
    [Fact]
    public async Task Hrm_document_types_start_off_and_held_and_a_rerun_keeps_what_an_accountant_set()
    {
        await using var sql = await LocalDb.CreateAsync();
        var org = Guid.NewGuid();
        int connectionId;
        await using (var db = sql.OpenAs(org))
        {
            var connection = new IntegrationConnection { OrganizationId = org, RealmId = "9130000000000001", CompanyName = "Sandbox", HomeCurrencyCode = "PKR", Country = "PK" };
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
            db.SyncControlConfigs.Add(new SyncControlConfig { OrganizationId = org, ConnectionId = connectionId, DocumentType = "SalesInvoice", IsEnabled = true, DisplayLabel = "Sales invoices" });
            await db.SaveChangesAsync();
        }

        await using (var db = sql.OpenAs(Guid.Empty, superAdmin: true))
        {
            await db.Database.ExecuteSqlRawAsync(A49_CostCenterQboMappings.CreateTableSql);
            await db.Database.ExecuteSqlRawAsync(A49_CostCenterQboMappings.SeedSyncControlSql);

            var rows = await db.SyncControlConfigs.IgnoreQueryFilters().Where(c => c.ConnectionId == connectionId).ToListAsync();
            rows.Should().HaveCount(9);
            rows.Where(r => r.DocumentType != "SalesInvoice").Should().OnlyContain(r => !r.IsEnabled && r.HoldWhenDisabled && r.OrganizationId == org);
            rows.Single(r => r.DocumentType == "SalesInvoice").IsEnabled.Should().BeTrue("existing types are not touched");

            // An accountant enables one; a re-run (a restart with the history row lost) leaves it and adds nothing.
            rows.Single(r => r.DocumentType == "PayrollRun").IsEnabled = true;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(A49_CostCenterQboMappings.CreateTableSql);
            await db.Database.ExecuteSqlRawAsync(A49_CostCenterQboMappings.SeedSyncControlSql);

            (await db.SyncControlConfigs.IgnoreQueryFilters().CountAsync(c => c.ConnectionId == connectionId)).Should().Be(9);
            (await db.SyncControlConfigs.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.ConnectionId == connectionId && c.DocumentType == "PayrollRun")).IsEnabled.Should().BeTrue();
        }
    }

    [Fact]
    public async Task One_mapping_per_connection_and_cost_center()
    {
        await using var sql = await LocalDb.CreateAsync();
        var org = Guid.NewGuid();
        await using var db = sql.OpenAs(org);
        db.CostCenterQboMappings.Add(new CostCenterQboMapping { OrganizationId = org, ConnectionId = 1, CostCenterCode = "OPS", CostCenterName = "Operations" });
        await db.SaveChangesAsync();
        db.CostCenterQboMappings.Add(new CostCenterQboMapping { OrganizationId = org, ConnectionId = 1, CostCenterCode = "OPS", CostCenterName = "Again" });
        var dup = () => db.SaveChangesAsync();
        await dup.Should().ThrowAsync<DbUpdateException>();
    }
}
