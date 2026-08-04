using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

file static class Build
{
    private static Mock<IOrgUserProvisioningService> NewOrgUserProvisioningMock()
    {
        var mock = new Mock<IOrgUserProvisioningService>();
        mock.Setup(x => x.CreateOrgAdminUserAsync(It.IsAny<CreateOrgAdminUserRequest>(), It.IsAny<System.Data.Common.DbTransaction>()))
            .ReturnsAsync(1);
        mock.Setup(x => x.DeleteActiveSessionsForOrganizationAsync(It.IsAny<Guid>()))
            .ReturnsAsync(0);
        return mock;
    }

    private static Mock<IWorkflowSeedingService> NewWorkflowSeedingMock()
    {
        var mock = new Mock<IWorkflowSeedingService>();
        mock.Setup(x => x.SeedDefaultWorkflowsAsync(It.IsAny<Guid>(), It.IsAny<System.Data.Common.DbTransaction?>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    // No System Admin user exists in these isolated test databases, so SeedSuperAdminAsync
    // (MT-007) safely no-ops against this default.
    private static IUserQueryService NewUserQueryStub() => Mock.Of<IUserQueryService>(
        u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null));

    // In-memory provider — fast, used for everything that doesn't go through
    // CreateOrganizationWithAdminAsync's raw connection/transaction plumbing. The EF InMemory
    // provider isn't relational and throws on Database.OpenConnectionAsync()/BeginTransactionAsync().
    internal static (TenancyDbContext Db, TenancyDataSeeder Seeder, ITenancyService Service, Mock<IOrgUserProvisioningService> OrgUserProvisioningMock, Mock<IWorkflowSeedingService> WorkflowSeedingMock) New()
    {
        var name = Guid.NewGuid().ToString();
        var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(name).Options);
        var seeder = new TenancyDataSeeder(db, NewUserQueryStub());
        var mock = NewOrgUserProvisioningMock();
        var workflowSeedingMock = NewWorkflowSeedingMock();
        var repo = new TenancyRepository(db, mock.Object, workflowSeedingMock.Object);
        var service = new TenancyService(repo, mock.Object, Mock.Of<ITenantSnapshotProvider>());
        return (db, seeder, service, mock, workflowSeedingMock);
    }

    // SQLite in-memory — a real relational provider. Needed for tests that exercise
    // CreateOrganizationWithAdminAsync, which mirrors MivService's shared-DbTransaction pattern
    // (Database.OpenConnectionAsync / BeginTransactionAsync / UseTransaction) and requires an
    // actual relational connection to run.
    //
    // Returns a scope factory rather than one long-lived context: after a manually-managed
    // transaction commits, EF's internal current-transaction reference on that DbContext instance
    // is left stale (same as the pre-existing MivService precedent) — harmless in production since
    // each HTTP request gets its own freshly-scoped DbContext, but a problem if a single test
    // reuses one context across multiple calls. Call NewScope() again to get a fresh context
    // bound to the same connection, mirroring a new request. Caller must dispose the connection.
    internal static (SqliteConnection Connection, Func<(TenancyDbContext Db, TenancyDataSeeder Seeder, ITenancyService Service, Mock<IOrgUserProvisioningService> OrgUserProvisioningMock, Mock<IWorkflowSeedingService> WorkflowSeedingMock)> NewScope) NewSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TenancyDbContext>().UseSqlite(connection).Options;
        using (var initDb = new TenancyDbContext(options))
            initDb.Database.EnsureCreated();

        (TenancyDbContext, TenancyDataSeeder, ITenancyService, Mock<IOrgUserProvisioningService>, Mock<IWorkflowSeedingService>) NewScope()
        {
            var db = new TenancyDbContext(options);
            var seeder = new TenancyDataSeeder(db, NewUserQueryStub());
            var mock = NewOrgUserProvisioningMock();
            var workflowSeedingMock = NewWorkflowSeedingMock();
            var repo = new TenancyRepository(db, mock.Object, workflowSeedingMock.Object);
            var service = new TenancyService(repo, mock.Object, Mock.Of<ITenantSnapshotProvider>());
            return (db, seeder, service, mock, workflowSeedingMock);
        }

        return (connection, NewScope);
    }
}

