using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// The sale order settings screen picks its intimation department from this list.
public class OrgChartDepartmentsTests
{
    private static AuthDbContext NewDb(string dbName, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

    [Fact]
    public async Task Lists_the_departments_of_the_callers_organization_by_name_and_says_which_have_a_head()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        using (var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA }))
        {
            db.Departments.AddRange(
                new Department { DepartmentId = 2, Name = "Warehouse", Code = "WH", HeadUserId = 7, OrganizationId = orgA },
                new Department { DepartmentId = 1, Name = "Accounts", Code = null, HeadUserId = null, OrganizationId = orgA });
            await db.SaveChangesAsync();
        }
        using (var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgB }))
        {
            db.Departments.Add(new Department { DepartmentId = 3, Name = "Elsewhere", HeadUserId = 9, OrganizationId = orgB });
            await db.SaveChangesAsync();
        }

        using var read = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var result = await new OrgChartService(read).GetDepartmentsAsync();

        result.Should().Equal(
            new DepartmentSummary(1, "Accounts", null, false),
            new DepartmentSummary(2, "Warehouse", "WH", true));
    }

    [Fact]
    public async Task An_organization_with_no_departments_gets_an_empty_list()
    {
        using var db = NewDb(Guid.NewGuid().ToString(), new StaticTenantContext { OrganizationId = Guid.NewGuid() });

        (await new OrgChartService(db).GetDepartmentsAsync()).Should().BeEmpty();
    }
}
