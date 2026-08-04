using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Repositories;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

// MT-006 acceptance criteria: WorkflowDefinitions/Steps are isolated per organization, the default
// template set is cloned per-org (not a one-time global seed), and editing one org's definition
// never touches another org's definition for the same interface_code.
public class TenantScopingTests
{
    private static readonly string[] ExpectedInterfaceCodes =
        ["PR", "PO", "GRN_QC", "GRN", "MIR_PROJECT", "MIR_GENERAL"];

    private static WorkflowDbContext NewDb(string dbName, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

    [Fact]
    public async Task Query_WithoutExplicitFilter_ReturnsOnlyCurrentOrgsDefinitions()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
            await new WorkflowDefinitionSeeder(dbA).SeedDefaultWorkflowsAsync(orgA.OrganizationId, null);
        using (var dbB = NewDb(dbName, orgB))
            await new WorkflowDefinitionSeeder(dbB).SeedDefaultWorkflowsAsync(orgB.OrganizationId, null);

        using var queryAsOrgA = NewDb(dbName, orgA);
        var visible = await queryAsOrgA.WorkflowDefinitions.Where(d => d.InterfaceCode == "PR").ToListAsync();

        visible.Should().ContainSingle();
        visible.Single().OrganizationId.Should().Be(orgA.OrganizationId);
    }

    [Fact]
    public async Task SuperAdmin_SeesBothOrganizationsDefinitions()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
            await new WorkflowDefinitionSeeder(dbA).SeedDefaultWorkflowsAsync(orgA.OrganizationId, null);
        using (var dbB = NewDb(dbName, orgB))
            await new WorkflowDefinitionSeeder(dbB).SeedDefaultWorkflowsAsync(orgB.OrganizationId, null);

        var superAdmin = new StaticTenantContext { IsSuperAdmin = true };
        using var asSuperAdmin = NewDb(dbName, superAdmin);
        var repo = new WorkflowDefinitionRepository(asSuperAdmin);

        var all = await repo.GetListAsync(new WorkflowDefinitionListFilter { InterfaceCode = "PR", PageSize = 100 });

        all.Data.Should().HaveCount(2);
        all.Data.Select(d => d.OrganizationId).Should().BeEquivalentTo([orgA.OrganizationId, orgB.OrganizationId]);
    }

    [Fact]
    public async Task SuperAdmin_CanFilterListByOrganizationId_ForSideBySideComparison()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
            await new WorkflowDefinitionSeeder(dbA).SeedDefaultWorkflowsAsync(orgA.OrganizationId, null);
        using (var dbB = NewDb(dbName, orgB))
            await new WorkflowDefinitionSeeder(dbB).SeedDefaultWorkflowsAsync(orgB.OrganizationId, null);

        var superAdmin = new StaticTenantContext { IsSuperAdmin = true };
        using var asSuperAdmin = NewDb(dbName, superAdmin);
        var repo = new WorkflowDefinitionRepository(asSuperAdmin);

        var onlyOrgA = await repo.GetListAsync(new WorkflowDefinitionListFilter { OrganizationId = orgA.OrganizationId, PageSize = 100 });

        onlyOrgA.Data.Should().NotBeEmpty();
        onlyOrgA.Data.Should().OnlyContain(d => d.OrganizationId == orgA.OrganizationId);
    }

    [Fact]
    public async Task SeedDefaultWorkflowsAsync_CreatesAllStandardInterfaceCodes_ScopedToTargetOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgId = Guid.NewGuid();

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgId });
        await new WorkflowDefinitionSeeder(db).SeedDefaultWorkflowsAsync(orgId, null);

        var codes = await db.WorkflowDefinitions.Select(d => d.InterfaceCode).ToListAsync();
        codes.Should().BeEquivalentTo(ExpectedInterfaceCodes);

        (await db.WorkflowDefinitions.AllAsync(d => d.OrganizationId == orgId)).Should().BeTrue();
        (await db.WorkflowSteps.AllAsync(s => s.OrganizationId == orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task SeedDefaultWorkflowsAsync_IsIdempotentPerOrg_DoesNotDuplicateOnSecondCall()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgId = Guid.NewGuid();

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgId });
        var seeder = new WorkflowDefinitionSeeder(db);

        await seeder.SeedDefaultWorkflowsAsync(orgId, null);
        await seeder.SeedDefaultWorkflowsAsync(orgId, null);

        var count = await db.WorkflowDefinitions.CountAsync(d => d.InterfaceCode == "PR");
        count.Should().Be(1);
    }

    [Fact]
    public async Task TwoOrganizations_BothGetTheirOwnMirWorkflow_WithIndependentStepConfigurations()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
            await new WorkflowDefinitionSeeder(dbA).SeedDefaultWorkflowsAsync(orgA.OrganizationId, null);
        using (var dbB = NewDb(dbName, orgB))
            await new WorkflowDefinitionSeeder(dbB).SeedDefaultWorkflowsAsync(orgB.OrganizationId, null);

        Guid orgAMirUuid;
        using (var dbA = NewDb(dbName, orgA))
        {
            var mir = await dbA.WorkflowDefinitions.Include(d => d.Steps)
                .SingleAsync(d => d.InterfaceCode == "MIR_PROJECT");
            mir.Steps.Should().HaveCount(5);
            orgAMirUuid = mir.UUID;
        }

        // Org A customizes its MIR_PROJECT workflow down to a single step.
        using (var dbA = NewDb(dbName, orgA))
        {
            var repo = new WorkflowDefinitionRepository(dbA);
            await repo.UpdateInPlaceAsync(orgAMirUuid, new UpdateWorkflowDefinitionRequest
            {
                Name = "Org A Custom MIR",
                SlaHours = 96,
                Steps =
                [
                    new AddWorkflowStepRequest
                    {
                        StepNumber = 1, StepName = "Sole Approver", ApproverType = "ROLE",
                        ApproverRefId = 1, IsMandatory = true, ApprovalMode = "ANY_ONE", CanReject = true
                    }
                ]
            }, userId: 1);
        }

        // Org B's MIR_PROJECT definition must be completely untouched.
        using var dbB2 = NewDb(dbName, orgB);
        var orgBMir = await dbB2.WorkflowDefinitions.Include(d => d.Steps)
            .SingleAsync(d => d.InterfaceCode == "MIR_PROJECT");

        orgBMir.Name.Should().Be("Material Issue Request — Project Type");
        orgBMir.Steps.Should().HaveCount(5);
        orgBMir.OrganizationId.Should().Be(orgB.OrganizationId);

        // And Org A's own change did take effect, scoped to its own org.
        using var dbA2 = NewDb(dbName, orgA);
        var orgAMir = await dbA2.WorkflowDefinitions.Include(d => d.Steps)
            .SingleAsync(d => d.InterfaceCode == "MIR_PROJECT" && d.UUID == orgAMirUuid);
        orgAMir.Steps.Should().ContainSingle();
        orgAMir.OrganizationId.Should().Be(orgA.OrganizationId);
    }
}