public class SeederTests
{
    [Fact]
    public async Task SeedAsync_CreatesScmDemoOrganization_ActiveTrue()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");
        org.IsActive.Should().BeTrue();
        org.Plan.Should().Be("ENTERPRISE");
    }

    [Fact]
    public async Task SeedAsync_SeedsAllFeatureDefinitions_WithCorrectCodes()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        var seededCodes  = db.FeatureDefinitions.Select(f => f.FeatureCode).ToHashSet();
        var expectedCodes = TenancyFeatureCatalog.Catalog.Select(e => e.Code).ToHashSet();

        seededCodes.Should().BeEquivalentTo(expectedCodes);
    }

    [Fact]
    public async Task SeedAsync_ScmDemoOrganizationFeatures_AllEnabled()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");
        var orgFeatures = db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id).ToList();

        orgFeatures.Should().HaveCount(db.FeatureDefinitions.Count());
        orgFeatures.Should().OnlyContain(f => f.IsEnabled);
    }

    [Fact]
    public async Task SeedAsync_UserAndRoleManagement_AreMarkedCore()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        db.FeatureDefinitions.Single(f => f.FeatureCode == "SCREEN_USER_MANAGEMENT").IsCore.Should().BeTrue();
        db.FeatureDefinitions.Single(f => f.FeatureCode == "SCREEN_ROLE_MANAGEMENT").IsCore.Should().BeTrue();
    }

    [Fact]
    public async Task PlanTemplates_Basic_ExcludesFinance()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        var finance = db.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_FINANCE");
        var basicRow = db.PlanFeatureTemplates.Single(t => t.Plan == "BASIC" && t.FeatureDefinitionId == finance.Id);

        basicRow.IsEnabledByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task PlanTemplates_Enterprise_IncludesEverything()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();

        var enterpriseRows = db.PlanFeatureTemplates.Where(t => t.Plan == "ENTERPRISE").ToList();

        enterpriseRows.Should().HaveCount(db.FeatureDefinitions.Count());
        enterpriseRows.Should().OnlyContain(t => t.IsEnabledByDefault);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent_RunTwice_NoDuplicatesOrExceptions()
    {
        var (db, seeder, _, _, _) = Build.New();

        await seeder.SeedAsync();
        var act = async () => await seeder.SeedAsync();
        await act.Should().NotThrowAsync();

        db.Organizations.Count().Should().Be(1);
        db.FeatureDefinitions.Count().Should().Be(TenancyFeatureCatalog.Catalog.Count);
        db.PlanFeatureTemplates.Count().Should().Be(TenancyFeatureCatalog.Catalog.Count * TenancyFeatureCatalog.AllPlans.Count);
        db.OrganizationFeatures.Count().Should().Be(TenancyFeatureCatalog.Catalog.Count);
    }

    [Fact]
    public async Task SeedAsync_BackfillsMissingOrganizationFeatureRow_ForExistingOrg()
    {
        var (db, seeder, _, _, _) = Build.New();
        await seeder.SeedAsync();

        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");
        var someFeature = db.FeatureDefinitions.First();
        var row = db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == someFeature.Id);
        db.OrganizationFeatures.Remove(row);
        await db.SaveChangesAsync();

        (await db.OrganizationFeatures.CountAsync(f => f.OrganizationId == org.Id))
            .Should().Be(TenancyFeatureCatalog.Catalog.Count - 1);

        // Simulates the next deploy re-running the seeder after a gap was introduced.
        await seeder.SeedAsync();

        var restored = await db.OrganizationFeatures
            .SingleOrDefaultAsync(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == someFeature.Id);

        restored.Should().NotBeNull();
        restored!.IsEnabled.Should().BeTrue();
    }
}

public class UpdateFeaturesTests
{
    private static async Task<Guid> ToggleOneAsync(ITenancyService service, Guid orgId, string code, bool isEnabled, int modifiedBy = 1)
    {
        await service.UpdateFeaturesAsync(orgId, [new FeatureToggleItem { FeatureCode = code, IsEnabled = isEnabled }], modifiedBy);
        return orgId;
    }

    [Fact]
    public async Task UpdateFeatures_CoreFeature_Disable_IsRejected()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        var act = async () => await ToggleOneAsync(service, org.Id, "SCREEN_USER_MANAGEMENT", false);

        await act.Should().ThrowAsync<UnprocessableEntityException>();

