using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>
/// A32 PA-03 — what other modules set up for a new organization (Demand's ten rejection reasons) is triggered here,
/// after the organization is committed; and their startup backfill reads every organization from here.
/// </summary>
public class OrganizationProvisioningHookTests
{
    private static (SqliteConnection Connection, DbContextOptions<TenancyDbContext> Options) NewDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TenancyDbContext>().UseSqlite(connection).Options;
        using (var db = new TenancyDbContext(options))
            db.Database.EnsureCreated();
        return (connection, options);
    }

    private static TenancyService NewService(TenancyDbContext db, params IOrganizationProvisionedHandler[] handlers)
    {
        var users = new Mock<IOrgUserProvisioningService>();
        users.Setup(x => x.CreateOrgAdminUserAsync(It.IsAny<CreateOrgAdminUserRequest>(), It.IsAny<System.Data.Common.DbTransaction>()))
            .ReturnsAsync(1);
        var workflows = new Mock<IWorkflowSeedingService>();
        workflows.Setup(x => x.SeedDefaultWorkflowsAsync(It.IsAny<Guid>(), It.IsAny<System.Data.Common.DbTransaction?>()))
            .Returns(Task.CompletedTask);
        var repo = new TenancyRepository(db, users.Object, workflows.Object);
        return new TenancyService(repo, users.Object, Mock.Of<ITenantSnapshotProvider>(), currencies: null, provisioned: handlers);
    }

    private static CreateOrganizationRequest Acme(string code) => new()
    {
        OrgCode = code, OrgName = $"{code} Corp", Plan = "BASIC",
        AdminFirstName = "Jane", AdminLastName = "Doe", AdminEmail = $"jane@{code.ToLowerInvariant()}.test"
    };

    private static async Task SeedCatalogAsync(DbContextOptions<TenancyDbContext> options)
    {
        await using var db = new TenancyDbContext(options);
        await new TenancyDataSeeder(db, Mock.Of<IUserQueryService>(u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null)))
            .SeedAsync();
    }

    [Fact]
    public async Task Every_provisioning_handler_is_called_once_with_the_new_organization()
    {
        var (connection, options) = NewDatabase();
        using var _ = connection;
        await SeedCatalogAsync(options);
        var first  = new Mock<IOrganizationProvisionedHandler>();
        var second = new Mock<IOrganizationProvisionedHandler>();
        await using var db = new TenancyDbContext(options);

        var result = await NewService(db, first.Object, second.Object).CreateOrganizationWithAdminAsync(Acme("HOOK1"), createdBy: 1);

        first.Verify(h => h.OnOrganizationProvisionedAsync(result.OrganizationId, It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(h => h.OnOrganizationProvisionedAsync(result.OrganizationId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failing_handler_does_not_undo_the_organization_or_stop_the_others()
    {
        var (connection, options) = NewDatabase();
        using var _ = connection;
        await SeedCatalogAsync(options);
        var failing = new Mock<IOrganizationProvisionedHandler>();
        failing.Setup(h => h.OnOrganizationProvisionedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var after = new Mock<IOrganizationProvisionedHandler>();
        await using var db = new TenancyDbContext(options);

        var result = await NewService(db, failing.Object, after.Object).CreateOrganizationWithAdminAsync(Acme("HOOK2"), createdBy: 1);

        result.OrganizationId.Should().NotBeEmpty();
        after.Verify(h => h.OnOrganizationProvisionedAsync(result.OrganizationId, It.IsAny<CancellationToken>()), Times.Once);
        await using var check = new TenancyDbContext(options);
        (await check.Organizations.AnyAsync(o => o.Id == result.OrganizationId)).Should().BeTrue();
    }

    [Fact]
    public async Task The_directory_lists_every_organization_active_or_not()
    {
        var (connection, options) = NewDatabase();
        using var _ = connection;
        await SeedCatalogAsync(options);
        Guid inactiveId;
        await using (var db = new TenancyDbContext(options))
        {
            var created = await NewService(db).CreateOrganizationWithAdminAsync(Acme("DIR1"), createdBy: 1);
            inactiveId = created.OrganizationId;
        }
        await using (var db = new TenancyDbContext(options))
        {
            (await db.Organizations.SingleAsync(o => o.Id == inactiveId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        await using var read = new TenancyDbContext(options);
        var ids = await new OrganizationDirectory(read).GetOrganizationIdsAsync();

        ids.Should().Contain(inactiveId);
        ids.Should().HaveCount(await read.Organizations.CountAsync());
    }
}
