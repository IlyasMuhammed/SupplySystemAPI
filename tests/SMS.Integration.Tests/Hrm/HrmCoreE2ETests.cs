using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 (CORE) — H1 on the real host (LocalDB): off for a new organization, licensed by hand, then org structure and an employee over HTTP
/// with masking, reveal and permission gates. Proves the DI wiring, the startup migrate and the module gate. QA owns the full E2E.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmCoreE2ETests</c>.
/// </summary>
public sealed class HrmCoreE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    public HrmCoreE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A49");

    [Fact]
    public async Task Hrm_is_off_for_a_new_organization_then_works_end_to_end_once_licensed()
    {
        var o = await _root.OrgAsync("HRM", currency: false);
        var k = o.K;

        (await k.Get("/api/hrm/branches")).Status.Should().Be(HttpStatusCode.Forbidden, "not licensed: the module gate answers");

        await _root.EnableHrmAsync(o);

        var branch = await k.Ok(k.Post("/api/hrm/branches", new { code = "khi", name = "Karachi" }), "branch");
        branch.S("code").Should().Be("KHI");
        var center = await k.Ok(k.Post("/api/hrm/cost-centers", new { code = "OPS", name = "Operations" }), "cost center");
        var department = await k.Ok(k.Post("/api/hrm/departments", new { code = "OPS", name = "Operations", costCenterUuid = center.G("uuid") }), "department");
        var designation = await k.Ok(k.Post("/api/hrm/designations", new { code = "CLERK", name = "Clerk", level = 1 }), "designation");

        var settings = await k.Ok(k.Put("/api/hrm/settings", new { payrollCostCenterUuid = center.G("uuid") }), "settings");
        settings.S("payrollCostCenterCode").Should().Be("OPS");

        var employee = await k.Ok(k.Post("/api/hrm/employees", new
        {
            firstName = "Ali", lastName = "Khan", cnic = "42101-1234567-3",
            employment = new { effectiveFrom = "2026-01-01", departmentUuid = department.G("uuid"), designationUuid = designation.G("uuid"), branchUuid = branch.G("uuid") }
        }), "employee");
        employee.S("employeeNumber").Should().Be("EMP-0001");
        employee.S("cnicMasked").Should().Be("*****-*******-3");
        employee.GetRawText().Should().NotContain("4210112345673").And.NotContain("42101-1234567-3");
        var uuid = employee.G("uuid");

        var duplicate = await k.Post("/api/hrm/employees", new { firstName = "Other", lastName = "One", cnic = "4210112345673" });
        duplicate.Status.Should().Be(HttpStatusCode.Conflict);

        // Org Admin holds the reveal code; the seed data arrived on first use.
        var revealed = await k.Ok(k.Get($"/api/hrm/employees/{uuid}/sensitive"), "reveal");
        revealed.S("cnic").Should().Be("42101-1234567-3");

        var viewer = await k.LoginWithPermissionsAsync("hrviewer", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        (await k.Get("/api/hrm/employees?pageSize=10", viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Get($"/api/hrm/employees/{uuid}/sensitive", viewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Post("/api/hrm/branches", new { code = "X", name = "X" }, viewer)).Status.Should().Be(HttpStatusCode.Forbidden);

        // The caller's scope reaches nobody without HRM_SCOPE_ALL: 404, not 403.
        var noScope = await k.LoginWithPermissionsAsync("hrnoscope", "HRM_EMPLOYEE_VIEW");
        (await k.Get($"/api/hrm/employees/{uuid}", noScope)).Status.Should().Be(HttpStatusCode.NotFound);

        // Another organization sees nothing of it.
        var other = await _root.OrgAsync("HRX", currency: false);
        await _root.EnableHrmAsync(other);
        (await other.K.Get($"/api/hrm/employees/{uuid}")).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