        var feature = db.FeatureDefinitions.Single(f => f.FeatureCode == "SCREEN_USER_MANAGEMENT");
        db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == feature.Id)
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFeatures_EnablingMir_AutoEnablesInventory_WhenNotAlreadyEnabled()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        // Start from a clean slate: both off.
        await ToggleOneAsync(service, org.Id, "MODULE_MIR", false);
        await ToggleOneAsync(service, org.Id, "MODULE_INVENTORY", false);

        var result = await service.UpdateFeaturesAsync(
            org.Id, [new FeatureToggleItem { FeatureCode = "MODULE_MIR", IsEnabled = true }], modifiedBy: 1);

        result.AutoEnabledDependencies.Should().Contain("MODULE_INVENTORY");

        var inventory = db.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_INVENTORY");
        db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == inventory.Id)
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFeatures_EnablingMir_DoesNotDuplicateOrOverride_WhenInventoryAlreadyEnabled()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        await ToggleOneAsync(service, org.Id, "MODULE_MIR", false);
        await ToggleOneAsync(service, org.Id, "MODULE_INVENTORY", true);

        var result = await service.UpdateFeaturesAsync(
            org.Id, [new FeatureToggleItem { FeatureCode = "MODULE_MIR", IsEnabled = true }], modifiedBy: 1);

        result.AutoEnabledDependencies.Should().BeEmpty();

        var inventory = db.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_INVENTORY");
        db.OrganizationFeatures.Count(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == inventory.Id)
            .Should().Be(1);
    }

    [Fact]
    public async Task UpdateFeatures_DisablingInventory_WhileMirEnabled_IsRejected_NamingBothCodes()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        // SCM-DEMO is seeded with everything enabled, so MIR is already on.
        var act = async () => await ToggleOneAsync(service, org.Id, "MODULE_INVENTORY", false);

        var ex = await act.Should().ThrowAsync<UnprocessableEntityException>();
        ex.Which.Message.Should().Contain("MODULE_INVENTORY").And.Contain("MODULE_MIR");

        var inventory = db.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_INVENTORY");
        db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == inventory.Id)
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFeatures_MixedValidAndInvalidBatch_WritesNothing()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        var finance = db.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_FINANCE");
        var financeBefore = db.OrganizationFeatures
            .Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == finance.Id).IsEnabled;

        // One legitimate toggle (Finance) bundled with one that violates core-protection —
        // the whole batch must be rejected, including the otherwise-valid Finance change.
        var act = async () => await service.UpdateFeaturesAsync(org.Id,
            [
                new FeatureToggleItem { FeatureCode = "MODULE_FINANCE", IsEnabled = !financeBefore },
                new FeatureToggleItem { FeatureCode = "SCREEN_USER_MANAGEMENT", IsEnabled = false }
            ], modifiedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>();

        db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == finance.Id)
            .IsEnabled.Should().Be(financeBefore);
    }

    [Fact]
    public async Task UpdateFeatures_UnknownFeatureCode_IsRejected()
    {
        var (db, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        var act = async () => await ToggleOneAsync(service, org.Id, "MODULE_DOES_NOT_EXIST", true);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }
}

