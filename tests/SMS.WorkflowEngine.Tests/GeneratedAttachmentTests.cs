using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Controllers;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// A29-P7-09 — documents the system generates (an issued invoice's PDF, a gate pass) filed as
/// attachments with their bytes in the database rather than under <c>wwwroot</c>, and read back only
/// through an authenticated, permission-checked endpoint.
/// </summary>
public class GeneratedAttachmentTests
{
    private const int Uploader = 42;
    private static readonly Guid Invoice = Guid.NewGuid();

    private static readonly byte[] Pdf = "%PDF-1.7\n% a filed invoice\n%%EOF"u8.ToArray();

    private sealed record H(WorkflowDbContext Db, AttachmentService Service, Guid Org, string DbName);

    private static H New(Guid? org = null, string? dbName = null)
    {
        org    ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var db = new WorkflowDbContext(options, new StaticTenantContext { OrganizationId = org.Value });

        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync((IReadOnlyList<int> ids) =>
            (IReadOnlyList<UserIdentity>)ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());

        return new H(db, new AttachmentService(db, users.Object), org.Value, dbName);
    }

    private static GeneratedAttachmentRequest Request(
        byte[]? content = null, Guid? document = null, string interfaceCode = "SALES_INVOICE",
        string fileName = "SINV-20260920-0001.pdf", string contentType = "application/pdf",
        string? notes = "Filed when the invoice was issued", string? permission = "SALES_INVOICE_VIEW") =>
        new()
        {
            InterfaceCode = interfaceCode, DocumentId = document ?? Invoice, FileName = fileName,
            ContentType = contentType, Content = content ?? Pdf, Notes = notes, RequiredPermission = permission
        };

