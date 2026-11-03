using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 (CORE, D-24) — the real token has no name claim: the stored "...ByName" snapshots must still hold the acting user's name. One action
/// per agent's area, read back from the database, plus the lookup outside a request, as a Hangfire job runs it. Unit tests pass a name
/// to HrmCaller by hand, which is how this stayed hidden.
/// </summary>
public sealed class HrmUserNameE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string AdminName = "Module Admin"; // Mods.OrgAsync's org admin
    private readonly SapWebApplicationFactory _factory;
    private readonly SapKit _root;

    public HrmUserNameE2ETests(SapWebApplicationFactory factory)
    {
        _factory = factory;
        _root = new SapKit(factory, "A49N");
    }

    private async Task<T> ReadAsync<T>(Func<HrmDbContext, Task<T>> read)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<HrmDbContext>());
    }

    [Fact]
    public async Task Stored_names_are_populated_for_core_att_pay_tal_and_life_actions()
    {
        var o = await _root.OrgAsync("HNM", currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o, "FEATURE_HRM_ATTENDANCE", "FEATURE_HRM_PAYROLL", "FEATURE_HRM_TRAINING", "FEATURE_HRM_LOANS");

        // CORE: Stamp, and the audit row of a reveal.
        var branch = await k.Ok(k.Post("/api/hrm/branches", new { code = "khi", name = "Karachi" }), "branch");
        var department = await k.Ok(k.Post("/api/hrm/departments", new { code = "OPS", name = "Operations" }), "department");
        var designation = await k.Ok(k.Post("/api/hrm/designations", new { code = "CLERK", name = "Clerk", level = 1 }), "designation");
        var employee = await k.Ok(k.Post("/api/hrm/employees", new
        {
            firstName = "Ali", lastName = "Khan", cnic = "42101-1234567-3",
            employment = new { effectiveFrom = "2026-01-01", departmentUuid = department.G("uuid"), designationUuid = designation.G("uuid"), branchUuid = branch.G("uuid") }
        }), "employee");
        var uuid = employee.G("uuid");
        await k.Ok(k.Get($"/api/hrm/employees/{uuid}/sensitive"), "reveal");
        var branchUuid = branch.G("uuid");
        (await ReadAsync(db => db.Branches.IgnoreQueryFilters().Where(b => b.Uuid == branchUuid).Select(b => b.CreatedByName).SingleAsync())).Should().Be(AdminName);
        (await ReadAsync(db => db.SensitiveAccessLogs.IgnoreQueryFilters().Where(l => l.OrganizationId == o.OrgId).Select(l => l.UserName).FirstAsync())).Should().Be(AdminName);

        // ATT: overtime decision.
        var overtime = await k.Ok(k.Post("/api/hrm/overtime", new { employeeUuid = uuid, date = "2026-02-02", minutes = 60 }), "overtime");
        var decided = await k.Ok(k.Post($"/api/hrm/overtime/{overtime.G("uuid")}/approve"), "approve overtime");
        decided.S("decidedByName").Should().Be(AdminName);

        // PAY: a statutory rate marked reviewed.
        var rates = (await k.Ok(k.Get("/api/hrm/statutory-rates"), "rates")).EnumerateArray().ToList();
        var rateUuid = rates.First().G("uuid");
        await k.Ok(k.Post($"/api/hrm/statutory-rates/{rateUuid}/verify", new { note = "test" }), "verify");
        (await ReadAsync(db => db.StatutoryRates.IgnoreQueryFilters().Where(r => r.Uuid == rateUuid).Select(r => r.VerifiedByName).SingleAsync())).Should().Be(AdminName);

        // TAL: Stamp through its own service.
        var course = await k.Ok(k.Post("/api/hrm/courses", new { code = "SAFE", name = "Safety" }), "course");
        var courseUuid = course.G("uuid");
        (await ReadAsync(db => db.Courses.IgnoreQueryFilters().Where(c => c.Uuid == courseUuid).Select(c => c.CreatedByName).SingleAsync())).Should().Be(AdminName);

        // LIFE: a loan approval and its Stamp.
        var loan = await k.Ok(k.Post("/api/hrm/loans", new { employeeUuid = uuid, type = "Personal", principal = 1000m, installmentCount = 2, startMonth = "2026-03-01" }), "loan");
        (await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/approve"), "approve loan")).S("approvedByName").Should().Be(AdminName);
        var loanUuid = loan.G("uuid");
        (await ReadAsync(db => db.Loans.IgnoreQueryFilters().Where(l => l.Uuid == loanUuid).Select(l => l.CreatedByName).SingleAsync())).Should().Be(AdminName);
    }

    [Fact]
    public async Task The_name_resolves_outside_a_request_as_a_job_runs_it()
    {
        var o = await _root.OrgAsync("HNJ", currency: false);
        var userId = await UserIdAsync(o);

        foreach (var jobOrganization in new Guid?[] { o.OrgId, null })
        {
            HangfireTenantScope.OrganizationId = jobOrganization;
            try
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                var names = scope.ServiceProvider.GetRequiredService<HrmUserNames>();
                (await names.NameAsync(o.OrgId, userId)).Should().Be(AdminName);
                (await names.NameAsync(o.OrgId, 0, "kept")).Should().Be("kept", "no user id: what the job already knows");
                (await names.NameAsync(Guid.NewGuid(), userId, "kept")).Should().Be("kept", "not a user of that organization");
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
    }

    private async Task<int> UserIdAsync(ModOrg o)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IOrgUserDirectory>();
        return (await users.SearchAsync(o.OrgId, o.Email, 1)).Single().UserId;
    }
}