public class OrganizationTests
{
    [Fact]
    public async Task CreateOrganization_ClonesPlanTemplate_IntoOrganizationFeatures()
    {
        var (connection, newScope) = Build.NewSqlite();
        using var _ = connection;

        var (_, seeder, service, _, _) = newScope();
        await seeder.SeedAsync();

        var result = await service.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "ACME",
            OrgName = "Acme Corp",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = "jane.doe@acme.test"
        }, createdBy: 1);

        // Fresh scope for assertions — mirrors a new request against the same database.
        var (db2, _, _, _, _) = newScope();
        var financeFeature = db2.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_FINANCE");
        var coreFeature     = db2.FeatureDefinitions.Single(f => f.FeatureCode == "SCREEN_USER_MANAGEMENT");

        db2.OrganizationFeatures.Single(f => f.OrganizationId == result.OrganizationId && f.FeatureDefinitionId == financeFeature.Id)
            .IsEnabled.Should().BeFalse();
        db2.OrganizationFeatures.Single(f => f.OrganizationId == result.OrganizationId && f.FeatureDefinitionId == coreFeature.Id)
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrganization_CreatesAdminUser_ViaOrgUserProvisioningService()
    {
        var (connection, newScope) = Build.NewSqlite();
        using var _ = connection;

        var (_, seeder, service, mock, _) = newScope();
        await seeder.SeedAsync();

        var result = await service.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "ACME3",
            OrgName = "Acme Three",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = "jane.doe3@acme.test"
        }, createdBy: 1);

        result.AdminUserId.Should().Be(1);
        mock.Verify(x => x.CreateOrgAdminUserAsync(
            It.Is<CreateOrgAdminUserRequest>(r => r.Email == "jane.doe3@acme.test" && r.OrganizationId == result.OrganizationId),
            It.IsAny<System.Data.Common.DbTransaction>()), Times.Once);
    }

    // MT-006 — "Creating a new organization auto-seeds default workflow definitions".
    [Fact]
    public async Task CreateOrganization_SeedsDefaultWorkflows_ViaWorkflowSeedingService_InTheSameTransaction()
    {
        var (connection, newScope) = Build.NewSqlite();
        using var _ = connection;

        var (_, seeder, service, _, workflowSeedingMock) = newScope();
        await seeder.SeedAsync();

        var result = await service.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "ACME4",
            OrgName = "Acme Four",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = "jane.doe4@acme.test"
        }, createdBy: 1);

        workflowSeedingMock.Verify(x => x.SeedDefaultWorkflowsAsync(
            result.OrganizationId, It.IsAny<System.Data.Common.DbTransaction>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrganization_DuplicateOrgCode_ThrowsBadRequest()
    {
        var (_, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();

        var act = async () => await service.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "SCM-DEMO",
            OrgName = "Duplicate",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = "jane.doe@dup.test"
        }, createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task CreateOrganization_MissingAdminEmail_ThrowsBadRequest()
    {
        var (_, seeder, service, _, _) = Build.New();
        await seeder.SeedAsync();

        var act = async () => await service.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "ACME4",
            OrgName = "Acme Four",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = ""
        }, createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task ApplyPlanTemplate_OverwritesManualToggles()
    {
        var (connection, newScope) = Build.NewSqlite();
        using var _ = connection;

        var (_, seeder, creationService, __, ___) = newScope();
        await seeder.SeedAsync();

        var result = await creationService.CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "ACME2",
            OrgName = "Acme Two",
            Plan    = "BASIC",
            AdminFirstName = "Jane",
            AdminLastName  = "Doe",
            AdminEmail     = "jane.doe2@acme.test"
        }, createdBy: 1);
        var id = result.OrganizationId;

        // Fresh scope — CreateOrganizationWithAdminAsync's manually-managed transaction leaves the
        // originating context's transaction state unusable for further calls (see NewSqlite doc
        // comment); ordinary (non-transactional) calls below can safely share one fresh scope.
        var (db2, _, service, _, _) = newScope();

        // Manually enable Finance, which BASIC excludes by default.
        await service.UpdateFeaturesAsync(id, [new FeatureToggleItem { FeatureCode = "MODULE_FINANCE", IsEnabled = true }], modifiedBy: 1);
        var financeFeature = db2.FeatureDefinitions.Single(f => f.FeatureCode == "MODULE_FINANCE");
        db2.OrganizationFeatures.Single(f => f.OrganizationId == id && f.FeatureDefinitionId == financeFeature.Id)
            .IsEnabled.Should().BeTrue();

        await service.ApplyPlanTemplateAsync(id, modifiedBy: 1);

        db2.OrganizationFeatures.Single(f => f.OrganizationId == id && f.FeatureDefinitionId == financeFeature.Id)
            .IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DeactivateOrganization_SetsIsActiveFalse_AndInvalidatesSessions()
    {
        var (db, seeder, service, mock, _) = Build.New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");

        var updated = await service.DeactivateOrganizationAsync(org.Id, modifiedBy: 1);

        updated.Should().BeTrue();
        db.Organizations.Single(o => o.Id == org.Id).IsActive.Should().BeFalse();
        mock.Verify(x => x.DeleteActiveSessionsForOrganizationAsync(org.Id), Times.Once);
    }

    [Fact]
    public async Task DeactivateOrganization_UnknownId_ReturnsFalse_AndDoesNotTouchSessions()
    {
        var (_, seeder, service, mock, _) = Build.New();
        await seeder.SeedAsync();

        var updated = await service.DeactivateOrganizationAsync(Guid.NewGuid(), modifiedBy: 1);

        updated.Should().BeFalse();
        mock.Verify(x => x.DeleteActiveSessionsForOrganizationAsync(It.IsAny<Guid>()), Times.Never);
    }
}
