using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.ServiceOrders;

namespace SMS.Integration.Tests.Hrm;

/// <summary>A49 QA: one organization with HRM on and its org structure.</summary>
internal sealed record HrOrg(FpOrg Fp, Guid Branch, Guid Department, Guid Designation, Guid CostCenter)
{
    public SapKit K => Fp.K;
    public Guid OrgId => Fp.OrgId;
    public SapWebApplicationFactory F => Fp.F;
    public Dictionary<string, Guid> Components { get; } = [];
    public Guid Structure { get; set; }
}

internal sealed record Staff(Guid Uuid, string Number, HttpClient? Login, int? UserId);

/// <summary>
/// A49 QA: everything the QA E2E classes share, done through the routes a person would use, as real users through the real authorization
/// pipeline. Nothing here touches anything but the factory's throwaway LocalDB database.
/// </summary>
internal static class HrQa
{
    public static readonly string[] AllFeatures =
    [
        "FEATURE_HRM_ATTENDANCE", "FEATURE_HRM_LEAVE", "FEATURE_HRM_PAYROLL", "FEATURE_HRM_RECRUITMENT", "FEATURE_HRM_PERFORMANCE",
        "FEATURE_HRM_TRAINING", "FEATURE_HRM_LOANS", "FEATURE_HRM_EXPENSES"
    ];

    public static readonly DateOnly PeriodStart = new(2026, 10, 1), PeriodEnd = new(2026, 10, 31);

    /// <summary>HRM on with every sub-feature; a branch, an OPS cost center + department, a designation; PAYROLL is the payroll cost center.</summary>
    public static async Task<HrOrg> ReadyOrgAsync(this SapKit root, string prefix, bool gl = true, bool mapRoles = true, bool verifyStatutory = true)
    {
        var fp = await root.NewOrgAsync(prefix);
        return await fp.ReadyAsync(root, gl, mapRoles, verifyStatutory);
    }

    public static async Task<HrOrg> ReadyAsync(this FpOrg fp, SapKit root, bool gl = true, bool mapRoles = true, bool verifyStatutory = true)
    {
        var k = fp.K;
        await root.EnableHrmAsync(fp.O, AllFeatures);
        var branch = await k.Ok(k.Post("/api/hrm/branches", new { code = "khi", name = "Karachi" }), "branch");
        var center = await k.Ok(k.Post("/api/hrm/cost-centers", new { code = "OPS", name = "Operations" }), "cost center");
        var dept = await k.Ok(k.Post("/api/hrm/departments", new { code = "OPS", name = "Operations", costCenterUuid = center.G("uuid"), branchUuid = branch.G("uuid") }), "department");
        var desig = await k.Ok(k.Post("/api/hrm/designations", new { code = "CLERK", name = "Clerk", level = 1 }), "designation");
        var o = new HrOrg(fp, branch.G("uuid"), dept.G("uuid"), desig.G("uuid"), center.G("uuid"));

        if (gl)
        {
            await k.EnableGlAsync();
            if (mapRoles) await o.MapHrRolesAsync();
        }

        await o.TaxTableForThisYearAsync();
        if (verifyStatutory) await o.VerifyStatutoryAsync();
        await o.LoadStructureAsync();
        // D-9: with no line manager the approver is the first OTHER holder of the permission, so a one-admin organization has none (HRM_NO_APPROVER, by design).
        await o.UserAsync("hrapprover", "HRM_SALARY_APPROVE", "HRM_LEAVE_APPROVE", "HRM_EXPENSE_APPROVE", "HRM_SCOPE_ALL");
        return o;
    }

    public static async Task LoadStructureAsync(this HrOrg o)
    {
        var k = o.K;
        var structures = (await k.Ok(k.Get("/api/hrm/salary-structures"), "structures")).Items();
        var std = structures.Single(s => s.S("code") == "STD_MONTHLY");
        o.Structure = std.G("uuid");
        foreach (var l in std.A("lines")) o.Components[l.S("componentCode")!] = l.G("componentUuid");
    }

    /// <summary>
    /// The seeded table ends 2026-06-30. A run for today's period needs a newer one, which is what a real organization has to enter by hand.
    /// Same slabs as the seed; values are test data and assert nothing about real tax.
    /// </summary>
    public static async Task TaxTableForThisYearAsync(this HrOrg o) =>
        await o.K.Ok(o.K.Post("/api/hrm/tax-tables", new
        {
            code = "QA-2027", name = "QA tax table", countryCode = "PK", effectiveFrom = "2026-07-01", effectiveTo = "2027-06-30",
            slabs = new object[]
            {
                new { fromAmount = 0m, toAmount = 600_000m, fixedAmount = 0m, ratePercent = 0m },
                new { fromAmount = 600_000m, toAmount = 1_200_000m, fixedAmount = 0m, ratePercent = 1m },
                new { fromAmount = 1_200_000m, toAmount = 2_200_000m, fixedAmount = 6_000m, ratePercent = 11m },
                new { fromAmount = 2_200_000m, toAmount = (decimal?)null, fixedAmount = 116_000m, ratePercent = 23m },
            }
        }), "tax table");

