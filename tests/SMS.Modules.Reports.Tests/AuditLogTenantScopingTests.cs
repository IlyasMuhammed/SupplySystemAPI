using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

// Regression coverage for a real cross-tenant leak: AuditLog had no org scoping at all, so the
// Audit Trail / User Activity reports mixed every organization's history together, and the
// separately (and correctly) tenant-scoped IUserQueryService name lookup then silently failed to
// resolve a name for any row whose UserId belonged to a different org than the current viewer —
// surfacing as "Unknown user" for most/all rows once seen in practice.
public class AuditLogTenantScopingTests
{
    private static (ReportsRepository Repo, ReportsDbContext Db) Build(string dbName, Guid organizationId)
    {
        var tenantContext = new StaticTenantContext { OrganizationId = organizationId };
        var db = new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

        var userQuery = new Mock<IUserQueryService>();
        userQuery.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) =>
                (IReadOnlyList<UserIdentity>)ids.Select(id => new UserIdentity(id, $"Resolved {id}")).ToList());

        var repo = new ReportsRepository(
            db:        db,
            demand:    new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            warehouse: new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            inventory: new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            finance:   new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            suppliers: new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            material:  new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext),
            userQuery: userQuery.Object,
            timeline:  Mock.Of<ITimelineService>());

        return (repo, db);
    }

    [Fact]
    public async Task GetUserActivityAsync_OnlyShowsCallersOwnOrgsActivity()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (_, dbA) = Build(dbName, orgA);
        dbA.AuditLogs.Add(new AuditLog { UserId = 1, Module = "PR", Action = "CREATE", Timestamp = DateTime.UtcNow, IpAddress = "" });
        await dbA.SaveChangesAsync();

        var (_, dbB) = Build(dbName, orgB);
        dbB.AuditLogs.Add(new AuditLog { UserId = 2, Module = "PO", Action = "CREATE", Timestamp = DateTime.UtcNow, IpAddress = "" });
        await dbB.SaveChangesAsync();

        var (repoA, _) = Build(dbName, orgA);
        var activityForA = await repoA.GetUserActivityAsync(new ReportDateFilter());

        activityForA.Should().ContainSingle(a => a.UserId == 1);
        activityForA.Should().NotContain(a => a.UserId == 2);
    }

    [Fact]
    public async Task GetUserActivityAsync_ResolvesNameForOwnOrgsUser()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();

        var (_, dbA) = Build(dbName, orgA);
        dbA.AuditLogs.Add(new AuditLog { UserId = 42, Module = "PR", Action = "CREATE", Timestamp = DateTime.UtcNow, IpAddress = "" });
        await dbA.SaveChangesAsync();

        var (repoA, _) = Build(dbName, orgA);
        var activity = await repoA.GetUserActivityAsync(new ReportDateFilter());

        activity.Should().ContainSingle().Which.UserName.Should().Be("Resolved 42");
    }

    [Fact]
    public async Task GetAuditTrailAsync_OnlyShowsCallersOwnOrgsRows()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (_, dbA) = Build(dbName, orgA);
        dbA.AuditLogs.Add(new AuditLog { UserId = 1, Module = "PR", Action = "CREATE", Timestamp = DateTime.UtcNow, IpAddress = "" });
        await dbA.SaveChangesAsync();

        var (_, dbB) = Build(dbName, orgB);
        dbB.AuditLogs.Add(new AuditLog { UserId = 2, Module = "PO", Action = "CREATE", Timestamp = DateTime.UtcNow, IpAddress = "" });
        await dbB.SaveChangesAsync();

        var (repoA, _) = Build(dbName, orgA);
        var trail = await repoA.GetAuditTrailAsync(new AuditLogFilter());

        trail.Data.Should().ContainSingle(a => a.UserId == 1);
        trail.Data.Should().NotContain(a => a.UserId == 2);
    }
}
