using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.DataImport;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.JournalEngine;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 QA, task 412: recruit → hire → attendance → leave → payroll → loan, expense → review → increment → tax certificate → remittance → settlement,
/// one person, one organization, real host, real GL. Also the routes the agents flagged as never hosted.
/// Run alone: dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmHireToRetireE2ETests
/// </summary>
public sealed class HrmHireToRetireE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public HrmHireToRetireE2ETests(SapWebApplicationFactory f)
    {
        _f = f;
        f.ShouldBeOnLocalDb();
        _root = new SapKit(f, "A49H");
    }

    private const string Runs = "/api/hrm/payroll-runs";

    [Fact]
    public async Task T412_hire_to_retire()
    {
        var o = await _root.ReadyOrgAsync("H2R");
        var k = o.K;
        var orgName = await o.OrgNameAsync();

        var bossLogin = await o.UserAsync("h2rboss", "HRM_RECRUITMENT_VIEW", "HRM_SELF_SERVICE", "HRM_LEAVE_APPROVE", "HRM_EXPENSE_APPROVE", "HRM_SALARY_APPROVE");
        var staffLogin = await o.UserAsync("h2rstaff", "HRM_SELF_SERVICE");
        var boss = (await o.EmployeeAsync("Bilal", "Boss", bossLogin)).G("uuid");

        // ── Recruit ──
        var vacancy = await k.Ok(k.Post("/api/hrm/vacancies", new { title = "Clerk", designationUuid = o.Designation, departmentUuid = o.Department, branchUuid = o.Branch, positions = 1 }), "vacancy");
        var candidate = await k.Ok(k.Post("/api/hrm/candidates", new { firstName = "Zainab", lastName = "Malik", email = "zainab@example.test", phone = "+92 300 1234567" }), "candidate");
        var dup = await k.Post("/api/hrm/candidates", new { firstName = "Z", lastName = "M", phone = "0300-1234567" });
        dup.Status.Should().Be(HttpStatusCode.Conflict, dup.ToString());
        dup.ToString().Should().Contain("CANDIDATE_DUPLICATE");
        var app = await k.Ok(k.Post("/api/hrm/applications", new { candidateUuid = candidate.G("uuid"), vacancyUuid = vacancy.G("uuid") }), "application");

        var interview = await k.Ok(k.Post($"/api/hrm/applications/{app.G("uuid")}/interviews", new { interviewerUuid = boss, scheduledAt = "2026-09-10T05:00:00Z" }), "interview");

        // api/hrm/interviews/me: only what feedback needs.
        var mine = await k.Ok(k.Get("/api/hrm/interviews/me", bossLogin), "my interviews");
        mine.Items().Should().ContainSingle().Which.S("candidateName").Should().Contain("Zainab");
        mine.GetRawText().Should().NotContain("zainab@example.test").And.NotContain("1234567");
        (await k.Get("/api/hrm/interviews/me", staffLogin)).ShouldBeForbidden("needs HRM_RECRUITMENT_VIEW");
        var fb = await k.Ok(k.Put($"/api/hrm/interviews/{interview.G("uuid")}", new { feedback = "Good", score = 4m, result = "Passed", scheduledAt = "2030-01-01T00:00:00Z" }, bossLogin), "feedback");
        fb.S("result").Should().Be("Passed");
        fb.S("scheduledAt").Should().StartWith("2026-09-10", "an interviewer's schedule edit is ignored");

        var offer = await k.Ok(k.Post($"/api/hrm/applications/{app.G("uuid")}/offers", new { offeredSalary = 100_000m, proposedJoinDate = "2026-10-01" }), "offer");
        offer.D("offeredSalary").Should().Be(100_000m);
        var noSalaryViewer = await o.UserAsync("h2rview", "HRM_RECRUITMENT_VIEW", "HRM_SCOPE_ALL");
        var masked = await k.Ok(k.Get($"/api/hrm/offers/{offer.G("uuid")}", noSalaryViewer), "offer masked");
        (masked.IsNull("offeredSalary"), masked.B("salaryMasked")).Should().Be((true, true), masked.GetRawText());

        // Offer letter through its own route, the merged organization name in the PDF's text.
        await k.Ok(k.Post($"/api/hrm/offers/{offer.G("uuid")}/letter", new { }), "offer letter");
        var offerPdf = await k.DownloadAsync($"/api/hrm/offers/{offer.G("uuid")}/letter");
        offerPdf.Status.Should().Be(HttpStatusCode.OK);
        var offerText = HrQa.PdfText(offerPdf.Bytes);
        offerText.Should().Contain(orgName).And.Contain("Zainab");
        offerText.Should().MatchRegex(@"100,?000", "the offered salary is merged");
        offerText.Should().NotContain("{OrganizationName}").And.NotContain("{Salary}");
        (await k.DownloadAsync($"/api/hrm/offers/{offer.G("uuid")}/letter", noSalaryViewer)).Status.Should().Be(HttpStatusCode.Forbidden, "D-TAL-4");

        await k.Ok(k.Patch($"/api/hrm/offers/{offer.G("uuid")}/status", new { status = "Sent" }), "sent");
        await k.Ok(k.Patch($"/api/hrm/offers/{offer.G("uuid")}/status", new { status = "Accepted" }), "accepted");

        // ── Hire ──
        var employee = await k.Ok(k.Post($"/api/hrm/applications/{app.G("uuid")}/convert", new { joinDate = "2026-10-01", managerUuid = boss, employmentType = "Permanent" }), "convert");
        var uuid = employee.G("uuid");
        employee.S("status").Should().Be("Active");
        (await k.Post($"/api/hrm/applications/{app.G("uuid")}/convert", new { joinDate = "2026-10-01" })).Status.Should().Be(HttpStatusCode.Conflict, "APPLICATION_ALREADY_HIRED");
        (await k.Ok(k.Get($"/api/hrm/vacancies/{vacancy.G("uuid")}"), "vacancy")).S("status").Should().Be("Closed");

        await k.Ok(k.Put($"/api/hrm/employees/{uuid}", new
        {
            firstName = "Zainab", lastName = "Malik", gender = "F", joinDate = "2026-10-01", userId = ServiceKit.AdminUserId(staffLogin),
            emergencyContacts = Array.Empty<object>()
        }), "link the user");
        await o.BankAsync(uuid);

        // Salary: the inbox path, approved by the line manager through the workflow routes.
        var draft = await k.Ok(k.Post($"/api/hrm/employees/{uuid}/salary", new { structureUuid = o.Structure, effectiveFrom = "2026-10-01", lines = new[] { new { componentUuid = o.Components["BASIC"], amount = 100_000m } } }), "salary draft");
        var submitted = await k.Ok(k.Post($"/api/hrm/salary-assignments/{draft.G("uuid")}/submit"), "salary submit");
        submitted.S("approvalStatus").Should().Be("PendingApproval");
        var salaryInbox = await k.Ok(k.Get("/api/workflow/inbox?pageSize=100", bossLogin), "inbox");
        var salaryStep = salaryInbox.A("data").Single(i => i.G("documentId") == draft.G("uuid"));
        await k.ApproveViaInboxAsync(bossLogin, salaryStep.G("approvalUUID"));
        var approved = (await k.Ok(k.Get($"/api/hrm/employees/{uuid}/salary"), "salary")).Items().Single();
        approved.S("approvalStatus").Should().Be("Approved", approved.GetRawText());
        approved.S("approvedByName").Should().Contain("SapE2E", "the inbox path records the decider's name: " + approved.GetRawText());

        (await k.Ok(k.Get($"/api/hrm/employees/{uuid}/salary/current", staffLogin), "own salary")).D("gross").Should().Be(100_000m);

        // ── Attendance: every working day of October, one late, one with overtime. ──
        var annual = (await k.Ok(k.Get("/api/hrm/leave/types"), "types")).Items().Single(t => t.S("code") == "ANNUAL");
        await k.Ok(k.Post("/api/hrm/leave/allocate", new { employeeUuid = uuid, leaveTypeUuid = annual.G("uuid"), days = 14m, year = 2026 }), "allocate");

        for (var d = new DateOnly(2026, 10, 1); d <= new DateOnly(2026, 10, 30); d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (d is { Day: 14 or 15 }) continue;
            var inH = d.Day == 5 ? 5 : 4;   // 10:00 PKT on the 5th: late
            var outH = d.Day == 6 ? 15 : 13;   // 20:00 PKT on the 6th: overtime
            await k.Ok(k.Post("/api/hrm/attendance/events", new { employeeUuid = uuid, eventTimeUtc = d.ToDateTime(new TimeOnly(inH, 0), DateTimeKind.Utc), eventType = "IN", source = "DEVICE", externalEventId = $"I{d:MMdd}" }), "in");
            await k.Ok(k.Post("/api/hrm/attendance/events", new { employeeUuid = uuid, eventTimeUtc = d.ToDateTime(new TimeOnly(outH, 0), DateTimeKind.Utc), eventType = "OUT", source = "DEVICE", externalEventId = $"O{d:MMdd}" }), "out");
        }
        (await k.Ok(k.Post("/api/hrm/attendance/events", new { employeeUuid = uuid, eventTimeUtc = "2026-10-01T04:00:00Z", eventType = "IN", source = "DEVICE", externalEventId = "I1001" }), "dup")).B("duplicate").Should().BeTrue();

        var days = await k.Ok(k.Get($"/api/hrm/attendance/days?employeeUuid={uuid}&from=2026-10-05&to=2026-10-06"), "days");
        var dayRows = days.A("data");
        dayRows.Single(r => r.S("date") == "2026-10-05").I("lateMinutes").Should().Be(60, "a 10:00 start in Karachi on a 09:00 shift: " + dayRows[0].GetRawText());
        dayRows.Single(r => r.S("date") == "2026-10-06").I("overtimeMinutes").Should().Be(120);

        // ── Leave: the inbox path. ──
        var leave = await k.Ok(k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-10-14", toDate = "2026-10-15", reason = "Family" }, staffLogin), "leave request");
        leave.D("days").Should().Be(2m);
        leave.S("status").Should().Be("Pending");
        (await k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-10-15", toDate = "2026-10-16" }, staffLogin)).Status.Should().Be(HttpStatusCode.Conflict, "LEAVE_OVERLAP");
        await k.ApproveViaInboxAsync(bossLogin, leave.G("approvalUuid"));
        var decided = await k.Ok(k.Get($"/api/hrm/leave/requests/{leave.G("uuid")}", bossLogin), "leave after the inbox");
        decided.S("status").Should().Be("Approved", decided.GetRawText());
        decided.S("decidedByName").Should().Contain("SapE2E", "the inbox path records who decided: " + decided.GetRawText());
        var balances = await k.Ok(k.Get("/api/hrm/leave/balances/me?year=2026", staffLogin), "balances");
        balances.Items().Single(b => b.S("leaveTypeCode") == "ANNUAL").D("balance").Should().Be(12m);

        // ── Overtime ──
        var ot = await k.Ok(k.Post("/api/hrm/overtime", new { employeeUuid = uuid, date = "2026-10-06", reason = "Close" }), "overtime");
        ot.I("minutes").Should().Be(120);
        await k.Ok(k.Post($"/api/hrm/overtime/{ot.G("uuid")}/approve"), "approve overtime");

        // ── Loan: recovered by this payroll. ──
        var loan = await k.Ok(k.Post("/api/hrm/loans", new { employeeUuid = uuid, type = "Personal", principal = 10_000m, installmentCount = 4, startMonth = "2026-10-01" }), "loan");
        await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/approve"), "loan approve");
        var disbursed = await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/disburse", new { disbursedOn = "2026-10-01", paidFromAccount = "Bank" }), "loan disburse");
        disbursed.S("postingState").Should().Be("Posted", disbursed.GetRawText());
        var codes = await k.DefaultCodesAsync();
        var loanJe = (await _f.JournalsAsync(o.OrgId, loan.G("uuid"))).Single();
        loanJe.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE")).Should().Be(10_000m, loanJe.ToString());
        loanJe.Net(codes["DEFAULT_BANK"]).Should().Be(-10_000m);
        (await k.Ok(k.Get("/api/hrm/loans/me", staffLogin), "own loans")).Items().Should().ContainSingle();

        // ── Expense with payroll (TS-G9-003). ──
        var category = await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "TAXI", name = "Taxi", accountRole = "DEFAULT_BONUS_EXPENSE" }), "category");
        var claim = await k.Ok(k.Post("/api/hrm/expense-claims", new
        {
            settlementMethod = "WithPayroll", lines = new[] { new { categoryUuid = category.G("uuid"), expenseDate = "2026-10-10", amount = 500m, taxAmount = 0m } }
        }, staffLogin), "claim");
        await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/submit", null, staffLogin), "submit claim");
        var claimApproved = await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, bossLogin), "approve claim");
        claimApproved.S("postingState").Should().Be("Posted", claimApproved.GetRawText());
        var claimJe = (await _f.JournalsAsync(o.OrgId, claim.G("uuid"))).Single();
        claimJe.Net(HrQa.CodeOf("DEFAULT_EXPENSE_CLAIM_PAYABLE")).Should().Be(-500m, claimJe.ToString());

        // ── Payroll. Attendance on and complete: full pay. ──
        var run = await o.ApprovedRunAsync();
        var line = (await o.LinesAsync(run)).Single(l => l.G("employeeUuid") == uuid);
        line.Amount("BASIC").Should().Be(100_000m, "worked or on approved paid leave every working day: " + line.GetRawText());
        line.Amount("LOAN").Should().Be(2_500m);
        line.Amount("EXPREIMB").Should().Be(500m);
        line.Amount("OT").Should().Be(Math.Round(100_000m / 31m / 8m * 2m, 2), "D-PAY-6: " + line.GetRawText());
        line.B("attendanceUntracked").Should().BeFalse();
        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        final.S("status").Should().Be("Posted", final.GetRawText());
        var runJe = (await _f.JournalsAsync(o.OrgId, run)).Single();
        runJe.Balanced.Should().BeTrue(runJe.ToString());
        runJe.Net(HrQa.CodeOf("DEFAULT_EXPENSE_CLAIM_PAYABLE")).Should().Be(500m, "the run clears the claim's payable: " + runJe);
        runJe.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE")).Should().Be(-2_500m);
        runJe.Net(HrQa.CodeOf("DEFAULT_OVERTIME_EXPENSE")).Should().BeGreaterThan(0m);
        (await k.Ok(k.Get($"/api/hrm/expense-claims/{claim.G("uuid")}", staffLogin), "claim after the run")).S("paidInRunNumber").Should().NotBeNullOrEmpty();
        await k.Ok(k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "OCT", paidFromAccount = "Bank" }), "pay the run");

        // A second run cannot take the same installment or claim: the Nov run's loan is the next one only.
        // ── Review: api/hrm/reviews/assigned, the reviewer holding only self service. ──
        var cycle = await k.Ok(k.Post("/api/hrm/review-cycles", new { name = "2026", period = "Annual", startDate = "2026-01-01", endDate = "2026-12-31" }), "cycle");
        await k.Ok(k.Post($"/api/hrm/review-cycles/{cycle.G("uuid")}/open"), "open cycle");
        var assigned = (await k.Ok(k.Get("/api/hrm/reviews/assigned", bossLogin), "assigned")).Items();
        var review = assigned.Single(r => r.G("employeeUuid") == uuid);
        (await k.Get("/api/hrm/reviews/assigned", await o.UserAsync("h2rnoemp", "HRM_SELF_SERVICE"))).Status.Should().Be(HttpStatusCode.NotFound, "HRM_NO_EMPLOYEE");
        var goal = await k.Ok(k.Post("/api/hrm/goals", new { employeeUuid = uuid, cycleUuid = cycle.G("uuid"), title = "Close books", weight = 100m }, bossLogin), "goal");
        await k.Ok(k.Put($"/api/hrm/goals/{goal.G("uuid")}", new { title = "Close books", weight = 100m, managerRating = 4m, status = "Achieved" }, bossLogin), "rate goal");
        var rated = await k.Ok(k.Post($"/api/hrm/reviews/{review.G("uuid")}/submit", null, bossLogin), "submit review");
        rated.D("overallRating").Should().Be(4m);
        (await k.Post($"/api/hrm/reviews/{review.G("uuid")}/acknowledge", null, bossLogin)).Status.Should().Be(HttpStatusCode.Forbidden, "the employee acknowledges");
        await k.Ok(k.Post($"/api/hrm/reviews/{review.G("uuid")}/acknowledge", null, staffLogin), "acknowledge");

        // ── Increment. ──
        var inc = await k.Ok(k.Post("/api/hrm/increments", new { employeeUuid = uuid, percent = 10m, effectiveDate = "2026-11-01", reviewUuid = review.G("uuid"), reason = "Rating 4" }), "increment");
        inc.D("newBasic").Should().Be(110_000m, inc.GetRawText());
        await k.Ok(k.Post($"/api/hrm/increments/{inc.G("uuid")}/approve"), "approve increment");
        var applied = await k.Ok(k.Post($"/api/hrm/increments/{inc.G("uuid")}/apply"), "apply increment");
        applied.S("status").Should().Be("Applied");
        var current = await k.Ok(k.Get($"/api/hrm/employees/{uuid}/salary"), "history");
        current.Items().Should().Contain(a => a.S("effectiveFrom") == "2026-11-01" && a.S("approvalStatus") == "Approved" && a.D("gross") == 110_000m, current.GetRawText());

        // ── Tax certificate through its own routes. ──
        var cert = (await k.Ok(k.Post("/api/hrm/tax-certificates/generate", new { employeeUuid = uuid, taxYear = 2027 }), "certificate")).Items().Single();
        cert.D("totalTax").Should().BeGreaterThan(0m, cert.GetRawText());
        cert.D("totalIncome").Should().BeGreaterThanOrEqualTo(100_000m);
        (await k.Ok(k.Post("/api/hrm/tax-certificates/generate", new { employeeUuid = uuid, taxYear = 2027 }), "again")).Items().Single().G("uuid").Should().Be(cert.G("uuid"), "idempotent");
        (await k.Ok(k.Get("/api/hrm/tax-certificates?taxYear=2027"), "list")).Items().Should().Contain(c => c.G("uuid") == cert.G("uuid"));
        (await k.Ok(k.Get("/api/hrm/tax-certificates/me", staffLogin), "own")).Items().Should().ContainSingle();
        var certPdf = await k.DownloadAsync($"/api/hrm/tax-certificates/{cert.G("uuid")}/pdf", staffLogin);
        certPdf.Status.Should().Be(HttpStatusCode.OK);
        HrQa.PdfText(certPdf.Bytes).Should().Contain("Zainab");
        (await k.DownloadAsync($"/api/hrm/tax-certificates/{cert.G("uuid")}/pdf", bossLogin)).Status.Should().NotBe(HttpStatusCode.OK, "another person's certificate");

        // ── Remittance: TS-G13-001. ──
        var tracker = await k.Ok(k.Get("/api/hrm/remittances/tracker?year=2026&type=IncomeTax"), "tracker");
        var taxDue = tracker.Items().Where(r => r.S("periodStart")!.StartsWith("2026-10")).Sum(r => r.D("balance"));
        taxDue.Should().Be(runJe.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_TAX_PAYABLE")) * -1, tracker.GetRawText());
        var remit = await k.Ok(k.Post("/api/hrm/remittances", new { remittanceType = "IncomeTax", periodStart = "2026-10-01", periodEnd = "2026-10-31", amount = taxDue, paidOn = "2026-10-31", paidFromAccount = "Bank", reference = "CPR-1", authorityName = "FBR" }), "remit");
        remit.S("postingState").Should().Be("Posted", remit.GetRawText());
        var remitJe = (await _f.JournalsAsync(o.OrgId, remit.G("uuid"))).Single();
        remitJe.Balanced.Should().BeTrue();
        remitJe.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_TAX_PAYABLE")).Should().Be(taxDue, remitJe.ToString());
        remitJe.Net(codes["DEFAULT_BANK"]).Should().Be(-taxDue);
        (await k.Post("/api/hrm/remittances", new { remittanceType = "IncomeTax", periodStart = "2026-10-01", periodEnd = "2026-10-31", amount = taxDue, paidOn = "2026-10-31", paidFromAccount = "Bank", reference = "CPR-1" })).Status.Should().Be(HttpStatusCode.Conflict, "REMITTANCE_DUPLICATE");
        (await k.Ok(k.Get("/api/hrm/remittances?type=IncomeTax"), "remittances")).Items().Should().ContainSingle();

        // ── Retire: notice, settlement computed, posted, paid; the letter. ──
        await k.Ok(k.Patch($"/api/hrm/employees/{uuid}/status", new { status = "Terminated", effectiveDate = "2026-11-15", departureReason = "Resignation" }), "terminate");
        var settlement = await k.Ok(k.Post("/api/hrm/settlements/compute", new { employeeUuid = uuid, lastWorkingDay = "2026-11-15" }), "compute");
        settlement.S("status").Should().Be("Draft");
        settlement.A("lines").Should().Contain(l => l.S("code") == "LOAN" && l.D("amount") == 7_500m, settlement.GetRawText());
        settlement.A("lines").Should().Contain(l => l.S("code") == "GRATUITY", settlement.GetRawText());
        (await k.Post("/api/hrm/settlements/compute", new { employeeUuid = uuid, lastWorkingDay = "2026-11-15" })).Status.Should().Be(HttpStatusCode.Conflict, "SETTLEMENT_EXISTS");
        await k.Ok(k.Post($"/api/hrm/settlements/{settlement.G("uuid")}/approve"), "approve");
        var posted = await k.Ok(k.Post($"/api/hrm/settlements/{settlement.G("uuid")}/post"), "post");
        posted.S("status").Should().Be("Posted", posted.GetRawText());
        posted.S("postingState").Should().Be("Posted", posted.GetRawText());
        var setJe = (await _f.JournalsAsync(o.OrgId, settlement.G("uuid"))).Single();
        setJe.Balanced.Should().BeTrue(setJe.ToString());
        setJe.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE")).Should().Be(-7_500m, setJe.ToString());
        var settled = await k.Ok(k.Post($"/api/hrm/settlements/{settlement.G("uuid")}/pay", new { paidOn = "2026-11-20", paidFromAccount = "Bank", reference = "FNF" }), "pay");
        settled.S("status").Should().Be("Paid", settled.GetRawText());
        (await _f.JournalsAsync(o.OrgId, settlement.G("uuid"))).Should().HaveCount(2).And.OnlyContain(j => j.Balanced);
        (await k.Ok(k.Get($"/api/hrm/loans/{loan.G("uuid")}"), "loan")).D("outstanding").Should().Be(0m);

        var template = (await k.Ok(k.Get("/api/hrm/letter-templates"), "templates")).Items().Single(t => t.S("code") == "ExperienceCertificate");
        var letter = await k.Ok(k.Post("/api/hrm/letters", new { employeeUuid = uuid, templateUuid = template.G("uuid") }), "letter");
        letter.S("issuedByName").Should().NotBeNullOrEmpty();
        var letterPdf = await k.DownloadAsync($"/api/hrm/letters/{letter.G("uuid")}/pdf", staffLogin);
        letterPdf.Status.Should().Be(HttpStatusCode.OK);
        var letterText = HrQa.PdfText(letterPdf.Bytes);
        letterText.Should().Contain(orgName, "the merged organization name").And.Contain("Zainab").And.NotContain("{OrganizationName}").And.NotContain("{LeaveDate}");

        // Every journal this life made balanced; the books still agree.
        var everything = await _f.AllJournalsAsync(o.OrgId);
        everything.Should().OnlyContain(j => j.Balanced, string.Join(" | ", everything));
        var tb = await k.TrialBalanceAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        tb.B("isBalanced").Should().BeTrue(tb.GetRawText());
    }

    [Fact]
    public async Task Leave_rejected_in_the_inbox_records_who_and_restores_and_a_payroll_without_punches_warns()
    {
        var o = await _root.ReadyOrgAsync("INB");
        var k = o.K;
        var bossLogin = await o.UserAsync("inbboss", "HRM_SELF_SERVICE", "HRM_LEAVE_APPROVE");
        var staffLogin = await o.UserAsync("inbstaff", "HRM_SELF_SERVICE");
        var boss = (await o.EmployeeAsync("Boss", login: bossLogin)).G("uuid");
        var staff = await o.PaidStaffAsync("Staff", 100_000m, 0m, staffLogin, manager: boss);
        var annual = (await k.Ok(k.Get("/api/hrm/leave/types"), "types")).Items().Single(t => t.S("code") == "ANNUAL");
        await k.Ok(k.Post("/api/hrm/leave/allocate", new { employeeUuid = staff, leaveTypeUuid = annual.G("uuid"), days = 5m, year = 2026 }), "allocate");

        (await k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-11-02", toDate = "2026-11-20" }, staffLogin)).Status.Should().Be(HttpStatusCode.Conflict, "LEAVE_INSUFFICIENT_BALANCE");

        var req = await k.Ok(k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-11-02", toDate = "2026-11-03" }, staffLogin), "request");
        var rej = await k.Post($"/api/workflow/approvals/{req.G("approvalUuid")}/reject", new { rejectionReason = "Busy" }, bossLogin);
        rej.Status.Should().Be(HttpStatusCode.OK, rej.ToString());
        var after = await k.Ok(k.Get($"/api/hrm/leave/requests/{req.G("uuid")}", bossLogin), "after");
        after.S("status").Should().Be("Rejected", after.GetRawText());
        after.S("decidedByName").Should().NotBeNullOrEmpty(after.GetRawText());
        (await k.Ok(k.Get("/api/hrm/leave/balances/me?year=2026", staffLogin), "bal")).Items().Single(b => b.S("leaveTypeCode") == "ANNUAL").D("balance").Should().Be(5m);

        // Cancel restores.
        var r2 = await k.Ok(k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-11-02", toDate = "2026-11-03" }, staffLogin), "request 2");
        await k.ApproveViaInboxAsync(bossLogin, r2.G("approvalUuid"));
        (await k.Ok(k.Get("/api/hrm/leave/balances/me?year=2026", staffLogin), "bal")).Items().Single(b => b.S("leaveTypeCode") == "ANNUAL").D("balance").Should().Be(3m);
        await k.Ok(k.Post($"/api/hrm/leave/requests/{r2.G("uuid")}/cancel", null, staffLogin), "cancel");
        (await k.Ok(k.Get("/api/hrm/leave/balances/me?year=2026", staffLogin), "bal")).Items().Single(b => b.S("leaveTypeCode") == "ANNUAL").D("balance").Should().Be(5m);

        var run = await o.ApprovedRunAsync();
        var fetched = await k.Ok(k.Get($"/api/hrm/payroll-runs/{run}"), "run");
        fetched.I("untrackedAttendanceEmployees").Should().BeGreaterThan(0, fetched.GetRawText());
    }

    [Fact]
    public async Task Employee_import_creates_people_with_names_and_encrypted_identity()
    {
        var o = await _root.ReadyOrgAsync("IMP");
        var k = o.K;
        var csv = "name,identification_id,gender,department_id,job_id,work_location_id,date_start,bank_name,bank_account_id\r\n" +
                  "Sara Khan,42101-7654321-5,female,OPS,CLERK,KHI,2026-01-15,HBL,99887766554433\r\n";
        await _root.LicenceAsync(o.OrgId, (Imp.Module, true));
        var session = await k.UploadOk("HRM_EMPLOYEE", System.Text.Encoding.UTF8.GetBytes(csv), "employees.csv");
        var uuid = session.G("uuid");
        await k.ValidateOk(uuid);
        await k.ExecuteOk(uuid);
        var done = await SapKit.WaitForAsync(() => k.SessionAsync(uuid), s => Imp.Finished.Contains(s.S("status")), "the import", 60);
        done.S("status").Should().Be("COMPLETED", done.GetRawText());

        var list = await k.Ok(k.Get("/api/hrm/employees?search=Sara"), "employees");
        var sara = list.A("data").Should().ContainSingle(list.GetRawText()).Subject;
        var detail = await k.Ok(k.Get($"/api/hrm/employees/{sara.G("uuid")}"), "detail");
        (detail.S("firstName"), detail.S("lastName")).Should().Be(("Sara", "Khan"));
        detail.S("cnicMasked").Should().Be("*****-*******-5");
        var stored = (await _f.QueryAsync("SELECT CreatedByName, CnicEncrypted FROM hrm.Employees WHERE Uuid = @u", ("@u", sara.G("uuid")))).Single();
        stored["CreatedByName"].Should().NotBeNull("import names its importer (D-24)");
        stored["CnicEncrypted"]!.ToString().Should().NotContain("7654321");
    }
}