    public static async Task VerifyStatutoryAsync(this HrOrg o)
    {
        var k = o.K;
        foreach (var t in (await k.Ok(k.Get("/api/hrm/tax-tables"), "tables")).Items().Where(t => !t.B("isVerified")))
            await k.Ok(k.Post($"/api/hrm/tax-tables/{t.G("uuid")}/verify", new { note = "QA" }), "verify table");
        foreach (var r in (await k.Ok(k.Get("/api/hrm/statutory-rates"), "rates")).Items().Where(t => !t.B("isVerified")))
            await k.Ok(k.Post($"/api/hrm/statutory-rates/{r.G("uuid")}/verify", new { note = "QA" }), "verify rate");
    }

    /// <summary>The account codes HR roles land on; every role has its own leaf, so a journal line proves which role it came from.</summary>
    public static readonly (string Role, string Code, string Type)[] HrAccounts =
    [
        ("DEFAULT_SALARY_EXPENSE", "5901", "Expense"), ("DEFAULT_OVERTIME_EXPENSE", "5902", "Expense"), ("DEFAULT_BONUS_EXPENSE", "5903", "Expense"),
        ("DEFAULT_EMPLOYER_CONTRIB_EXPENSE", "5904", "Expense"), ("DEFAULT_GRATUITY_EXPENSE", "5905", "Expense"), ("DEFAULT_LEAVE_ENCASHMENT_EXPENSE", "5906", "Expense"),
        ("DEFAULT_SALARY_PAYABLE", "2901", "OtherCurrentLiability"), ("DEFAULT_EMPLOYEE_TAX_PAYABLE", "2902", "OtherCurrentLiability"),
        ("DEFAULT_EOBI_PAYABLE", "2903", "OtherCurrentLiability"), ("DEFAULT_PF_PAYABLE", "2904", "OtherCurrentLiability"),
        ("DEFAULT_SOCIAL_SECURITY_PAYABLE", "2905", "OtherCurrentLiability"), ("DEFAULT_OTHER_DEDUCTION_PAYABLE", "2906", "OtherCurrentLiability"),
        ("DEFAULT_EXPENSE_CLAIM_PAYABLE", "2907", "OtherCurrentLiability"), ("DEFAULT_GRATUITY_PROVISION", "2908", "OtherCurrentLiability"),
        ("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE", "1901", "OtherCurrentAsset"), ("DEFAULT_EMPLOYEE_ADVANCE", "1902", "OtherCurrentAsset"),
    ];

    public static string CodeOf(string role) => HrAccounts.Single(a => a.Role == role).Code;

    public static async Task MapHrRolesAsync(this HrOrg o, params string[] except)
    {
        var k = o.K;
        var flat = (await k.Ok(k.Get(Gl.Chart + "/flat"), "chart")).Items();
        foreach (var (role, code, type) in HrAccounts)
        {
            if (except.Contains(role)) continue;
            // A leaf of this root type shows the level 3 group the new leaf can sit under.
            var root = type == "Expense" ? "Expense" : type == "OtherCurrentLiability" ? "Liability" : "Asset";
            var sibling = flat.First(a => a.S("rootType") == root && !a.B("isGroup") && !a.IsNull("parentUuid"));
            var created = await k.Ok(k.CreateAccount(sibling.G("parentUuid"), code, role.Replace("DEFAULT_", "HR "), type), $"account for {role}");
            await k.Ok(k.SetDefault(role, created.G("uuid")), $"assign {role}");
        }
    }

    /// <summary>A real login holding exactly these permissions.</summary>
    public static Task<HttpClient> UserAsync(this HrOrg o, string tag, params string[] codes) => o.K.LoginWithPermissionsAsync(tag, codes);

    public static object Hire(this HrOrg o, string from = "2026-01-01", Guid? manager = null, Guid? department = null, Guid? branch = null) => new
    {
        effectiveFrom = from, departmentUuid = department ?? o.Department, designationUuid = o.Designation, branchUuid = branch ?? o.Branch, managerUuid = manager
    };

    public static async Task<JsonElement> EmployeeAsync(this HrOrg o, string first, string last = "Tester", HttpClient? login = null, string from = "2026-01-01", Guid? manager = null,
        string? cnic = null, Guid? department = null, Guid? branch = null, string? gender = null) =>
        await o.K.Ok(o.K.Post("/api/hrm/employees", new
        {
            firstName = first, lastName = last, cnic, gender, joinDate = from, userId = login is null ? (int?)null : ServiceKit.AdminUserId(login),
            employment = o.Hire(from, manager, department, branch)
        }), $"employee {first}");