    // ── Storing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_generated_document_is_filed_with_its_bytes_in_the_database_and_an_authenticated_url()
    {
        var h = New();

        var stored = await h.Service.StoreGeneratedAsync(Request(), Uploader);

        stored.AlreadyStored.Should().BeFalse();

        var attachment = await h.Db.DocumentAttachments.AsNoTracking().SingleAsync();
        attachment.UUID.Should().Be(stored.Uuid);
        (attachment.InterfaceCode, attachment.DocumentId, attachment.FileName).Should().Be(("SALES_INVOICE", Invoice, "SINV-20260920-0001.pdf"));
        attachment.FileUrl.Should().Be($"/api/attachments/{stored.Uuid}/content", "not a path into wwwroot");
        attachment.FileUrl.Should().NotContain("uploads");
        (attachment.ContentType, attachment.FileSize, attachment.Notes, attachment.UploadedBy).Should().Be(("application/pdf", (long?)Pdf.Length, "Filed when the invoice was issued", Uploader));
        attachment.IsDelete.Should().BeFalse();
        attachment.OrganizationId.Should().Be(h.Org);

        var content = await h.Db.DocumentAttachmentContents.AsNoTracking().SingleAsync();
        content.DocumentAttachmentId.Should().Be(attachment.Id);
        content.Content.Should().Equal(Pdf);
        content.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant());
        content.RequiredPermission.Should().Be("SALES_INVOICE_VIEW");
        content.OrganizationId.Should().Be(h.Org);
    }

    [Fact]
    public async Task It_appears_in_the_documents_attachment_list_like_any_other_file_without_carrying_its_bytes()
    {
        var h = New();
        await h.Service.StoreGeneratedAsync(Request(), Uploader);

        var listed = (await h.Service.GetByDocumentAsync("SALES_INVOICE", Invoice)).Should().ContainSingle().Subject;

        (listed.FileName, listed.ContentType, listed.FileSize, listed.UploadedByName).Should().Be(("SINV-20260920-0001.pdf", "application/pdf", (long?)Pdf.Length, "User 42"));
        listed.FileUrl.Should().StartWith("/api/attachments/").And.EndWith("/content");
        typeof(AttachmentModel).GetProperties().Select(p => p.PropertyType).Should().NotContain(typeof(byte[]), "listing must never load the file");
    }

    [Fact]
    public async Task Filing_the_same_bytes_on_the_same_document_again_returns_the_first_copy()
    {
        var h = New();

        var first  = await h.Service.StoreGeneratedAsync(Request(), Uploader);
        var second = await h.Service.StoreGeneratedAsync(Request(), 99);

        second.AlreadyStored.Should().BeTrue();
        second.Uuid.Should().Be(first.Uuid);
        (await h.Db.DocumentAttachments.CountAsync()).Should().Be(1);
        (await h.Db.DocumentAttachmentContents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Different_bytes_on_the_same_document_are_a_new_attachment_so_the_history_is_kept()
    {
        var h = New();
        var revised = "%PDF-1.7\n% the invoice after a payment\n%%EOF"u8.ToArray();

        var first  = await h.Service.StoreGeneratedAsync(Request(), Uploader);
        var second = await h.Service.StoreGeneratedAsync(Request(revised), Uploader);

        second.AlreadyStored.Should().BeFalse();
        second.Uuid.Should().NotBe(first.Uuid);
        (await h.Service.GetByDocumentAsync("SALES_INVOICE", Invoice)).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_same_bytes_on_another_document_or_under_another_interface_are_kept_apart()
    {
        var h = New();

        var one   = await h.Service.StoreGeneratedAsync(Request(), Uploader);
        var other = await h.Service.StoreGeneratedAsync(Request(document: Guid.NewGuid()), Uploader);
        var gate  = await h.Service.StoreGeneratedAsync(Request(interfaceCode: "DELIVERY"), Uploader);

        new[] { one.Uuid, other.Uuid, gate.Uuid }.Should().OnlyHaveUniqueItems();
        new[] { one, other, gate }.Should().OnlyContain(s => !s.AlreadyStored);
    }

    [Fact]
    public async Task A_deleted_copy_does_not_count_so_the_document_can_be_filed_again()
    {
        var h = New();
        var first = await h.Service.StoreGeneratedAsync(Request(), Uploader);
        await h.Service.DeleteAsync(first.Uuid, Uploader);

        var again = await h.Service.StoreGeneratedAsync(Request(), Uploader);

        again.AlreadyStored.Should().BeFalse();
        again.Uuid.Should().NotBe(first.Uuid);
    }

    [Fact]
    public async Task Another_organization_filing_the_same_bytes_under_the_same_document_id_gets_its_own_copy()
    {
        var a = New();
        var b = New(Guid.NewGuid(), a.DbName);

        var mine  = await a.Service.StoreGeneratedAsync(Request(), Uploader);
        var yours = await b.Service.StoreGeneratedAsync(Request(), Uploader);

        yours.AlreadyStored.Should().BeFalse("a match in another organization is invisible, not shared");
        yours.Uuid.Should().NotBe(mine.Uuid);
    }

    // ── What is refused ──────────────────────────────────────────────────────

    private static Func<GeneratedAttachmentRequest, GeneratedAttachmentRequest> Spoil(Action<GeneratedAttachmentRequest> change) =>
        request => { change(request); return request; };

    public static IEnumerable<object[]> Refusals()
    {
        yield return ["blank interface code",     Spoil(r => r.InterfaceCode = " "), "InterfaceCode is required"];
        yield return ["over-long interface code", Spoil(r => r.InterfaceCode = new string('X', 31)), "longer than 30"];
        yield return ["no document",              Spoil(r => r.DocumentId = Guid.Empty), "DocumentId is required"];
        yield return ["blank file name",          Spoil(r => r.FileName = "  "), "FileName is required"];
        yield return ["only a path",              Spoil(r => r.FileName = "..\\..\\"), "FileName is required"];
        yield return ["a web page",               Spoil(r => r.ContentType = "text/html"), "Only application/pdf"];
        yield return ["an image",                 Spoil(r => r.ContentType = "image/png"), "Only application/pdf"];
        yield return ["nothing",                  Spoil(r => r.Content = []), "empty"];
        yield return ["html claiming to be pdf",  Spoil(r => r.Content = "<html><script>alert(1)</script></html>"u8.ToArray()), "not a PDF"];
        yield return ["too large",                Spoil(r => r.Content = Pdf.Concat(new byte[AttachmentService.MaxGeneratedBytes]).ToArray()), "larger than 20 MB"];
        yield return ["long notes",               Spoil(r => r.Notes = new string('n', 301)), "Notes are longer"];
        yield return ["long permission",          Spoil(r => r.RequiredPermission = new string('P', 101)), "RequiredPermission"];
    }
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_document_that_should_not_be_filed_is_refused_and_nothing_is_written(
        string why, Func<GeneratedAttachmentRequest, GeneratedAttachmentRequest> spoil, string message)
    {
        _ = why;
        var h = New();

        var act = async () => await h.Service.StoreGeneratedAsync(spoil(Request()), Uploader);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{message}*");
        (await h.Db.DocumentAttachments.CountAsync()).Should().Be(0);
        (await h.Db.DocumentAttachmentContents.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("..\\..\\windows\\evil.pdf", "evil.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("C:\\temp\\SINV-1.pdf", "SINV-1.pdf")]
    [InlineData("SINV\r\n-1.pdf", "SINV-1.pdf")]
    public async Task A_file_name_is_reduced_to_a_safe_leaf(string given, string expected)
    {
        var h = New();

        await h.Service.StoreGeneratedAsync(Request(fileName: given), Uploader);

        (await h.Db.DocumentAttachments.AsNoTracking().SingleAsync()).FileName.Should().Be(expected);
    }

    [Fact]
    public async Task An_over_long_file_name_is_cut_to_the_column()
    {
        var h = New();

        await h.Service.StoreGeneratedAsync(Request(fileName: new string('a', 400) + ".pdf"), Uploader);

        (await h.Db.DocumentAttachments.AsNoTracking().SingleAsync()).FileName.Length.Should().Be(255);
    }

    [Fact]
    public async Task A_content_type_in_any_case_is_accepted_and_stored_normalized()
    {
        var h = New();

        await h.Service.StoreGeneratedAsync(Request(contentType: "Application/PDF"), Uploader);

        (await h.Db.DocumentAttachments.AsNoTracking().SingleAsync()).ContentType.Should().Be("application/pdf");
    }

    // ── Reading back ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_bytes_come_back_with_the_name_the_type_and_the_permission_they_were_filed_with()
    {
        var h = New();
        var stored = await h.Service.StoreGeneratedAsync(Request(), Uploader);

        var file = await h.Service.GetContentAsync(stored.Uuid);

        file.Should().NotBeNull();
        file!.Content.Should().Equal(Pdf);
        (file.FileName, file.ContentType, file.RequiredPermission).Should().Be(("SINV-20260920-0001.pdf", "application/pdf", "SALES_INVOICE_VIEW"));
    }

    [Fact]
    public async Task An_unknown_a_deleted_or_an_uploaded_attachment_has_no_generated_content()
    {
        var h = New();
        var stored = await h.Service.StoreGeneratedAsync(Request(), Uploader);
        var uploaded = await h.Service.CreateAsync(new CreateAttachmentRequest
        {
            InterfaceCode = "INVOICE", DocumentId = Guid.NewGuid(), FileName = "scan.pdf", FileUrl = "/uploads/attachments/invoice/x.pdf"
        }, Uploader);

        (await h.Service.GetContentAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Service.GetContentAsync(uploaded)).Should().BeNull("its file is kept elsewhere and served from its own url");

        await h.Service.DeleteAsync(stored.Uuid, Uploader);
        (await h.Service.GetContentAsync(stored.Uuid)).Should().BeNull("a removed attachment is gone");
    }

    [Fact]
    public async Task Another_organization_cannot_read_the_file_by_guessing_its_id()
    {
        var a = New();
        var stored = await a.Service.StoreGeneratedAsync(Request(), Uploader);
        var b = New(Guid.NewGuid(), a.DbName);

        (await b.Service.GetContentAsync(stored.Uuid)).Should().BeNull();
        (await b.Service.GetByDocumentAsync("SALES_INVOICE", Invoice)).Should().BeEmpty();
    }

    // ── The endpoint ─────────────────────────────────────────────────────────

    private static AttachmentsController Controller(IAttachmentService service, params string[] permissions)
    {
        var claims = permissions.Select(p => new Claim("permission", p)).Append(new Claim("sub", "42"));
        var controller = new AttachmentsController(service, Mock.Of<IWebHostEnvironment>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
        return controller;
    }

    private static Mock<IAttachmentService> Serving(string? requiredPermission)
    {
        var service = new Mock<IAttachmentService>();
        service.Setup(s => s.GetContentAsync(It.IsAny<Guid>()))
               .ReturnsAsync(new AttachmentContent(Pdf, "SINV-20260920-0001.pdf", "application/pdf", requiredPermission));
        return service;
    }

    [Fact]
    public async Task The_endpoint_serves_the_file_to_a_caller_holding_the_permission_it_was_filed_with()
    {
        var controller = Controller(Serving("SALES_INVOICE_VIEW").Object, "SALES_INVOICE_VIEW");

        var result = await controller.GetContent(Guid.NewGuid());

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        (file.ContentType, file.FileDownloadName).Should().Be(("application/pdf", "SINV-20260920-0001.pdf"));
        file.FileContents.Should().Equal(Pdf);
        controller.Response.Headers.CacheControl.ToString().Should().Contain("no-store").And.Contain("private");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task The_endpoint_refuses_a_caller_without_that_permission_and_sends_no_bytes()
    {
        var controller = Controller(Serving("SALES_INVOICE_VIEW").Object, "DELIVERY_VIEW", "INVOICE_VIEW");

        var result = await controller.GetContent(Guid.NewGuid());

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refused.Value.Should().NotBeOfType<byte[]>();
    }

    [Fact]
    public async Task A_file_filed_with_no_permission_is_open_to_any_signed_in_user_of_the_organization()
    {
        var controller = Controller(Serving(requiredPermission: null).Object);

        (await controller.GetContent(Guid.NewGuid())).Should().BeOfType<FileContentResult>();
    }

    [Fact]
    public async Task The_endpoint_answers_404_for_a_file_that_is_not_there()
    {
        var service = new Mock<IAttachmentService>();
        service.Setup(s => s.GetContentAsync(It.IsAny<Guid>())).ReturnsAsync((AttachmentContent?)null);

        (await Controller(service.Object, "SALES_INVOICE_VIEW").GetContent(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void The_content_route_and_its_authentication_are_what_the_url_stored_on_the_attachment_says()
    {
        var method = typeof(AttachmentsController).GetMethod(nameof(AttachmentsController.GetContent))!;

        method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false)
              .Cast<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().Single().Template.Should().Be("{uuid:guid}/content");
        typeof(AttachmentsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
              .Should().NotBeEmpty("the whole controller is behind authentication");
        typeof(AttachmentsController).GetCustomAttributes(typeof(RouteAttribute), false)
              .Cast<RouteAttribute>().Single().Template.Should().Be("api/attachments");
    }
}
