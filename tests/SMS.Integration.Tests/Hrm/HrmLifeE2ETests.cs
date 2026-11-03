using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 (LIFE) — H6/H6E on the real host (LocalDB): DI wiring, feature gates, the real IOrganizationNameLookup behind a letter (a null answer
/// would be 400 LETTER_ORGANIZATION_REQUIRED), the real workflow engine calling the EXPENSE_CLAIM handler, the real gateway.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmLifeE2ETests</c>.
/// </summary>
public sealed class HrmLifeE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    public HrmLifeE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A49L");

    [Fact]
    public async Task Letters_loans_and_an_expense_claim_work_through_the_host()
    {
        var o = await _root.OrgAsync("LIF", currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o);
        (await k.Get("/api/hrm/loans")).Status.Should().Be(HttpStatusCode.Forbidden, "the sub-feature is off");
        await _root.LicenceAsync(o.OrgId, ("FEATURE_HRM_LOANS", true), ("FEATURE_HRM_EXPENSES", true));

        var branch = await k.Ok(k.Post("/api/hrm/branches", new { code = "khi", name = "Karachi" }), "branch");
        var center = await k.Ok(k.Post("/api/hrm/cost-centers", new { code = "OPS", name = "Operations" }), "cost center");
        var department = await k.Ok(k.Post("/api/hrm/departments", new { code = "OPS", name = "Operations", costCenterUuid = center.G("uuid") }), "department");
        var designation = await k.Ok(k.Post("/api/hrm/designations", new { code = "CLERK", name = "Clerk", level = 1 }), "designation");
        await k.Ok(k.Put("/api/hrm/settings", new { payrollCostCenterUuid = center.G("uuid") }), "settings");

        var approver = await k.LoginWithPermissionsAsync("lifeappr", "HRM_EXPENSE_APPROVE", "HRM_EXPENSE_VIEW", "HRM_SCOPE_ALL");
        var staff = await k.LoginWithPermissionsAsync("lifestaff", "HRM_SELF_SERVICE");
        object Hire(Guid? manager = null) => new { effectiveFrom = "2026-01-01", departmentUuid = department.G("uuid"), designationUuid = designation.G("uuid"), branchUuid = branch.G("uuid"), managerUuid = manager };

        var boss = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Boss", lastName = "One", userId = ServiceKit.AdminUserId(approver), employment = Hire() }), "boss");
        var employee = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Zainab", lastName = "Malik", userId = ServiceKit.AdminUserId(staff), employment = Hire(boss.G("uuid")) }), "employee");
        var uuid = employee.G("uuid");

        // Letters: {OrganizationName} comes from Tenancy's lookup; no fallback name is sent.
        var templates = (await k.Ok(k.Get("/api/hrm/letter-templates"), "templates")).EnumerateArray().ToList();
        var experience = templates.Single(t => t.S("code") == "ExperienceCertificate");
        var letter = await k.Ok(k.Post("/api/hrm/letters", new { employeeUuid = uuid, templateUuid = experience.G("uuid") }), "the real lookup must answer for a real organization");
        letter.S("issuedByName").Should().NotBeNullOrEmpty();
        var pdf = await k.Get($"/api/hrm/letters/{letter.G("uuid")}/pdf");
        pdf.Status.Should().Be(HttpStatusCode.OK);
        pdf.ToString().Should().Contain("%PDF");
        (await k.Ok(k.Get("/api/hrm/letters/me", staff), "own letters")).GetArrayLength().Should().Be(1);
        (await k.Get($"/api/hrm/letters/{letter.G("uuid")}/pdf", approver)).Status.Should().Be(HttpStatusCode.Forbidden, "no role and not theirs");

        // Loan through approval to disbursement: the real gateway answers, whatever the organization's targets are.
        var loan = await k.Ok(k.Post("/api/hrm/loans", new { employeeUuid = uuid, type = "Personal", principal = 1000m, installmentCount = 3, startMonth = "2026-03-01" }), "loan");
        loan.A("installments").Sum(i => i.GetProperty("amount").GetDecimal()).Should().Be(1000m);
        (await k.Get("/api/hrm/loans", staff)).Status.Should().Be(HttpStatusCode.Forbidden);
        await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/approve"), "approve").ContinueWith(t => t.Result.S("approvedByName").Should().NotBeNullOrEmpty());
        var disbursed = await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/disburse", new { disbursedOn = "2026-03-01", paidFromAccount = "Bank" }), "disburse");
        disbursed.S("status").Should().Be("Disbursed");
        disbursed.S("postingState").Should().BeOneOf(new[] { "Posted", "NoTarget" }, $"the gateway answered: {disbursed.GetRawText()}");

        // Claim: the staff member submits, their manager approves through the real engine, which calls the handler back.
        var category = await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "TAXI", name = "Taxi", accountRole = "DEFAULT_SALARY_EXPENSE" }), "category");
        var claim = await k.Ok(k.Post("/api/hrm/expense-claims", new
        {
            settlementMethod = "SeparatePayment",
            lines = new[] { new { categoryUuid = category.G("uuid"), expenseDate = "2026-03-10", amount = 500m, taxAmount = 0m } }
        }, staff), "claim");
        var submitted = await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/submit", null, staff), "submit: workflow EXPENSE_CLAIM");
        submitted.S("status").Should().Be("Submitted");
        submitted.G("approvalUuid").Should().NotBeEmpty("the real engine opened an approval");
        (await k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, staff)).Status.Should().Be(HttpStatusCode.Forbidden);
        // Holds the permission and the scope but is not the step's approver: only the real engine can refuse this.
        var bystander = await k.LoginWithPermissionsAsync("lifeby", "HRM_EXPENSE_APPROVE", "HRM_EXPENSE_VIEW", "HRM_SCOPE_ALL");
        (await k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, bystander)).Status.Should().Be(HttpStatusCode.Forbidden);
        var approved = await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, approver), "approve through the engine");
        approved.S("status").Should().Be("Approved");
        approved.S("postingState").Should().BeOneOf(new[] { "Posted", "NoTarget" }, $"the gateway answered: {approved.GetRawText()}");
        approved.S("decidedByName").Should().NotBeNullOrEmpty("the route passed the decider to the engine's callback");

        var payer = await k.LoginWithPermissionsAsync("lifepay", "HRM_EXPENSE_PAY", "HRM_SCOPE_ALL");
        var paid = await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/pay", new { paidOn = "2026-03-20", paidFromAccount = "Bank" }, payer), "pay");
        paid.S("status").Should().Be("Paid");
        paid.S("paymentPostingState").Should().BeOneOf(new[] { "Posted", "NoTarget" }, $"the gateway answered: {paid.GetRawText()}");
        (await k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/pay", new { paidOn = "2026-03-20" }, payer)).Status.Should().Be(HttpStatusCode.Conflict, "never paid twice");
    }
}