    public static async Task BankAsync(this HrOrg o, Guid employee, string number = "00112233445566", string iban = "PK36SCBL0000001123456702") =>
        await o.K.Ok(o.K.Post($"/api/hrm/employees/{employee}/bank-accounts", new { bankName = "HBL", accountNumber = number, iban, paymentMethod = "Bank" }), "bank account");

    /// <summary>Draft, submit, approve, by the org admin through the real routes.</summary>
    public static async Task<JsonElement> SalaryAsync(this HrOrg o, Guid employee, decimal basic, decimal rent = 0m, string from = "2026-01-01", HttpClient? approver = null)
    {
        var k = o.K;
        var lines = new List<object> { new { componentUuid = o.Components["BASIC"], amount = basic } };
        if (rent > 0) lines.Add(new { componentUuid = o.Components["HRENT"], amount = rent });
        var draft = await k.Ok(k.Post($"/api/hrm/employees/{employee}/salary", new { structureUuid = o.Structure, effectiveFrom = from, lines }), "salary draft");
        await k.Ok(k.Post($"/api/hrm/salary-assignments/{draft.G("uuid")}/submit"), "salary submit");
        return await k.Ok(k.Post($"/api/hrm/salary-assignments/{draft.G("uuid")}/approve", null, approver), "salary approve");
    }

    public static async Task<Guid> PaidStaffAsync(this HrOrg o, string first, decimal basic, decimal rent = 0m, HttpClient? login = null, string from = "2026-01-01", Guid? manager = null, Guid? department = null)
    {
        var e = await o.EmployeeAsync(first, login: login, from: from, manager: manager, department: department);
        await o.BankAsync(e.G("uuid"));
        await o.SalaryAsync(e.G("uuid"), basic, rent, from);
        return e.G("uuid");
    }

    public static async Task<JsonElement> NewRunAsync(this HrOrg o, DateOnly? start = null, DateOnly? end = null, DateOnly? payDate = null) =>
        await o.K.Ok(o.K.Post("/api/hrm/payroll-runs", new
        {
            periodStart = (start ?? PeriodStart).ToString("yyyy-MM-dd"), periodEnd = (end ?? PeriodEnd).ToString("yyyy-MM-dd"),
            payDate = (payDate ?? (end ?? PeriodEnd)).ToString("yyyy-MM-dd")
        }), "create run");

    /// <summary>Create, calculate, review, approve.</summary>
    public static async Task<Guid> ApprovedRunAsync(this HrOrg o, DateOnly? start = null, DateOnly? end = null, DateOnly? payDate = null)
    {
        var k = o.K;
        var run = (await o.NewRunAsync(start, end, payDate)).G("uuid");
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/calculate"), "calculate");
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/review"), "review");
        await k.Ok(k.Post($"/api/hrm/payroll-runs/{run}/approve", new { excludeBlocked = true }), "approve"); // a manager with no salary is a blocked line by design
        return run;
    }

    public static async Task<List<JsonElement>> LinesAsync(this HrOrg o, Guid run, HttpClient? c = null) =>
        (await o.K.Ok(o.K.Get($"/api/hrm/payroll-runs/{run}/lines?pageSize=200", c), "lines")).A("data");

    public static decimal Amount(this JsonElement line, string code) =>
        line.A("components").Where(c => c.S("code") == code).Sum(c => c.D("amount"));

    /// <summary>The raw bytes of a download; (status, bytes).</summary>
    public static async Task<(HttpStatusCode Status, byte[] Bytes, string? ContentType)> DownloadAsync(this SapKit k, string url, HttpClient? c = null)
    {
        using var resp = await (c ?? k.Admin).GetAsync(url);
        return (resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType);
    }

    public static string PdfText(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    /// <summary>The organization's registered name, as the letters should print it.</summary>
    public static async Task<string> OrgNameAsync(this HrOrg o) =>
        (string)(await o.F.QueryAsync("SELECT OrgName FROM tenant.Organizations WHERE Id = @o", ("@o", o.OrgId))).Single()["OrgName"]!;

    /// <summary>Every account's balance in a trial balance by code.</summary>
    public static Dictionary<string, decimal> PeriodNet(JsonElement tb) =>
        tb.A("lines").ToDictionary(l => l.P("account").S("accountCode")!, l => l.D("periodDebit") - l.D("periodCredit"));

    /// <summary>Approves through the workflow inbox route: the approver's own pending step for this document.</summary>
    public static async Task ApproveViaInboxAsync(this SapKit k, HttpClient approver, Guid approvalUuid, string remarks = "QA inbox")
    {
        var inbox = await k.Ok(k.Get("/api/workflow/inbox?pageSize=100", approver), "inbox");
        J.Short(inbox).Should().Contain(approvalUuid.ToString(), "the step is in the approver's inbox");
        await k.Ok(k.Post("/api/workflow/approve", new { approvalUUID = approvalUuid, remarks }, approver), "inbox approve");
    }
}
