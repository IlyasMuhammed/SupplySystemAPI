using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 D-25 on the real host: the five HRM upload kinds are read through the row that holds them (self, in scope, out of scope, no permission,
/// another organization), api/attachments refuses them, a save only accepts the caller's own upload of the right kind, and an unused upload can
/// be removed. Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmAttachmentAccessE2ETests</c>.
/// </summary>
public sealed class HrmAttachmentAccessE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.7\n% hr file\n%%EOF"u8.ToArray();
    private static readonly string[] HrCodes =
        ["HRM_EMPLOYEE_EDIT", "HRM_LEAVE_VIEW", "HRM_EXPENSE_VIEW", "HRM_TRAINING_VIEW", "HRM_SCOPE_DEPARTMENT"];

    private readonly SapWebApplicationFactory _factory;
    private readonly SapKit _root;

    public HrmAttachmentAccessE2ETests(SapWebApplicationFactory factory)
    {
        _factory = factory;
        _root = new SapKit(factory, "A49A");
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string code)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "scan.pdf");
        form.Add(new StringContent(code), "interfaceCode");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "documentId");
        var response = await client.PostAsync("/api/attachments/upload", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK, code);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("result").GetGuid();
    }

    private sealed record World(ModOrg O, HttpClient Admin, HttpClient Staff, HttpClient Peer, HttpClient HrIn, HttpClient HrOut, HttpClient Nobody, Guid Employee, Guid HrOutEmployee, Guid LeaveType, Guid Course, Guid Category);

    private async Task<World> WorldAsync(string tag)
    {
        var o = await _root.OrgAsync(tag, currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o, "FEATURE_HRM_LEAVE", "FEATURE_HRM_EXPENSES", "FEATURE_HRM_RECRUITMENT", "FEATURE_HRM_TRAINING");
        var admin = _factory.CreateBearerClient(await _factory.LoginAsync(o.Email, "A37Qa@12345!"));

        var branch = await k.Ok(k.Post("/api/hrm/branches", new { code = "khi", name = "Karachi" }), "branch");
        var center = await k.Ok(k.Post("/api/hrm/cost-centers", new { code = "OPS", name = "Operations" }), "cost center");
        var inside = await k.Ok(k.Post("/api/hrm/departments", new { code = "IN", name = "Inside", costCenterUuid = center.G("uuid") }), "department");
        var outside = await k.Ok(k.Post("/api/hrm/departments", new { code = "OUT", name = "Outside", costCenterUuid = center.G("uuid") }), "department");
        var designation = await k.Ok(k.Post("/api/hrm/designations", new { code = "CLERK", name = "Clerk", level = 1 }), "designation");
        object Hire(JsonElement department, Guid? manager = null) => new { effectiveFrom = "2026-01-01", departmentUuid = department.G("uuid"), designationUuid = designation.G("uuid"), branchUuid = branch.G("uuid"), managerUuid = manager };

        var bossUser = await k.LoginWithPermissionsAsync(tag + "boss", "HRM_LEAVE_APPROVE", "HRM_SCOPE_ALL");
        var staff = await k.LoginWithPermissionsAsync(tag + "staff", "HRM_SELF_SERVICE");
        var peer = await k.LoginWithPermissionsAsync(tag + "peer", "HRM_SELF_SERVICE");
        var hrIn = await k.LoginWithPermissionsAsync(tag + "hrin", HrCodes);
        var hrOut = await k.LoginWithPermissionsAsync(tag + "hrout", HrCodes);
        var nobody = await k.LoginWithPermissionsAsync(tag + "none", "CURRENCY_VIEW");

        var boss = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Boss", lastName = "One", userId = ServiceKit.AdminUserId(bossUser), employment = Hire(outside) }), "boss");
        var employee = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Zainab", lastName = "Malik", userId = ServiceKit.AdminUserId(staff), employment = Hire(inside, boss.G("uuid")) }), "employee");
        await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Peer", lastName = "Two", userId = ServiceKit.AdminUserId(peer), employment = Hire(outside) }), "peer");
        await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Hr", lastName = "Inside", userId = ServiceKit.AdminUserId(hrIn), employment = Hire(inside) }), "hr inside");
        var hrOutEmployee = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Hr", lastName = "Outside", userId = ServiceKit.AdminUserId(hrOut), employment = Hire(outside) }), "hr outside");

        var leaveType = await k.Ok(k.Post("/api/hrm/leave/types", new { code = "PROOF", name = "Needs proof", isPaid = false, attachmentAfterDays = 0 }), "leave type");
        var course = await k.Ok(k.Post("/api/hrm/courses", new { code = "SAFE", name = "Safety" }), "course");
        var category = await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "TAXI", name = "Taxi", accountRole = "DEFAULT_SALARY_EXPENSE" }), "category");
        return new World(o, admin, staff, peer, hrIn, hrOut, nobody, employee.G("uuid"), hrOutEmployee.G("uuid"), leaveType.G("uuid"), course.G("uuid"), category.G("uuid"));
    }

    private static object Leave(World w, Guid? attachment) => new { leaveTypeUuid = w.LeaveType, fromDate = "2026-03-02", toDate = "2026-03-02", attachmentUuid = attachment };
    private static object Claim(World w, Guid? attachment) => new { settlementMethod = "SeparatePayment", lines = new[] { new { categoryUuid = w.Category, expenseDate = "2026-03-10", amount = 500m, taxAmount = 0m, attachmentUuid = attachment } } };

    private async Task<JsonElement> EnrollAsync(World w) =>
        await w.O.K.Ok(w.O.K.Post("/api/hrm/enrollments", new { employeeUuid = w.Employee, courseUuid = w.Course, enrolledDate = "2026-02-01" }), "enroll");

    /// <summary>The file belongs to the employee's row: the same answer for each of the four employee-bound kinds.</summary>
    private async Task AccessMatrixAsync(World w, string kind, Guid file, Guid row, ModOrg otherOrg)
    {
        var k = w.O.K;
        var content = $"/api/hrm/attachments/{file}/content";
        var list = $"/api/hrm/attachments?interfaceCode={kind}&rowUuid={row}";

        var self = await w.Staff.GetAsync(content);
        self.StatusCode.Should().Be(HttpStatusCode.OK, $"{kind}: the employee's own file");
        (await self.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        self.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await k.Ok(k.Get(list, w.Staff), $"{kind}: own list")).EnumerateArray().Select(f => f.G("uuid")).Should().Contain(file);

        (await k.Get(content, w.HrIn)).Status.Should().Be(HttpStatusCode.OK, $"{kind}: HR over this department");
        (await k.Ok(k.Get(list, w.HrIn), $"{kind}: in-scope list")).EnumerateArray().Select(f => f.G("uuid")).Should().Contain(file);

        (await k.Get(content, w.HrOut)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: HR over another department");
        (await k.Get(list, w.HrOut)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: out-of-scope list");
        (await k.Get(content, w.Peer)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: a colleague");
        (await k.Get(list, w.Peer)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: a colleague's list");

        (await k.Get(content, w.Nobody)).Status.Should().Be(HttpStatusCode.Forbidden, $"{kind}: no HRM code");
        (await k.Get(list, w.Nobody)).Status.Should().Be(HttpStatusCode.Forbidden, $"{kind}: no HRM code");

        (await otherOrg.K.Get(content)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: another organization");
        (await otherOrg.K.Get(list)).Status.Should().Be(HttpStatusCode.NotFound, $"{kind}: another organization");

        await GenericRoutesRefuseAsync(w, kind, file);
    }

    private static async Task GenericRoutesRefuseAsync(World w, string kind, Guid file)
    {
        var k = w.O.K;
        foreach (var caller in new[] { w.Admin, w.Staff, w.HrIn })
        {
            (await caller.GetAsync($"/api/attachments/{file}/content")).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{kind}: the generic content route cannot check the row");
            (await caller.GetAsync($"/api/attachments?interface={kind}&documentId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{kind}: the generic list route");
            (await caller.GetAsync($"/api/attachments?interface={kind.ToLowerInvariant()}&documentId={Guid.NewGuid()}")).StatusCode.Should().NotBe(HttpStatusCode.OK, $"{kind}: another casing of the code");
        }
        (await k.Get($"/api/attachments/{file}/content")).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Each_employee_kind_reads_for_self_and_in_scope_only_and_the_generic_routes_refuse()
    {
        var w = await WorldAsync("AAM");
        var k = w.O.K;
        var other = await _root.OrgAsync("AAX", currency: false);
        await _root.EnableHrmAsync(other);

        var receipt = await UploadAsync(w.Staff, "HRM_EXPENSE_CLAIM");
        var claim = await k.Ok(k.Post("/api/hrm/expense-claims", Claim(w, receipt), w.Staff), "claim");
        await AccessMatrixAsync(w, "HRM_EXPENSE_CLAIM", receipt, claim.G("uuid"), other);

        var note = await UploadAsync(w.Staff, "HRM_LEAVE_REQUEST");
        var leave = await k.Ok(k.Post("/api/hrm/leave/requests", Leave(w, note), w.Staff), "leave");
        leave.G("attachmentUuid").Should().Be(note);
        await AccessMatrixAsync(w, "HRM_LEAVE_REQUEST", note, leave.G("uuid"), other);

        var scan = await UploadAsync(w.Admin, "HRM_EMPLOYEE");
        await k.Ok(k.Post($"/api/hrm/employees/{w.Employee}/documents", new { title = "CNIC", documentType = "CNIC", attachmentUuid = scan }), "document");
        await AccessMatrixAsync(w, "HRM_EMPLOYEE", scan, w.Employee, other);

        var certificate = await UploadAsync(w.Admin, "HRM_ENROLLMENT");
        var enrollment = await EnrollAsync(w);
        await k.Ok(k.Post($"/api/hrm/enrollments/{enrollment.G("uuid")}/complete", new { completedDate = "2026-02-10", certificateAttachmentUuid = certificate }), "complete");
        await AccessMatrixAsync(w, "HRM_ENROLLMENT", certificate, enrollment.G("uuid"), other);
    }

    [Fact]
    public async Task A_resume_reads_under_the_recruitment_scope()
    {
        var w = await WorldAsync("AAC");
        var k = w.O.K;
        var resume = await UploadAsync(w.Admin, "HRM_CANDIDATE");
        var candidate = await k.Ok(k.Post("/api/hrm/candidates", new { firstName = "Sara", lastName = "Ali", email = "sara@example.com", resumeAttachmentUuid = resume }), "candidate");
        var content = $"/api/hrm/attachments/{resume}/content";
        var list = $"/api/hrm/attachments?interfaceCode=HRM_CANDIDATE&rowUuid={candidate.G("uuid")}";

        var everywhere = await k.LoginWithPermissionsAsync("aacall", "HRM_RECRUITMENT_VIEW", "HRM_SCOPE_ALL");
        (await k.Get(content, everywhere)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Ok(k.Get(list, everywhere), "list")).GetArrayLength().Should().Be(1);

        var limited = await k.LoginWithPermissionsAsync("aaclim", "HRM_RECRUITMENT_VIEW");
        (await k.Get(content, limited)).Status.Should().Be(HttpStatusCode.NotFound, "no vacancy in scope holds this candidate");
        (await k.Get(list, limited)).Status.Should().Be(HttpStatusCode.NotFound);
        (await k.Get(content, w.HrIn)).Status.Should().Be(HttpStatusCode.NotFound, "HR codes of another kind");
        (await k.Get(content, w.Nobody)).Status.Should().Be(HttpStatusCode.Forbidden);

        var other = await _root.OrgAsync("AAY", currency: false);
        await _root.EnableHrmAsync(other);
        (await other.K.Get(content)).Status.Should().Be(HttpStatusCode.NotFound);
        await GenericRoutesRefuseAsync(w, "HRM_CANDIDATE", resume);
    }

    [Fact]
    public async Task A_save_accepts_only_the_callers_own_upload_of_the_right_kind_in_this_organization()
    {
        var w = await WorldAsync("AAS");
        var k = w.O.K;
        var other = await _root.OrgAsync("AAZ", currency: false);
        var otherOrgs = await UploadAsync(_factory.CreateBearerClient(await _factory.LoginAsync(other.Email, "A37Qa@12345!")), "HRM_LEAVE_REQUEST");

        // Leave: nothing is LEAVE_ATTACHMENT_REQUIRED; each kind of bad uuid is HRM_ATTACHMENT_INVALID.
        var none = await k.Post("/api/hrm/leave/requests", Leave(w, null), w.Staff);
        none.Status.Should().Be(HttpStatusCode.BadRequest);
        none.ToString().Should().Contain("LEAVE_ATTACHMENT_REQUIRED");

        var wrongKind = await UploadAsync(w.Staff, "HRM_EXPENSE_CLAIM");
        var someoneElses = await UploadAsync(w.Peer, "HRM_LEAVE_REQUEST");
        foreach (var bad in new[] { Guid.NewGuid(), wrongKind, someoneElses, otherOrgs })
        {
            var refused = await k.Post("/api/hrm/leave/requests", Leave(w, bad), w.Staff);
            refused.Status.Should().Be(HttpStatusCode.BadRequest);
            refused.ToString().Should().Contain("HRM_ATTACHMENT_INVALID");
        }

        // The other save sites, with a made-up uuid.
        var made = Guid.NewGuid();
        var enrollment = await EnrollAsync(w);
        var attempts = new[]
        {
            await k.Post("/api/hrm/expense-claims", Claim(w, made), w.Staff),
            await k.Post($"/api/hrm/employees/{w.Employee}/documents", new { title = "CNIC", documentType = "CNIC", attachmentUuid = made }),
            await k.Post("/api/hrm/employees", new { firstName = "No", lastName = "Photo", photoAttachmentUuid = made }),
            await k.Post("/api/hrm/candidates", new { firstName = "No", lastName = "Resume", email = "none@example.com", resumeAttachmentUuid = made }),
            await k.Post($"/api/hrm/enrollments/{enrollment.G("uuid")}/complete", new { completedDate = "2026-02-10", certificateAttachmentUuid = made })
        };
        foreach (var attempt in attempts)
        {
            attempt.Status.Should().Be(HttpStatusCode.BadRequest);
            attempt.ToString().Should().Contain("HRM_ATTACHMENT_INVALID");
        }

        // A row can be edited by someone else while keeping a file it already holds.
        var resume = await UploadAsync(w.Admin, "HRM_CANDIDATE");
        var candidate = await k.Ok(k.Post("/api/hrm/candidates", new { firstName = "Sara", lastName = "Ali", email = "keep@example.com", resumeAttachmentUuid = resume }), "candidate");
        var recruiter = await k.LoginWithPermissionsAsync("aasrec", "HRM_RECRUITMENT_MANAGE", "HRM_SCOPE_ALL");
        await k.Ok(k.Put($"/api/hrm/candidates/{candidate.G("uuid")}", new { firstName = "Sara", lastName = "Khan", email = "keep@example.com", resumeAttachmentUuid = resume }, recruiter), "kept file");
    }

    [Fact]
    public async Task An_unused_upload_is_removed_by_its_uploader_and_a_used_one_is_not()
    {
        var w = await WorldAsync("AAR");
        var k = w.O.K;

        var dropped = await UploadAsync(w.Staff, "HRM_LEAVE_REQUEST");
        (await k.Get($"/api/hrm/attachments/{dropped}/content", w.Peer)).Status.Should().Be(HttpStatusCode.NotFound, "before a row holds it, it is the uploader's alone");
        (await k.Delete($"/api/hrm/attachments/{dropped}", w.Peer)).Status.Should().Be(HttpStatusCode.NotFound, "not their upload");
        (await k.Delete($"/api/attachments/{dropped}", w.Staff)).Status.Should().Be(HttpStatusCode.Conflict, "the generic route does not remove these");
        await k.Ok(k.Delete($"/api/hrm/attachments/{dropped}", w.Staff), "remove");
        (await k.Get($"/api/hrm/attachments/{dropped}/content", w.Staff)).Status.Should().Be(HttpStatusCode.NotFound);
        (await k.Delete($"/api/hrm/attachments/{dropped}", w.Staff)).Status.Should().Be(HttpStatusCode.NotFound);

        var receipt = await UploadAsync(w.Staff, "HRM_EXPENSE_CLAIM");
        await k.Ok(k.Post("/api/hrm/expense-claims", Claim(w, receipt), w.Staff), "claim");
        var inUse = await k.Delete($"/api/hrm/attachments/{receipt}", w.Staff);
        inUse.Status.Should().Be(HttpStatusCode.Conflict);
        inUse.ToString().Should().Contain("ATTACHMENT_IN_USE");
    }
}
