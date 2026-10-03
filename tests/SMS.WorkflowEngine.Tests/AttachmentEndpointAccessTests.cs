using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Controllers;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// <c>api/attachments</c> end to end in process: the real controller over the real service and an
/// in-memory database shared by two organizations, so the access policy, the refusal to remove a filed
/// document and the explicit organization filter are each exercised rather than assumed. Uploads land in
/// a throw-away web root, so a refused upload can be shown to have written nothing.
/// </summary>
public sealed class AttachmentEndpointAccessTests : IDisposable
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid Document = Guid.NewGuid();

    private static readonly byte[] Pdf = "%PDF-1.7\n% filed\n%%EOF"u8.ToArray();

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly string _webRoot  = Path.Combine(Path.GetTempPath(), "sms-attachment-access-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static readonly string[] Everything        = PermissionCodes.All.ToArray();
    private static string[] Auditor        => SeededRoles.Permissions(EnumRole.Auditor);
    private static string[] PurchaseOfficer => SeededRoles.Permissions(EnumRole.PurchaseOfficer);
    private static string[] FinanceOfficer  => SeededRoles.Permissions(EnumRole.FinanceOfficer);
    private static string[] Requester       => SeededRoles.Permissions(EnumRole.Requester);

    private WorkflowDbContext Db(Guid org, bool superAdmin = false) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>()
                .UseInMemoryDatabase(_database)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    private AttachmentService Service(Guid org, bool superAdmin = false)
    {
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync((IReadOnlyList<int> ids) =>
            (IReadOnlyList<UserIdentity>)ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());
        return new AttachmentService(Db(org, superAdmin), users.Object);
    }

    private AttachmentsController Controller(Guid org, string[] permissions, bool superAdmin = false, int user = 7)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);

        var claims = permissions.Select(p => new Claim("permission", p))
            .Append(new Claim("sub", user.ToString()))
            .Append(new Claim("organizationId", org.ToString()));
        if (superAdmin) claims = claims.Append(new Claim("is_super_admin", "true"));

        return new AttachmentsController(Service(org, superAdmin), env.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static IFormFile AFile(string name = "delivery-note.pdf", string contentType = "application/pdf")
    {
        var stream = new MemoryStream(Pdf);
        return new FormFile(stream, 0, stream.Length, "file", name)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };
    }

    private string[] FilesWritten() =>
        Directory.Exists(_webRoot) ? Directory.GetFiles(_webRoot, "*", SearchOption.AllDirectories) : [];

    private async Task<Guid> Uploaded(Guid org, string code = "PO", Guid? document = null, int by = 7) =>
        await Service(org).CreateAsync(new CreateAttachmentRequest
        {
            InterfaceCode = code, DocumentId = document ?? Document, FileName = "quote.pdf",
            FileUrl = $"/uploads/attachments/{code.ToLowerInvariant()}/x.pdf", ContentType = "application/pdf"
        }, uploadedBy: by);

    private async Task<Guid> Filed(Guid org, string code, string? permission) =>
        (await Service(org).StoreGeneratedAsync(new GeneratedAttachmentRequest
        {
            InterfaceCode = code, DocumentId = Document, FileName = "filed.pdf", ContentType = "application/pdf",
            Content = Pdf, RequiredPermission = permission
        }, uploadedBy: 7)).Uuid;

    private async Task<List<Guid>> Listed(Guid org, string code = "PO") =>
        (await Service(org).GetByDocumentAsync(code, Document)).Select(a => a.UUID).ToList();

    private static int Status(IActionResult result) => result switch
    {
        ObjectResult o     => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        FileResult         => 200,
        _                  => throw new InvalidOperationException(result.GetType().Name)
    };

    private static string Message(IActionResult result) =>
        ((result as ObjectResult)?.Value as ApiResponse)?.Message ?? string.Empty;

    // ── Uploading ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_auditor_cannot_attach_a_file_to_a_purchase_order_and_nothing_is_written()
    {
        var result = await Controller(OrgA, Auditor).Upload(AFile(), "PO", Document, notes: null);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
        FilesWritten().Should().BeEmpty("the permission is checked before the file touches the disk");
        (await Listed(OrgA)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_purchase_officer_attaches_a_file_to_a_purchase_order_under_their_own_organization()
    {
        var result = await Controller(OrgA, PurchaseOfficer).Upload(AFile(), "PO", Document, notes: "Supplier's quote");

        Status(result).Should().Be(StatusCodes.Status200OK);
        var uuid = ((result as OkObjectResult)!.Value as ApiResponse<Guid>)!.Result;
        (await Listed(OrgA)).Should().Equal(uuid);
        (await Listed(OrgB)).Should().BeEmpty();
        FilesWritten().Should().ContainSingle().Which.Should().Contain(Path.Combine("uploads", "attachments", "po"));
    }

    [Theory]
    [InlineData("SUPPLIER_QUOTATION")]
    [InlineData("po")]
    [InlineData("MIR_GENERAL")]
    [InlineData("../../escaped")]          // the code used to become a folder name as it came
    [InlineData("..\\..\\escaped")]
    public async Task A_kind_of_document_that_is_not_in_the_map_is_refused_before_anything_is_written(string code)
    {
        var result = await Controller(OrgA, Everything).Upload(AFile(), code, Document, notes: null);

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
        FilesWritten().Should().BeEmpty();
        (await Service(OrgA, superAdmin: true).GetByDocumentAsync(code, Document)).Should().BeEmpty();
    }

    [Fact]
    public async Task Nobody_can_upload_among_a_sales_invoices_filed_copies_not_even_a_holder_of_every_permission()
    {
        var result = await Controller(OrgA, Everything).Upload(AFile(), "SALES_INVOICE", Document, notes: null);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
        FilesWritten().Should().BeEmpty();
    }

    [Theory]
    [InlineData("statement.html", "text/html")]
    [InlineData("statement.html", "application/pdf")]
    [InlineData("page.hxt",       "application/octet-stream")]
    [InlineData("schema.xsd",     "application/octet-stream")]
    [InlineData("logo.svg",       "image/svg+xml")]
    [InlineData("quote.pdf",      "text/html")]
    [InlineData("setup.exe",      "application/octet-stream")]
    public async Task A_file_that_is_not_an_ordinary_document_or_picture_is_refused_and_nothing_is_written(string name, string contentType)
    {
        var result = await Controller(OrgA, Everything).Upload(AFile(name, contentType), "PO", Document, notes: null);

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
        FilesWritten().Should().BeEmpty();
        (await Listed(OrgA)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_product_image_or_a_logo_must_be_a_picture()
    {
        Status(await Controller(OrgA, Everything).Upload(AFile("spec.pdf"), "PRODUCT_IMAGE", Document, notes: null))
            .Should().Be(StatusCodes.Status400BadRequest);
        Status(await Controller(OrgA, Everything).Upload(AFile("logo.pdf"), "PO_TEMPLATE_LOGO", Document, notes: null))
            .Should().Be(StatusCodes.Status400BadRequest);
        FilesWritten().Should().BeEmpty();

        Status(await Controller(OrgA, Everything).Upload(AFile("shoe.png", "image/png"), "PRODUCT_IMAGE", Document, notes: null))
            .Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task What_a_file_is_recorded_as_comes_from_its_extension_and_its_name_is_made_safe()
    {
        await Controller(OrgA, PurchaseOfficer).Upload(AFile("..\\..\\quotes\\Final\tQuote.PDF", "application/x-anything"), "PO", Document, notes: null);

        var listed = (await Service(OrgA).GetByDocumentAsync("PO", Document)).Single();
        (listed.FileName, listed.ContentType).Should().Be(("FinalQuote.PDF", "application/pdf"));
        listed.FileUrl.Should().MatchRegex("^/uploads/attachments/po/[0-9a-f-]{36}\\.pdf$");
    }

    [Fact]
    public async Task Notes_too_long_for_their_column_are_refused_before_anything_is_written()
    {
        var result = await Controller(OrgA, PurchaseOfficer).Upload(AFile(), "PO", Document, notes: new string('n', 301));

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
        FilesWritten().Should().BeEmpty();
    }

    [Fact]
    public async Task A_super_admins_upload_is_filed_under_their_own_organization()
    {
        await Controller(OrgB, Everything, superAdmin: true).Upload(AFile(), "PO", Document, notes: null);

        (await Listed(OrgB)).Should().ContainSingle();
        (await Listed(OrgA)).Should().BeEmpty();
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_auditor_still_sees_a_purchase_orders_files()
    {
        var uuid = await Uploaded(OrgA);

        var result = await Controller(OrgA, Auditor).GetByDocument("PO", Document);

        Status(result).Should().Be(StatusCodes.Status200OK);
        ((result as OkObjectResult)!.Value as ApiResponse<List<AttachmentModel>>)!.Result!.Select(a => a.UUID).Should().Equal(uuid);
    }

    [Fact]
    public async Task A_caller_who_cannot_open_the_document_cannot_list_its_files()
    {
        await Uploaded(OrgA, "INVOICE");

        var result = await Controller(OrgA, Requester).GetByDocument("INVOICE", Document);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_kind_of_document_that_is_not_in_the_map_cannot_be_listed()
    {
        Status(await Controller(OrgA, Everything).GetByDocument("SUPPLIER_QUOTATION", Document))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task The_list_says_which_files_the_system_filed()
    {
        var uploaded = await Uploaded(OrgA, "DELIVERY");
        var filed    = await Filed(OrgA, "DELIVERY", "DELIVERY_VIEW");

        var list = await Service(OrgA).GetByDocumentAsync("DELIVERY", Document);

        list.Single(a => a.UUID == filed).IsGenerated.Should().BeTrue();
        list.Single(a => a.UUID == uploaded).IsGenerated.Should().BeFalse();
    }

    // ── Removing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_auditor_cannot_remove_a_file_and_it_stays()
    {
        var uuid = await Uploaded(OrgA);

        var result = await Controller(OrgA, Auditor).Delete(uuid);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
        (await Listed(OrgA)).Should().Equal(uuid);
    }

    [Fact]
    public async Task Somebody_with_no_say_over_purchase_orders_cannot_remove_one_of_their_files()
    {
        var uuid = await Uploaded(OrgA);

        Status(await Controller(OrgA, FinanceOfficer).Delete(uuid)).Should().Be(StatusCodes.Status403Forbidden);
        (await Listed(OrgA)).Should().Equal(uuid);
    }

    [Fact]
    public async Task A_purchase_officer_removes_a_file_from_a_purchase_order()
    {
        var uuid = await Uploaded(OrgA);

        Status(await Controller(OrgA, PurchaseOfficer).Delete(uuid)).Should().Be(StatusCodes.Status200OK);
        (await Listed(OrgA)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("SALES_INVOICE", "SALES_INVOICE_VIEW")]
    [InlineData("DELIVERY",      "DELIVERY_VIEW")]
    public async Task A_document_the_system_filed_cannot_be_removed_by_anybody(string code, string filedWith)
    {
        var uuid = await Filed(OrgA, code, filedWith);

        var result = await Controller(OrgA, Everything).Delete(uuid);

        Status(result).Should().Be(StatusCodes.Status409Conflict);
        Message(result).Should().Contain("filed by the system").And.Contain("cannot be removed");
        (await Listed(OrgA, code)).Should().Equal(uuid);
        (await Service(OrgA).GetContentAsync(uuid)).Should().NotBeNull("it is still on file and can still be opened");
    }

    [Fact]
    public async Task A_gate_pass_cannot_be_removed_by_the_people_who_may_otherwise_change_the_delivery()
    {
        var uuid = await Filed(OrgA, "DELIVERY", "DELIVERY_VIEW");
        var uploaded = await Uploaded(OrgA, "DELIVERY");
        var operatorOfTheDock = SeededRoles.Permissions(EnumRole.WarehouseOperator);

        Status(await Controller(OrgA, operatorOfTheDock).Delete(uuid)).Should().Be(StatusCodes.Status409Conflict);
        Status(await Controller(OrgA, operatorOfTheDock).Delete(uploaded)).Should().Be(StatusCodes.Status200OK,
            "an ordinary file on the same delivery is theirs to remove");
    }

    [Fact]
    public async Task The_service_itself_refuses_to_remove_a_filed_document()
    {
        var uuid = await Filed(OrgA, "SALES_INVOICE", "SALES_INVOICE_VIEW");

        var act = () => Service(OrgA).DeleteAsync(uuid, deletedBy: 7);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*filed by the system*");
        (await Listed(OrgA, "SALES_INVOICE")).Should().Equal(uuid);
    }

    [Fact]
    public async Task A_requester_removes_their_own_file_from_a_requisition_but_not_one_somebody_else_attached()
    {
        var requester = SeededRoles.Permissions(EnumRole.Requester);
        var mine   = await Uploaded(OrgA, "PR", by: 4);
        var theirs = await Uploaded(OrgA, "PR", by: 1);

        Status(await Controller(OrgA, requester, user: 4).Delete(theirs)).Should().Be(StatusCodes.Status403Forbidden);
        Status(await Controller(OrgA, requester, user: 4).Delete(mine)).Should().Be(StatusCodes.Status200OK);
        (await Listed(OrgA, "PR")).Should().Equal(theirs);
    }

    [Fact]
    public async Task An_approver_removes_anybodys_file_from_a_requisition()
    {
        var theirs = await Uploaded(OrgA, "PR", by: 4);

        Status(await Controller(OrgA, SeededRoles.Permissions(EnumRole.ProcurementManager), user: 2).Delete(theirs))
            .Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task The_list_tells_each_caller_which_files_they_may_remove()
    {
        var mine   = await Uploaded(OrgA, "PR", by: 4);
        var theirs = await Uploaded(OrgA, "PR", by: 1);
        var filed  = await Filed(OrgA, "PR", "REQUISITION_VIEW_ALL");

        async Task<Dictionary<Guid, bool>> CanRemove(string[] permissions, int user)
        {
            var result = await Controller(OrgA, permissions, user: user).GetByDocument("PR", Document);
            return ((result as OkObjectResult)!.Value as ApiResponse<List<AttachmentModel>>)!.Result!.ToDictionary(a => a.UUID, a => a.CanRemove);
        }

        (await CanRemove(Requester, 4)).Should().Equal(new Dictionary<Guid, bool> { [mine] = true, [theirs] = false, [filed] = false });
        (await CanRemove(SeededRoles.Permissions(EnumRole.ProcurementManager), 2))
            .Should().Equal(new Dictionary<Guid, bool> { [mine] = true, [theirs] = true, [filed] = false });
        (await CanRemove(Auditor, 8)).Values.Should().OnlyContain(can => !can);
    }

    [Fact]
    public async Task Removing_a_file_that_is_not_there_is_404()
    {
        Status(await Controller(OrgA, Everything).Delete(Guid.NewGuid())).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Another organization ─────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // a super admin bypasses the EF tenant filter; this endpoint still keeps to their own organization
    public async Task Another_organizations_files_are_404_to_remove_or_open_and_absent_from_its_list(bool superAdmin)
    {
        var uploaded = await Uploaded(OrgA);
        var filed    = await Filed(OrgA, "PO", permission: null);
        var asB      = Controller(OrgB, Everything, superAdmin);

        var list = await asB.GetByDocument("PO", Document);
        ((list as OkObjectResult)!.Value as ApiResponse<List<AttachmentModel>>)!.Result.Should().BeEmpty();

        Status(await asB.Delete(uploaded)).Should().Be(StatusCodes.Status404NotFound);
        Status(await asB.GetContent(filed)).Should().Be(StatusCodes.Status404NotFound);

        (await Listed(OrgA)).Should().BeEquivalentTo(new[] { uploaded, filed }, "nothing was removed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_service_keeps_every_read_to_the_callers_own_organization(bool superAdmin)
    {
        var uploaded = await Uploaded(OrgA);
        var filed    = await Filed(OrgA, "PO", "PO_VIEW");
        var asB      = Service(OrgB, superAdmin);

        (await asB.GetByDocumentAsync("PO", Document)).Should().BeEmpty();
        (await asB.FindAsync(uploaded)).Should().BeNull();
        (await asB.GetContentAsync(filed)).Should().BeNull();
        (await asB.GetCountsByDocumentIdsAsync("PO", [Document])).Should().BeEmpty();
        await asB.Awaiting(s => s.DeleteAsync(uploaded, 7)).Should().ThrowAsync<NotFoundException>();

        (await Service(OrgA).GetCountsByDocumentIdsAsync("PO", [Document])).Should().Equal(
            new Dictionary<Guid, int> { [Document] = 2 });
    }

    [Fact]
    public async Task The_same_document_id_in_two_organizations_keeps_two_separate_lists()
    {
        var mine  = await Uploaded(OrgA);
        var yours = await Uploaded(OrgB);

        (await Listed(OrgA)).Should().Equal(mine);
        (await Listed(OrgB)).Should().Equal(yours);
    }

    [Fact]
    public async Task Filing_the_same_bytes_in_another_organization_never_matches_the_first_ones_copy_even_for_a_super_admin()
    {
        var mine = await Filed(OrgA, "SALES_INVOICE", "SALES_INVOICE_VIEW");

        var yours = await Service(OrgB, superAdmin: true).StoreGeneratedAsync(new GeneratedAttachmentRequest
        {
            InterfaceCode = "SALES_INVOICE", DocumentId = Document, FileName = "filed.pdf", ContentType = "application/pdf",
            Content = Pdf, RequiredPermission = "SALES_INVOICE_VIEW"
        }, uploadedBy: 7);

        yours.AlreadyStored.Should().BeFalse();
        yours.Uuid.Should().NotBe(mine);
    }

    // ── Opening a filed document ─────────────────────────────────────────────

    [Fact]
    public async Task A_filed_document_opens_only_for_a_caller_who_may_see_that_kind_of_document_and_holds_what_it_was_filed_with()
    {
        var uuid = await Filed(OrgA, "SALES_INVOICE", "SALES_INVOICE_VIEW");

        Status(await Controller(OrgA, ["SALES_INVOICE_VIEW"]).GetContent(uuid)).Should().Be(StatusCodes.Status200OK);
        Status(await Controller(OrgA, ["SALES_INVOICE_MANAGE"]).GetContent(uuid)).Should().Be(StatusCodes.Status403Forbidden);
        Status(await Controller(OrgA, ["DELIVERY_VIEW"]).GetContent(uuid)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_document_filed_with_no_permission_of_its_own_still_needs_the_permission_to_see_that_kind_of_document()
    {
        var uuid = await Filed(OrgA, "DELIVERY", permission: null);

        Status(await Controller(OrgA, ["DELIVERY_VIEW"]).GetContent(uuid)).Should().Be(StatusCodes.Status200OK);
        Status(await Controller(OrgA, Requester).GetContent(uuid)).Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── The policy, for the frontend ─────────────────────────────────────────

    [Fact]
    public void The_policy_endpoint_hands_the_frontend_every_rule_exactly_as_the_server_applies_it()
    {
        var result = Controller(OrgA, Requester).GetPolicy();

        var rules = ((result as OkObjectResult)!.Value as ApiResponse<List<AttachmentAccessRuleModel>>)!.Result!;
        rules.Select(r => r.InterfaceCode).Should().Equal(AttachmentAccessPolicy.Rules.Select(r => r.InterfaceCode));
        foreach (var rule in AttachmentAccessPolicy.Rules)
        {
            var sent = rules.Single(r => r.InterfaceCode == rule.InterfaceCode);
            sent.View.Should().Equal(rule.View);
            sent.Upload.Should().Equal(rule.Upload);
            sent.Delete.Should().Equal(rule.Delete);
        }
    }

    [Fact]
    public void The_policy_needs_only_a_sign_in_because_every_page_with_an_attachment_panel_reads_it()
    {
        var method = typeof(AttachmentsController).GetMethod(nameof(AttachmentsController.GetPolicy))!;

        method.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single().Template.Should().Be("policy");
        method.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Should().BeEmpty();
        method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Should().BeEmpty();
        typeof(AttachmentsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Should().NotBeEmpty();
    }
}
