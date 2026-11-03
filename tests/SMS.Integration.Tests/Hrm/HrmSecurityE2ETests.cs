using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 QA, tasks 418 (tenant isolation), 419 (S2/S3 at rest and in answers; stored bytes inspected) and 420 (HR scope), through the real pipeline.
/// Run alone: dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmSecurityE2ETests
/// </summary>
public sealed class HrmSecurityE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Cnic = "42201-9988776-1", CnicDigits = "4220199887761";
    private const string Ntn = "7788990-4";
    private const string Account = "77665544332211", Iban = "PK36MEZN0077665544332211";

    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public HrmSecurityE2ETests(SapWebApplicationFactory f)
    {
        _f = f;
        f.ShouldBeOnLocalDb();
        _root = new SapKit(f, "A49S");
    }

    /// <summary>A person with a row behind every employee-bound route, in a full lifecycle.</summary>
    private async Task<World> WorldAsync(HrOrg o, string tag)
    {
        var k = o.K;
        var login = await o.UserAsync(tag + "w", "HRM_SELF_SERVICE");
        var boss = await o.UserAsync(tag + "b", "HRM_SELF_SERVICE", "HRM_LEAVE_APPROVE", "HRM_EXPENSE_APPROVE");
        var bossE = (await o.EmployeeAsync("Boss" + tag, login: boss)).G("uuid");
        var e = await o.PaidStaffAsync("Person" + tag, 90_000m, 0m, login, manager: bossE);

        var run = await o.ApprovedRunAsync();
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/finalize"), "finalize");
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/payslips"), "payslips");
        var line = (await o.LinesAsync(run)).Single(l => l.G("employeeUuid") == e).G("uuid");
        var slip = (await k.Ok(k.Get("/api/hrm/payslips/me?year=2026", login), "slips")).Items().Single().G("payslipUuid");

        var annual = (await k.Ok(k.Get("/api/hrm/leave/types"), "types")).Items().Single(t => t.S("code") == "ANNUAL");
        await k.Ok(k.Post("/api/hrm/leave/allocate", new { employeeUuid = e, leaveTypeUuid = annual.G("uuid"), days = 5m, year = 2026 }), "allocate");
        var leave = (await k.Ok(k.Post("/api/hrm/leave/requests", new { leaveTypeUuid = annual.G("uuid"), fromDate = "2026-11-02", toDate = "2026-11-02" }, login), "leave")).G("uuid");

        var loan = (await k.Ok(k.Post("/api/hrm/loans", new { employeeUuid = e, type = "Advance", principal = 1_000m, installmentCount = 1, startMonth = "2026-12-01" }), "loan")).G("uuid");
        var category = (await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "CAB", name = "Cab", accountRole = "DEFAULT_BONUS_EXPENSE" }), "category")).G("uuid");
        var claim = (await k.Ok(k.Post("/api/hrm/expense-claims", new { settlementMethod = "SeparatePayment", lines = new[] { new { categoryUuid = category, expenseDate = "2026-10-10", amount = 100m, taxAmount = 0m } } }, login), "claim")).G("uuid");
        var template = (await k.Ok(k.Get("/api/hrm/letter-templates"), "templates")).Items().Single(t => t.S("code") == "SalaryCertificate").G("uuid");
        var letter = (await k.Ok(k.Post("/api/hrm/letters", new { employeeUuid = e, templateUuid = template }), "letter")).G("uuid");
        var cert = (await k.Ok(k.Post("/api/hrm/tax-certificates/generate", new { employeeUuid = e, taxYear = 2027 }), "cert")).Items().Single().G("uuid");
        var increment = (await k.Ok(k.Post("/api/hrm/increments", new { employeeUuid = e, percent = 5m, effectiveDate = "2027-01-01" }), "increment")).G("uuid");
        var ot = (await k.Ok(k.Post("/api/hrm/overtime", new { employeeUuid = e, date = "2026-10-06", minutes = 60 }), "overtime")).G("uuid");
        var settlementE = await o.PaidStaffAsync("Leaver" + tag, 50_000m);
        var settlement = (await k.Ok(k.Post("/api/hrm/settlements/compute", new { employeeUuid = settlementE, lastWorkingDay = "2026-10-20" }), "settlement")).G("uuid");
        var remit = (await k.Ok(k.Post("/api/hrm/remittances", new { remittanceType = "EOBI", periodStart = "2026-10-01", periodEnd = "2026-10-31", amount = 100m, paidOn = "2026-10-31", paidFromAccount = "Bank", reference = "R" + tag, force = true }), "remit")).G("uuid");
        var vacancy = (await k.Ok(k.Post("/api/hrm/vacancies", new { title = "V", designationUuid = o.Designation, departmentUuid = o.Department, positions = 1 }), "vacancy")).G("uuid");
        var cand = (await k.Ok(k.Post("/api/hrm/candidates", new { firstName = "C", lastName = tag }), "candidate")).G("uuid");
        var app = (await k.Ok(k.Post("/api/hrm/applications", new { candidateUuid = cand, vacancyUuid = vacancy }), "app")).G("uuid");
        var offer = (await k.Ok(k.Post($"/api/hrm/applications/{app}/offers", new { offeredSalary = 1000m, proposedJoinDate = "2026-12-01" }), "offer")).G("uuid");
        var cycle = (await k.Ok(k.Post("/api/hrm/review-cycles", new { name = "C", period = "Annual", startDate = "2026-01-01", endDate = "2026-12-31" }), "cycle")).G("uuid");
        await k.Ok(k.Post($"/api/hrm/review-cycles/{cycle}/open"), "open");
        var review = (await k.Ok(k.Get("/api/hrm/reviews/me", login), "my review")).Items().Single().G("uuid");
        var course = (await k.Ok(k.Post("/api/hrm/courses", new { code = "CRS", name = "Course" }), "course")).G("uuid");
        var enrollment = (await k.Ok(k.Post("/api/hrm/enrollments", new { employeeUuid = e, courseUuid = course, enrolledDate = "2026-10-01" }), "enroll")).G("uuid");

        var attachment = await k.UploadHrmAsync("HRM_EXPENSE_CLAIM", claim, login);
        var hold = await k.Ok(k.Put($"/api/hrm/expense-claims/{claim}", new { settlementMethod = "SeparatePayment", lines = new[] { new { categoryUuid = category, expenseDate = "2026-10-10", amount = 100m, taxAmount = 0m, attachmentUuid = attachment } } }, login), "attach");
        hold.A("lines").Single().NG("attachmentUuid").Should().Be(attachment);

        return new World(e, login, boss, run, line, slip, leave, loan, claim, letter, cert, increment, ot, settlement, remit, vacancy, cand, app, offer, cycle, review, course, enrollment, attachment, category, bossE);
    }

    private sealed record World(Guid E, HttpClient Login, HttpClient Boss, Guid Run, Guid Line, Guid Slip, Guid Leave, Guid Loan, Guid Claim, Guid Letter, Guid Cert, Guid Increment, Guid Ot,
        Guid Settlement, Guid Remit, Guid Vacancy, Guid Candidate, Guid App, Guid Offer, Guid Cycle, Guid Review, Guid Course, Guid Enrollment, Guid Attachment, Guid Category, Guid Manager);

    private static IEnumerable<(HttpMethod Method, string Url, object? Body)> RoutesOf(World w)
    {
        var get = HttpMethod.Get;
        var post = HttpMethod.Post;
        var put = HttpMethod.Put;
        yield return (get, $"/api/hrm/employees/{w.E}", null);
        yield return (get, $"/api/hrm/employees/{w.E}/sensitive", null);
        yield return (get, $"/api/hrm/employees/{w.E}/documents", null);
        yield return (get, $"/api/hrm/employees/{w.E}/employment", null);
        yield return (get, $"/api/hrm/employees/{w.E}/bank-accounts", null);
        yield return (get, $"/api/hrm/employees/{w.E}/salary", null);
        yield return (get, $"/api/hrm/employees/{w.E}/salary/current", null);
        yield return (get, $"/api/hrm/employees/{w.E}/payroll-history", null);
        yield return (put, $"/api/hrm/employees/{w.E}", new { firstName = "Hack", lastName = "Ed" });
        yield return (HttpMethod.Patch, $"/api/hrm/employees/{w.E}/status", new { status = "Suspended" });
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/lines", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/journal", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/journal-export/csv", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/bank-file", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/posting-readiness", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/reconciliation", null);
        yield return (get, $"/api/hrm/payroll-runs/{w.Run}/lines/{w.Line}/payslip", null);
        yield return (post, $"/api/hrm/payroll-runs/{w.Run}/reverse", new { reason = "hack" });
        yield return (post, $"/api/hrm/payroll-runs/{w.Run}/pay", new { paidOn = "2026-10-31", reference = "hack" });
        yield return (get, $"/api/hrm/payslips/{w.Slip}/pdf", null);
        yield return (get, $"/api/hrm/leave/requests/{w.Leave}", null);
        yield return (post, $"/api/hrm/leave/requests/{w.Leave}/cancel", null);
        yield return (get, $"/api/hrm/loans/{w.Loan}", null);
        yield return (post, $"/api/hrm/loans/{w.Loan}/approve", null);
        yield return (get, $"/api/hrm/expense-claims/{w.Claim}", null);
        yield return (get, $"/api/hrm/letters/{w.Letter}/pdf", null);
        yield return (get, $"/api/hrm/tax-certificates/{w.Cert}/pdf", null);
        yield return (post, $"/api/hrm/increments/{w.Increment}/approve", null);
        yield return (post, $"/api/hrm/overtime/{w.Ot}/approve", null);
        yield return (get, $"/api/hrm/settlements/{w.Settlement}", null);
        yield return (post, $"/api/hrm/settlements/{w.Settlement}/approve", null);
        yield return (post, $"/api/hrm/remittances/{w.Remit}/retry-posting", null);
        yield return (get, $"/api/hrm/vacancies/{w.Vacancy}", null);
        yield return (get, $"/api/hrm/vacancies/{w.Vacancy}/board", null);
        yield return (get, $"/api/hrm/candidates/{w.Candidate}", null);
        yield return (get, $"/api/hrm/applications/{w.App}", null);
        yield return (get, $"/api/hrm/offers/{w.Offer}", null);
        yield return (get, $"/api/hrm/offers/{w.Offer}/letter", null);
        yield return (get, $"/api/hrm/review-cycles/{w.Cycle}", null);
        yield return (get, $"/api/hrm/reviews/{w.Review}", null);
        yield return (get, $"/api/hrm/courses/{w.Course}", null);
        yield return (post, $"/api/hrm/enrollments/{w.Enrollment}/cancel", null);
        yield return (get, $"/api/hrm/attachments/{w.Attachment}/content", null);
        yield return (get, $"/api/hrm/attachments?interfaceCode=HRM_EXPENSE_CLAIM&rowUuid={w.Claim}", null);
        yield return (HttpMethod.Delete, $"/api/hrm/attachments/{w.Attachment}", null);
    }

    [Fact]
    public async Task T418_every_route_of_org_A_answers_404_to_org_B_and_nothing_moves_and_lists_are_empty()
    {
        var a = await _root.ReadyOrgAsync("ISOA");
        var w = await WorldAsync(a, "A");
        var b = await _root.ReadyOrgAsync("ISOB");
        var kb = b.K;

        var wrong = new List<string>();
        foreach (var (method, url, body) in RoutesOf(w))
        {
            var api = await kb.Send(method, url, body);
            // List of attachments for a row of another org answers an empty list or 404; anything holding data is a leak.
            if (url.StartsWith("/api/hrm/attachments?") && api.Status == HttpStatusCode.OK && api.Result.GetArrayLength() == 0) continue;
            if (api.Status != HttpStatusCode.NotFound) wrong.Add($"{method} {url} -> {api}");
        }
        wrong.Should().BeEmpty(string.Join("\n", wrong));

        // Cross-org uuids in bodies.
        (await kb.Post("/api/hrm/loans", new { employeeUuid = w.E, type = "Personal", principal = 100m, installmentCount = 1, startMonth = "2026-12-01" })).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.Post("/api/hrm/settlements/compute", new { employeeUuid = w.E, lastWorkingDay = "2026-10-20" })).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.Post("/api/hrm/attendance/events", new { employeeUuid = w.E, eventTimeUtc = "2026-10-01T04:00:00Z", eventType = "IN" })).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.Post("/api/hrm/employees", new { firstName = "X", lastName = "Y", employment = new { effectiveFrom = "2026-01-01", departmentUuid = a.Department, designationUuid = b.Designation, branchUuid = b.Branch } })).Status.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);

        // Lists show none of A's rows (B has its own org structure and no people).
        foreach (var url in new[] { "/api/hrm/employees", "/api/hrm/payroll-runs", "/api/hrm/loans", "/api/hrm/expense-claims", "/api/hrm/leave/requests", "/api/hrm/settlements", "/api/hrm/remittances", "/api/hrm/vacancies", "/api/hrm/candidates", "/api/hrm/letters", "/api/hrm/tax-certificates?taxYear=2027", "/api/hrm/increments", "/api/hrm/courses" })
        {
            var r = await kb.Get(url);
            r.Status.Should().Be(HttpStatusCode.OK, r.ToString());
            r.Raw.Should().NotContain(w.E.ToString()).And.NotContain("PersonA").And.NotContain(w.Run.ToString());
        }

        // Same CNIC in both organizations is allowed and its hash does not correlate (D-15).
        await a.K.Ok(a.K.Post("/api/hrm/employees", new { firstName = "Same", lastName = "Person", cnic = Cnic, employment = a.Hire() }), "A");
        await b.K.Ok(b.K.Post("/api/hrm/employees", new { firstName = "Same", lastName = "Person", cnic = Cnic, employment = b.Hire() }), "B");
        var hashes = await _f.QueryAsync("SELECT CnicHash, CnicEncrypted FROM hrm.Employees WHERE FirstName = 'Same' AND LastName = 'Person' AND OrganizationId IN (@a, @b)", ("@a", a.OrgId), ("@b", b.OrgId));
        hashes.Should().HaveCount(2);
        hashes[0]["CnicHash"].Should().NotBe(hashes[1]["CnicHash"]);
        hashes[0]["CnicEncrypted"].Should().NotBe(hashes[1]["CnicEncrypted"]);

        // A did not move.
        (await a.K.Ok(a.K.Get($"/api/hrm/employees/{w.E}"), "A's person")).S("status").Should().Be("Active");
        (await a.K.Ok(a.K.Get($"/api/hrm/payroll-runs/{w.Run}"), "A's run")).S("status").Should().Be("Posted");
        (await a.K.Ok(a.K.Get($"/api/hrm/loans/{w.Loan}"), "A's loan")).S("status").Should().Be("Draft");

        // Sent under api/attachments, the kinds answer nothing to anyone.
        (await kb.Get($"/api/attachments/{w.Attachment}/content")).Status.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await a.K.Get($"/api/attachments/{w.Attachment}/content")).Status.Should().Be(HttpStatusCode.Forbidden, "D-25");
    }

    [Fact]
    public async Task T418_a_super_admin_reads_no_payroll_identity_or_bank_data_of_an_organization()
    {
        var a = await _root.ReadyOrgAsync("ISOS");
        var k = a.K;
        var e = await o_EmployeeWithSecretsAsync(a);
        await a.BankAsync(e, Account, Iban);
        await a.SalaryAsync(e, 80_000m);
        var run = await a.ApprovedRunAsync();

        foreach (var url in new[]
                 {
                     $"/api/hrm/employees/{e}", $"/api/hrm/employees/{e}/sensitive", $"/api/hrm/employees/{e}/bank-accounts", $"/api/hrm/employees/{e}/salary",
                     $"/api/hrm/payroll-runs/{run}", $"/api/hrm/payroll-runs/{run}/lines", $"/api/hrm/payroll-runs/{run}/bank-file"
                 })
        {
            var api = await _root.Get(url);
            api.Status.Should().NotBe(HttpStatusCode.OK, $"{url} — {api}");
            api.Raw.Should().NotContain(CnicDigits).And.NotContain(Account);
        }
    }

    private static async Task<Guid> o_EmployeeWithSecretsAsync(HrOrg o) =>
        (await o.K.Ok(o.K.Post("/api/hrm/employees", new { firstName = "Secret", lastName = "Keeper", cnic = Cnic, ntn = Ntn, employment = o.Hire() }), "employee")).G("uuid");

    [Fact]
    public async Task T419_S3_never_in_clear_in_any_column_of_the_database_masked_everywhere_revealed_only_by_code_and_logged()
    {
        var o = await _root.ReadyOrgAsync("S3");
        var k = o.K;
        var e = await o_EmployeeWithSecretsAsync(o);
        await o.BankAsync(e, Account, Iban);
        await o.SalaryAsync(e, 80_000m);
        var run = await o.ApprovedRunAsync();
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/finalize"), "finalize");
        await k.DownloadAsync($"/api/hrm/payroll-runs/{run}/bank-file");

        // Every text column of every table of the database, in every schema, cast as text; varbinary as well.
        var cols = await _f.QueryAsync(
            "SELECT s.name AS S, t.name AS T, c.name AS C, ty.name AS Ty FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE ty.name IN ('nvarchar','varchar','nchar','char','ntext','text','varbinary')");
        cols.Should().HaveCountGreaterThan(500);
        var leaks = new List<string>();
        foreach (var c in cols)
        {
            var col = $"[{c["S"]}].[{c["T"]}].[{c["C"]}]";
            var expr = (string)c["Ty"]! == "varbinary" ? $"CONVERT(varchar(max), [{c["C"]}])" : $"CAST([{c["C"]}] AS nvarchar(max))";
            var hits = await _f.QueryAsync(
                $"SELECT COUNT(*) AS N FROM [{c["S"]}].[{c["T"]}] WHERE {expr} LIKE '%' + @a + '%' OR {expr} LIKE '%' + @b + '%' OR {expr} LIKE '%' + @c + '%' OR {expr} LIKE '%' + @d + '%' OR {expr} LIKE '%' + @e + '%'",
                ("@a", CnicDigits), ("@b", Cnic), ("@c", Ntn), ("@d", Account), ("@e", Iban));
            if (Convert.ToInt32(hits[0]["N"]) > 0) leaks.Add(col);
        }
        leaks.Should().BeEmpty("S3 in the clear in: " + string.Join(", ", leaks));

        // The stored value is AES-GCM ciphertext that decrypts: not a hash, not base64 of the clear text.
        var stored = (await _f.QueryAsync("SELECT CnicEncrypted, CnicHash, CnicLastDigit, NtnEncrypted, NtnLast4 FROM hrm.Employees WHERE Uuid = @u", ("@u", e))).Single();
        stored["CnicEncrypted"]!.ToString()!.Length.Should().BeGreaterThan(CnicDigits.Length);
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CnicDigits)).Should().NotBe(stored["CnicEncrypted"]!.ToString());
        stored["CnicHash"]!.ToString()!.Length.Should().Be(64);
        stored["CnicLastDigit"].Should().Be("1");
        var bank = (await _f.QueryAsync("SELECT AccountNumberEncrypted, IbanEncrypted, AccountNumberLast4 FROM hrm.EmployeeBankAccounts WHERE OrganizationId = @o", ("@o", o.OrgId))).Single();
        bank["AccountNumberEncrypted"]!.ToString().Should().NotContain(Account);
        bank["AccountNumberLast4"].Should().Be("2211");

        // Masked: list, detail, bank list. Exact search needs the code.
        var noCode = await o.UserAsync("s3v", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        var bankList = await o.UserAsync("s3b", "HRM_BANK_EDIT", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        var bankView = await o.UserAsync("s3bv", "HRM_BANK_VIEW", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        var revealer = await o.UserAsync("s3r", "HRM_EMPLOYEE_SENSITIVE_VIEW", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        foreach (var url in new[] { "/api/hrm/employees", $"/api/hrm/employees/{e}" })
        {
            var api = await k.Get(url, noCode);
            api.Status.Should().Be(HttpStatusCode.OK);
            api.Raw.Should().NotContain(CnicDigits).And.NotContain(Cnic).And.NotContain(Ntn).And.Contain("*****-*******-1");
        }
        (await k.Get("/api/hrm/employees?cnic=" + Cnic, noCode)).ShouldBeForbidden("exact CNIC search needs SENSITIVE_VIEW");
        (await k.Ok(k.Get("/api/hrm/employees?cnic=" + CnicDigits, revealer), "search")).A("data").Should().ContainSingle();
        (await k.Get($"/api/hrm/employees/{e}/sensitive", noCode)).ShouldBeForbidden("no code");
        (await k.Get($"/api/hrm/employees/{e}/bank-accounts", noCode)).ShouldBeForbidden("no code");

        var masked = await k.Get($"/api/hrm/employees/{e}/bank-accounts", bankList);
        masked.Status.Should().Be(HttpStatusCode.OK);
        masked.Raw.Should().NotContain(Account).And.NotContain(Iban).And.Contain("2211");
        var accountUuid = masked.Result.Items().Single().G("uuid");
        (await k.Get($"/api/hrm/employees/{e}/bank-accounts/{accountUuid}/sensitive", bankList)).ShouldBeForbidden("edit does not reveal");

        var before = await LogCountAsync(o.OrgId);
        var reveal = await k.Ok(k.Get($"/api/hrm/employees/{e}/sensitive", revealer), "reveal");
        (reveal.S("cnic"), reveal.S("ntn")).Should().Be((Cnic, Ntn));
        (await LogCountAsync(o.OrgId)).Should().Be(before + 2, "one log row per value");
        var bankReveal = await k.Ok(k.Get($"/api/hrm/employees/{e}/bank-accounts/{accountUuid}/sensitive", bankView), "reveal bank");
        (bankReveal.S("accountNumber"), bankReveal.S("iban")).Should().Be((Account, Iban));
        (await LogCountAsync(o.OrgId)).Should().BeGreaterThan(before + 2);

        // An update with null keeps; "" clears.
        await k.Ok(k.Put($"/api/hrm/employees/{e}", new { firstName = "Secret", lastName = "Keeper", ntn = "", emergencyContacts = Array.Empty<object>() }), "clear ntn");
        var kept = (await _f.QueryAsync("SELECT CnicEncrypted, NtnEncrypted, NtnHash FROM hrm.Employees WHERE Uuid = @u", ("@u", e))).Single();
        kept["CnicEncrypted"].Should().Be(stored["CnicEncrypted"]);
        (kept["NtnEncrypted"], kept["NtnHash"]).Should().Be(((object?)null, (object?)null));
    }

    private async Task<int> LogCountAsync(Guid org) =>
        Convert.ToInt32((await _f.QueryAsync("SELECT COUNT(*) AS N FROM hrm.SensitiveAccessLogs WHERE OrganizationId = @o", ("@o", org)))[0]["N"]);

    [Fact]
    public async Task T419_S2_salary_and_bands_are_masked_without_the_code()
    {
        var o = await _root.ReadyOrgAsync("S2");
        var k = o.K;
        var grade = await k.Ok(k.Post("/api/hrm/grades", new { code = "G1", name = "Grade 1", minSalary = 50_000m, maxSalary = 150_000m }), "grade");
        var e = await o.PaidStaffAsync("Saima", 80_000m);
        var viewer = await o.UserAsync("s2v", "HRM_ORG_VIEW", "HRM_EMPLOYEE_VIEW", "HRM_SCOPE_ALL");
        var g = (await k.Ok(k.Get($"/api/hrm/grades"), "grades")).Items().Single();
        g.D("minSalary").Should().Be(50_000m);
        var masked = (await k.Ok(k.Get($"/api/hrm/grades", viewer), "grades masked")).Items().Single();
        (masked.IsNull("minSalary"), masked.IsNull("maxSalary"), masked.B("salaryMasked")).Should().Be((true, true, true), masked.GetRawText());
        (await k.Get($"/api/hrm/employees/{e}/salary", viewer)).ShouldBeForbidden("no salary code");
        var editor = await o.UserAsync("s2e", "HRM_ORG_MANAGE", "HRM_ORG_VIEW");
        await k.Ok(k.Put($"/api/hrm/grades/{grade.G("uuid")}", new { code = "G1", name = "Renamed" }, editor), "masked editor saves");
        (await k.Ok(k.Get($"/api/hrm/grades"), "grades")).Items().Single().D("maxSalary").Should().Be(150_000m, "D-23: a masked editor's save keeps the band");
    }

    [Fact]
    public async Task T420_department_branch_team_self_scope_on_every_employee_bound_route()
    {
        var o = await _root.ReadyOrgAsync("SCO");
        var k = o.K;

        // Org: department X (child XC) on branch A; department Z on branch B.
        var branchB = (await k.Ok(k.Post("/api/hrm/branches", new { code = "lhr", name = "Lahore" }), "branch")).G("uuid");
        var deptX = o.Department;
        var deptXc = (await k.Ok(k.Post("/api/hrm/departments", new { code = "XC", name = "Child", parentUuid = deptX }), "child")).G("uuid");
        var deptZ = (await k.Ok(k.Post("/api/hrm/departments", new { code = "Z", name = "Zed", branchUuid = branchB }), "z")).G("uuid");

        var codes = new[]
        {
            "HRM_EMPLOYEE_VIEW", "HRM_EMPLOYEE_EDIT", "HRM_EMPLOYEE_SENSITIVE_VIEW", "HRM_EMPLOYMENT_MANAGE", "HRM_BANK_VIEW", "HRM_SALARY_VIEW", "HRM_PAYROLL_VIEW", "HRM_LEAVE_VIEW", "HRM_LEAVE_MANAGE",
            "HRM_ATTENDANCE_VIEW", "HRM_ATTENDANCE_MANAGE", "HRM_OVERTIME_APPROVE", "HRM_LOAN_VIEW", "HRM_LOAN_MANAGE", "HRM_SETTLEMENT_VIEW", "HRM_SETTLEMENT_MANAGE", "HRM_EXPENSE_VIEW", "HRM_LETTER_ISSUE", "HRM_TAX_CERT_VIEW",
            "HRM_INCREMENT_MANAGE", "HRM_PERFORMANCE_VIEW", "HRM_TRAINING_VIEW"
        };
        HttpClient U(HttpClient c) => c;
        var deptUser = await o.UserAsync("sd", [.. codes, "HRM_SCOPE_DEPARTMENT", "HRM_SELF_SERVICE"]);
        var branchUser = await o.UserAsync("sb", [.. codes, "HRM_SCOPE_BRANCH", "HRM_SELF_SERVICE"]);
        var teamUser = await o.UserAsync("st", [.. codes, "HRM_SELF_SERVICE"]);
        var selfUser = await o.UserAsync("ss", [.. codes, "HRM_SELF_SERVICE"]);
        var grantUser = await o.UserAsync("sg", [.. codes, "HRM_SELF_SERVICE"]);

        var dUser = (await o.EmployeeAsync("DeptUser", login: deptUser, department: deptX)).G("uuid");
        var bUser = (await o.EmployeeAsync("BranchUser", login: branchUser, department: deptX)).G("uuid");
        var tUser = (await o.EmployeeAsync("TeamUser", login: teamUser, department: deptZ, branch: branchB)).G("uuid");
        var sUser = (await o.EmployeeAsync("SelfUser", login: selfUser, department: deptZ, branch: branchB)).G("uuid");
        var gUser = (await o.EmployeeAsync("GrantUser", login: grantUser, department: deptZ, branch: branchB)).G("uuid");
        await k.Ok(k.Post("/api/hrm/user-scopes", new { userId = ServiceKit.AdminUserId(grantUser), departmentUuid = deptXc }), "grant");

        var inX = await o.PaidStaffAsync("InX", 50_000m, department: deptX);
        var inXc = await o.PaidStaffAsync("InXc", 50_000m, department: deptXc);
        var eZ = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "InZ", lastName = "T", employment = o.Hire(manager: tUser, department: deptZ, branch: branchB) }), "z person");
        var inZ = eZ.G("uuid");
        await o.BankAsync(inZ);
        await o.SalaryAsync(inZ, 50_000m);
        var indirect = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "UnderZ", lastName = "T", employment = o.Hire(manager: inZ, department: deptZ, branch: branchB) }), "indirect");

        var visible = new Dictionary<string, (HttpClient C, Guid[] In, Guid[] Out)>
        {
            ["department"] = (deptUser, [dUser, inX, inXc], [inZ, tUser, indirect.G("uuid")]),
            ["branch"] = (branchUser, [bUser, inX, inXc, dUser], [inZ, tUser]),
            ["team"] = (teamUser, [tUser, inZ, indirect.G("uuid")], [inX, inXc, sUser]),
            ["self"] = (selfUser, [sUser], [inX, inZ, tUser]),
            ["granted"] = (grantUser, [gUser, inXc], [inX, inZ]),
        };

        var wrong = new List<string>();
        foreach (var (who, (client, inside, outside)) in visible)
        {
            foreach (var e in inside)
            {
                var api = await k.Get($"/api/hrm/employees/{e}", client);
                if (api.Status != HttpStatusCode.OK) wrong.Add($"{who}: should see {e}: {api}");
            }
            foreach (var e in outside)
            {
                foreach (var url in new[]
                         {
                             $"/api/hrm/employees/{e}", $"/api/hrm/employees/{e}/sensitive", $"/api/hrm/employees/{e}/employment", $"/api/hrm/employees/{e}/employment/current",
                             $"/api/hrm/employees/{e}/bank-accounts", $"/api/hrm/employees/{e}/salary", $"/api/hrm/employees/{e}/salary/current", $"/api/hrm/employees/{e}/payroll-history",
                             $"/api/hrm/employees/{e}/documents", $"/api/hrm/leave/balances?employeeUuid={e}&year=2026", $"/api/hrm/leave/ledger?employeeUuid={e}&year=2026",
                             $"/api/hrm/attendance/summary?employeeUuid={e}&year=2026&month=10",
                         })
                {
                    var api = await k.Get(url, client);
                    if (api.Status != HttpStatusCode.NotFound) wrong.Add($"{who} {url}: {api}");
                }
                foreach (var (url, body) in new (string, object)[]
                         {
                             ($"/api/hrm/loans", new { employeeUuid = e, type = "Personal", principal = 100m, installmentCount = 1, startMonth = "2026-12-01" }),
                             ($"/api/hrm/settlements/compute", new { employeeUuid = e, lastWorkingDay = "2026-10-20" }),
                             ($"/api/hrm/increments", new { employeeUuid = e, percent = 5m, effectiveDate = "2027-01-01" }),
                             ($"/api/hrm/attendance/events", new { employeeUuid = e, eventTimeUtc = "2026-10-01T04:00:00Z", eventType = "IN" }),
                             ($"/api/hrm/overtime", new { employeeUuid = e, date = "2026-10-06", minutes = 60 }),
                             ($"/api/hrm/letters", new { employeeUuid = e, templateUuid = Guid.NewGuid() }),
                             ($"/api/hrm/tax-certificates/generate", new { employeeUuid = e, taxYear = 2027 }),
                         })
                {
                    var api = await k.Post(url, body, client);
                    if (api.Status != HttpStatusCode.NotFound) wrong.Add($"{who} POST {url} for outside {e}: {api}");
                }
                var patch = await k.Patch($"/api/hrm/employees/{e}/status", new { status = "Suspended" }, client);
                if (patch.Status != HttpStatusCode.NotFound) wrong.Add($"{who} status of {e}: {patch}");
            }
        }

        // Lists are cut to the scope.
        var deptList = await k.Ok(k.Get("/api/hrm/employees?pageSize=200", deptUser), "list");
        deptList.A("data").Select(r => r.G("uuid")).Should().Contain(inXc).And.NotContain(inZ);
        var selfList = await k.Ok(k.Get("/api/hrm/employees?pageSize=200", selfUser), "list");
        selfList.A("data").Select(r => r.G("uuid")).Should().BeEquivalentTo([sUser]);

        // Payroll lines: the run belongs to the organization, but a scoped viewer must not read outsiders' salaries through it.
        var run = await o.ApprovedRunAsync();
        var linesApi = await k.Get($"/api/hrm/payroll-runs/{run}/lines?pageSize=200", selfUser);
        if (linesApi.Status == HttpStatusCode.OK && linesApi.Raw.Contains(inX.ToString())) wrong.Add($"self scope reads another person's payroll line: {linesApi}");
        var lineX = (await o.LinesAsync(run)).Single(l => l.G("employeeUuid") == inX).G("uuid");
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/finalize"), "finalize");
        var slip = await k.DownloadAsync($"/api/hrm/payroll-runs/{run}/lines/{lineX}/payslip", selfUser);
        if (slip.Status != HttpStatusCode.NotFound) wrong.Add($"self scope opens another person's payslip by line: {slip.Status}");
        var bankFile = await k.DownloadAsync($"/api/hrm/payroll-runs/{run}/bank-file", await o.UserAsync("sf", "HRM_PAYROLL_PAY", "HRM_SCOPE_DEPARTMENT"));
        if (bankFile.Status != HttpStatusCode.Forbidden) wrong.Add($"bank file without SCOPE_ALL: {bankFile.Status}");

        wrong.Should().BeEmpty(string.Join("\n", wrong));
        _ = U(selfUser);
    }

    [Fact]
    public async Task Seeded_roles_hold_what_D18_says_through_the_real_permission_tables()
    {
        var has = async (string role, string code) => Convert.ToInt32((await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM auth.RolePermissions rp JOIN auth.Roles r ON r.RoleID = rp.RoleID JOIN auth.Permissions p ON p.PermissionID = rp.PermissionID WHERE r.RoleCode = @r AND p.Code = @c",
            ("@r", role), ("@c", code)))[0]["N"]) > 0;
        (await has("HR_MANAGER", "HRM_SALARY_APPROVE")).Should().BeTrue();
        (await has("HR_OFFICER", "HRM_SALARY_VIEW")).Should().BeFalse();
        (await has("HR_OFFICER", "HRM_BANK_VIEW")).Should().BeFalse();
        (await has("HR_OFFICER", "HRM_SCOPE_ALL")).Should().BeTrue();
        (await has("SYSTEM_ADMIN", "HRM_PAYROLL_VIEW")).Should().BeFalse();
        (await has("SYSTEM_ADMIN", "HRM_EMPLOYEE_SENSITIVE_VIEW")).Should().BeFalse();
        (await has("SYSTEM_ADMIN", "HRM_SETTINGS_MANAGE")).Should().BeTrue();
    }
}

internal static class HrUpload
{
    /// <summary>A small upload through api/attachments, as the screens do; returns its uuid.</summary>
    public static async Task<Guid> UploadHrmAsync(this SapKit k, string interfaceCode, Guid documentId, HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent("%PDF-1.4 qa"u8.ToArray());
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(bytes, "file", "qa.pdf");
        form.Add(new StringContent(interfaceCode), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        using var resp = await client.PostAsync("/api/attachments", form);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue(raw);
        var result = JsonDocument.Parse(raw).RootElement.GetProperty("result");
        return result.ValueKind == JsonValueKind.Object ? result.GetProperty("uuid").GetGuid() : result.GetGuid();
    }
}
