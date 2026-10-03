using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Controllers;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// Security audit of <c>api/attachments</c> (attachments hardening). Every test drives the real
/// <see cref="AttachmentsController"/> and the real attachment service through an ASP.NET Core pipeline on Kestrel,
/// with the real <c>TenantContext</c> reading the <c>organizationId</c> / <c>is_super_admin</c> claims, an
/// in-memory database and a throwaway web root on disk. The exception middleware mirrors SMS.API's
/// GlobalExceptionMiddleware, including the exception text it puts in the body, so what a test sees in
/// a response is what a caller of the real API would see.
/// </summary>
public class AttachmentsSecurityAuditTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = "%PDF-1.7\n% audit fixture\n%%EOF"u8.ToArray();

    private readonly Guid _orgA = Guid.NewGuid();
    private readonly Guid _orgB = Guid.NewGuid();
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _dbRoot = new();

    private string _root = null!;
    private string _webRoot = null!;
    private string _uploadsRoot = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    // ── Callers ──────────────────────────────────────────────────────────────

    private sealed record Caller(int UserId, Guid Org, bool SuperAdmin, IReadOnlyCollection<string> Permissions);

    /// <summary>The seeded "Read-Only / Auditor" role (AuthDataSeeder.RolePermissionSeed): view-only everywhere.</summary>
    private static readonly string[] AuditorPermissions =
    [
        PermissionCodes.SUPPLIER_VIEW, PermissionCodes.RFQ_VIEW, PermissionCodes.CONTRACT_VIEW, PermissionCodes.PO_VIEW,
        PermissionCodes.BUDGET_VIEW, PermissionCodes.REQUISITION_VIEW_ALL, PermissionCodes.INVENTORY_VIEW,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.SALES_INVOICE_VIEW,
        PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW,
        PermissionCodes.ALLOCATION_VIEW, PermissionCodes.BOM_VIEW, PermissionCodes.PROD_VIEW, PermissionCodes.SUPPLY_VIEW,
        PermissionCodes.PROD_LEDGER_VIEW, PermissionCodes.AUDIT_LOG_VIEW, PermissionCodes.REPORT_VIEW,
        PermissionCodes.REPORT_EXPORT, PermissionCodes.WORKFLOW_VIEW, PermissionCodes.INTEGRATION_VIEW,
    ];

    /// <summary>The seeded Finance Officer: everything finance, nothing on purchase orders.</summary>
    private static readonly string[] FinanceOfficerPermissions =
    [
        PermissionCodes.INVOICE_VIEW, PermissionCodes.INVOICE_PROCESS, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS,
        PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.SALES_INVOICE_MANAGE, PermissionCodes.CUSTOMER_PAYMENT_VIEW,
        PermissionCodes.CUSTOMER_PAYMENT_RECORD, PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.PRODUCT_LEDGER_VIEW,
        PermissionCodes.RECONCILIATION, PermissionCodes.BUDGET_VIEW, PermissionCodes.BUDGET_MONITOR, PermissionCodes.REPORT_VIEW,
        PermissionCodes.REPORT_EXPORT, PermissionCodes.GRN_FINANCE_APPROVE, PermissionCodes.INTEGRATION_VIEW, PermissionCodes.INTEGRATION_SYNC,
    ];

    /// <summary>The seeded Requester: raises requisitions, sees its own. No purchase-order permission at all.</summary>
    private static readonly string[] RequesterPermissions = [PermissionCodes.REQUISITION_CREATE, PermissionCodes.REQUISITION_VIEW_OWN];

    private Caller AdminOf(Guid org)        => new(1, org, false, PermissionCodes.All);
    private Caller AuditorOf(Guid org)      => new(2, org, false, AuditorPermissions);
    private Caller FinanceOfficerOf(Guid org) => new(3, org, false, FinanceOfficerPermissions);
    private Caller RequesterOf(Guid org)    => new(4, org, false, RequesterPermissions);
    private Caller SuperAdminIn(Guid org)   => new(5, org, true, PermissionCodes.All);

    // ── Host ─────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _root        = Path.Combine(Path.GetTempPath(), "att-audit-" + Guid.NewGuid().ToString("N"));
        _webRoot     = Path.Combine(_root, "site", "wwwroot");
        _uploadsRoot = Path.GetFullPath(Path.Combine(_webRoot, "uploads", "attachments"));
        Directory.CreateDirectory(_webRoot);

        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync((IReadOnlyList<int> ids) =>
            (IReadOnlyList<UserIdentity>)ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());

        // Real Kestrel on a loopback port (this project has no TestHost package): request-size limits apply as in production.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.Combine(_root, "site"),
            WebRootPath     = _webRoot,
            EnvironmentName = "Production"
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddRouting();
        services.AddControllers(options =>
        {
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
        }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheAttachmentsController()));

        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionClaimPolicyProvider>();

        services.AddTenantContext();
        services.AddDbContext<WorkflowDbContext>(o => o
            .UseInMemoryDatabase(_dbName, _dbRoot)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddSingleton(users.Object);

        var app = builder.Build();
        app.Use(MirrorOfGlobalExceptionMiddleware);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        _app = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// SMS.API's GlobalExceptionMiddleware as fixed by the lead (2026-10-02): the same status mapping; only the
    /// application's own exceptions carry their text in the body, anything else is "An unexpected error occurred."
    /// with a reference. (Before the fix every body, 500s included, echoed exception.Message — which is how this
    /// audit's leak test first saw the web root path.)
    /// </summary>
    private static async Task MirrorOfGlobalExceptionMiddleware(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            var (status, message, own) = ex switch
            {
                NotFoundException              => (HttpStatusCode.NotFound, ex.Message, true),
                ConflictException              => (HttpStatusCode.Conflict, ex.Message, true),
                BadRequestException            => (HttpStatusCode.BadRequest, ex.Message, true),
                ForbiddenException             => (HttpStatusCode.Forbidden, ex.Message, true),
                UnauthorizedException          => (HttpStatusCode.Unauthorized, ex.Message, true),
                UnprocessableEntityException   => ((HttpStatusCode)422, ex.Message, true),
                _                              => (HttpStatusCode.InternalServerError, $"An unexpected error occurred. Reference: {context.TraceIdentifier}.", false)
            };
            context.Response.StatusCode  = (int)status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(own
                ? JsonSerializer.Serialize(new { success = false, message, result = new { exceptionMessage = ex.Message, exceptionMessageDetail = ex.InnerException?.Message } })
                : JsonSerializer.Serialize(new { success = false, message, result = (object?)null }));
        }
    }

    // ── Requests ─────────────────────────────────────────────────────────────

    private static void Sign(HttpRequestMessage request, Caller who)
    {
        request.Headers.Add("X-User", who.UserId.ToString());
        request.Headers.Add("X-Org", who.Org.ToString());
        request.Headers.Add("X-SuperAdmin", who.SuperAdmin ? "true" : "false");
        if (who.Permissions.Count > 0) request.Headers.Add("X-Permissions", string.Join(',', who.Permissions));
    }

    private Task<HttpResponseMessage> Upload(
        Caller who, string interfaceCode, Guid documentId,
        string fileName = "spec-sheet.pdf", string contentType = "application/pdf", byte[]? bytes = null,
        string? notes = null, IDictionary<string, string>? extraFields = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? Pdf);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(interfaceCode), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        if (notes is not null) form.Add(new StringContent(notes), "notes");
        foreach (var (k, v) in extraFields ?? new Dictionary<string, string>()) form.Add(new StringContent(v), k);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/attachments/upload") { Content = form };
        Sign(request, who);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> List(Caller who, string interfaceCode, Guid documentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/attachments?interface={Uri.EscapeDataString(interfaceCode)}&documentId={documentId}");
        Sign(request, who);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Delete(Caller who, Guid uuid)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/attachments/{uuid}");
        Sign(request, who);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Content(Caller who, Guid uuid)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/attachments/{uuid}/content");
        Sign(request, who);
        return _client.SendAsync(request);
    }

    private sealed record Listed(Guid Uuid, string FileName, string FileUrl);

    private static async Task<List<Listed>> ListedIn(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return [];
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) return [];
        return result.EnumerateArray().Select(a => new Listed(
            a.GetProperty("uuid").GetGuid(), a.GetProperty("fileName").GetString() ?? "", a.GetProperty("fileUrl").GetString() ?? "")).ToList();
    }

    private static async Task<Guid> UploadedId(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"the fixture upload must succeed, but the server said: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("result").GetGuid();
    }

    /// <summary>
    /// Files a generated PDF the way the filing modules do (GatePassArchive, SalesInvoiceDocumentArchive): through
    /// the service, in the background-job tenant scope of <paramref name="org"/>, with no HTTP caller.
    /// </summary>
    private async Task<Guid> FileGenerated(Guid org, Guid document, string interfaceCode = "SALES_INVOICE",
                                           string? requiredPermission = PermissionCodes.SALES_INVOICE_VIEW, byte[]? content = null)
    {
        HangfireTenantScope.OrganizationId = org;
        try
        {
            using var scope = _app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
            var stored = await service.StoreGeneratedAsync(new GeneratedAttachmentRequest
            {
                InterfaceCode = interfaceCode, DocumentId = document, FileName = "SINV-20261002-0001.pdf",
                ContentType = "application/pdf", Content = content ?? Pdf, Notes = "Filed when issued",
                RequiredPermission = requiredPermission
            }, 7);
            return stored.Uuid;
        }
        finally
        {
            HangfireTenantScope.OrganizationId = null;
        }
    }

    /// <summary>The table as it really is: no tenant filter, no soft-delete filter.</summary>
    private DbContextOptions<WorkflowDbContext> RawOptions() =>
        new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(_dbName, _dbRoot)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning, CoreEventId.ManyServiceProvidersCreatedWarning)).Options;

    private async Task<(Guid Org, bool IsDelete)?> Row(Guid uuid)
    {
        await using var db = new WorkflowDbContext(RawOptions(), new StaticTenantContext { IsSuperAdmin = true });
        var row = await db.DocumentAttachments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.UUID == uuid);
        return row is null ? null : (row.OrganizationId, row.IsDelete);
    }

    private List<string> FilesOnDisk() =>
        Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToList() : [];

    /// <summary>
    /// Files anywhere except one folder level below an "attachments" folder inside the content root
    /// (wwwroot/uploads/attachments/&lt;kind&gt; today; a private store such as App_Data/attachments/&lt;kind&gt; is fine too).
    /// </summary>
    private List<string> FilesOutsideTheAttachmentFolders()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(_root, "site")) + Path.DirectorySeparatorChar;
        return FilesOnDisk().Where(f =>
        {
            var store = Path.GetDirectoryName(Path.GetDirectoryName(f)!)!;
            return !f.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(store), "attachments", StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    private static bool IsGuid(string? s) => Guid.TryParse(s, out var _);

    private static bool IsClientError(HttpResponseMessage r) => (int)r.StatusCode is >= 400 and < 500 && r.StatusCode != HttpStatusCode.Unauthorized;

    // ══ 1. Permission per action and per document type ═══════════════════════

    [Fact]
    public async Task Audit_an_auditor_cannot_upload_and_nothing_reaches_the_disk_or_the_database()
    {
        var po = Guid.NewGuid();

        var response = await Upload(AuditorOf(_orgA), "PO", po);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Auditor role is view-only");
        FilesOnDisk().Should().BeEmpty("a refused upload must be refused before the file is written");
        (await ListedIn(await List(AdminOf(_orgA), "PO", po))).Should().BeEmpty();
    }

    [Fact]
    public async Task Audit_an_auditor_cannot_delete_an_attachment()
    {
        var po = Guid.NewGuid();
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po));

        var response = await Delete(AuditorOf(_orgA), uploaded);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Auditor role is view-only");
        (await Row(uploaded))!.Value.IsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Audit_without_the_document_types_permission_a_user_cannot_list_its_attachments()
    {
        var po = Guid.NewGuid();
        await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "po-terms-secret.pdf"));

        foreach (var outsider in new[] { RequesterOf(_orgA), FinanceOfficerOf(_orgA) })
        {
            var response = await List(outsider, "PO", po);

            IsClientError(response).Should().BeTrue($"a caller without PO_VIEW must be refused, got {(int)response.StatusCode}");
            (await response.Content.ReadAsStringAsync()).Should().NotContain("po-terms-secret");
        }
    }

    [Fact]
    public async Task Audit_without_the_document_types_permission_a_user_cannot_upload_or_delete_its_attachments()
    {
        var po = Guid.NewGuid();
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po));
        var before = FilesOnDisk().Count;

        var upload = await Upload(FinanceOfficerOf(_orgA), "PO", po);
        var delete = await Delete(FinanceOfficerOf(_orgA), uploaded);

        IsClientError(upload).Should().BeTrue($"uploading onto a PO needs a PO permission, got {(int)upload.StatusCode}");
        IsClientError(delete).Should().BeTrue($"deleting a PO's attachment needs a PO permission, got {(int)delete.StatusCode}");
        FilesOnDisk().Should().HaveCount(before, "the refused upload wrote nothing");
        (await Row(uploaded))!.Value.IsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Audit_without_the_document_types_permission_a_user_cannot_download_a_filed_copy()
    {
        // A filed copy whose filing module named no permission of its own is still a document of its type.
        var po = Guid.NewGuid();
        var filed = await FileGenerated(_orgA, po, interfaceCode: "PO", requiredPermission: null);

        var response = await Content(RequesterOf(_orgA), filed);

        IsClientError(response).Should().BeTrue($"a caller without PO_VIEW must not get a PO's document, got {(int)response.StatusCode}");
        (await response.Content.ReadAsByteArrayAsync()).Should().NotEqual(Pdf);
    }

    [Fact]
    public async Task Audit_a_requester_cannot_remove_a_file_somebody_else_attached_to_a_requisition()
    {
        // The Requester role is "raise purchase requisitions, view own order status": REQUISITION_CREATE must not make
        // it the editor of every requisition in the organization, or of the evidence an approver attached to one.
        var pr = Guid.NewGuid();
        var approversFile = await UploadedId(await Upload(AdminOf(_orgA), "PR", pr, fileName: "approval-evidence.pdf"));

        var response = await Delete(RequesterOf(_orgA), approversFile);

        response.IsSuccessStatusCode.Should().BeFalse("a requester may take back its own upload, not somebody else's");
        (await Row(approversFile))!.Value.IsDelete.Should().BeFalse();
    }

    [Theory]
    [InlineData("NOT_A_DOCUMENT_TYPE")]
    [InlineData("po")]
    [InlineData("WORKFLOW_DEFINITION")]
    public async Task Audit_an_unknown_document_type_is_refused_even_to_a_caller_holding_every_permission(string interfaceCode)
    {
        var document = Guid.NewGuid();

        var upload = await Upload(AdminOf(_orgA), interfaceCode, document);
        var list   = await List(AdminOf(_orgA), interfaceCode, document);

        IsClientError(upload).Should().BeTrue($"an unknown type has no policy, so it must not default to allowed (upload got {(int)upload.StatusCode})");
        IsClientError(list).Should().BeTrue($"an unknown type has no policy, so it must not default to allowed (list got {(int)list.StatusCode})");
        FilesOnDisk().Should().BeEmpty();
    }

    // ══ 2. Tenancy, IDOR and the super admin ══════════════════════════════════

    [Fact]
    public async Task Audit_another_organizations_attachment_is_a_404_for_list_download_and_delete()
    {
        var po = Guid.NewGuid();
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "org-a-only.pdf"));
        var invoice = Guid.NewGuid();
        var filed = await FileGenerated(_orgA, invoice);
        var outsider = AdminOf(_orgB);

        var list = await List(outsider, "PO", po);
        (await list.Content.ReadAsStringAsync()).Should().NotContain("org-a-only");
        (await ListedIn(list)).Should().BeEmpty();

        (await Content(outsider, filed)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(outsider, uploaded)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(outsider, filed)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Row(uploaded))!.Value.IsDelete.Should().BeFalse();
        (await Row(filed))!.Value.IsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Audit_a_super_admin_acting_in_org_B_never_lists_org_As_attachments()
    {
        var po = Guid.NewGuid();
        await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "org-a-only.pdf"));

        var list = await List(SuperAdminIn(_orgB), "PO", po);

        (await list.Content.ReadAsStringAsync()).Should().NotContain("org-a-only");
        (await ListedIn(list)).Should().BeEmpty("the EF filter's super-admin bypass must not reach the attachment list");
    }

    [Fact]
    public async Task Audit_a_super_admin_acting_in_org_B_cannot_delete_org_As_attachment_or_filed_copy()
    {
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", Guid.NewGuid()));
        var filed = await FileGenerated(_orgA, Guid.NewGuid());

        var deleteUploaded = await Delete(SuperAdminIn(_orgB), uploaded);
        var deleteFiled    = await Delete(SuperAdminIn(_orgB), filed);

        deleteUploaded.StatusCode.Should().Be(HttpStatusCode.NotFound);
        deleteFiled.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Row(uploaded))!.Value.IsDelete.Should().BeFalse("org B's super admin must not reach org A's row");
        (await Row(filed))!.Value.IsDelete.Should().BeFalse("org B's super admin must not reach org A's row");
    }

    [Fact]
    public async Task Audit_a_super_admin_acting_in_org_B_cannot_download_org_As_filed_copy()
    {
        var filed = await FileGenerated(_orgA, Guid.NewGuid());

        var response = await Content(SuperAdminIn(_orgB), filed);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotEqual(Pdf);
    }

    [Fact]
    public async Task Audit_a_super_admin_sees_only_its_own_organizations_logo_under_the_fixed_logo_document_id()
    {
        // po-document-template.component.ts files every organization's PO logo under this one fixed id.
        var logoDocument = Guid.Parse("00000000-0000-0000-0000-000000000001");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
        await UploadedId(await Upload(AdminOf(_orgA), "PO_TEMPLATE_LOGO", logoDocument, fileName: "org-a-logo.png", contentType: "image/png", bytes: png));

        var list = await List(SuperAdminIn(_orgB), "PO_TEMPLATE_LOGO", logoDocument);

        (await ListedIn(list)).Select(l => l.FileName).Should().NotContain("org-a-logo.png",
            "a super admin editing org B's PO template must not be handed org A's logo");
    }

    [Fact]
    public async Task Audit_an_upload_onto_another_organizations_document_id_never_shows_up_on_that_document()
    {
        var po = Guid.NewGuid();
        await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "genuine.pdf"));

        await Upload(AdminOf(_orgB), "PO", po, fileName: "planted-by-org-b.pdf");
        await Upload(SuperAdminIn(_orgB), "PO", po, fileName: "planted-by-super-admin.pdf");

        (await ListedIn(await List(AdminOf(_orgA), "PO", po))).Select(l => l.FileName).Should().Equal("genuine.pdf");
    }

    // ══ 3. Generated (system-filed) copies ════════════════════════════════════

    [Theory]
    [InlineData("SALES_INVOICE", PermissionCodes.SALES_INVOICE_VIEW)]   // SalesInvoiceDocumentArchive
    [InlineData("DELIVERY", PermissionCodes.DELIVERY_VIEW)]             // GatePassArchive — a type whose own uploads DELIVERY_EDIT may remove
    public async Task Audit_a_filed_copy_cannot_be_deleted_through_the_api_by_anyone(string interfaceCode, string requiredPermission)
    {
        var document = Guid.NewGuid();
        var filed = await FileGenerated(_orgA, document, interfaceCode, requiredPermission);

        foreach (var who in new[] { AdminOf(_orgA), SuperAdminIn(_orgA) })
        {
            var response = await Delete(who, filed);
            response.IsSuccessStatusCode.Should().BeFalse($"the system's own filed copy is not a user's file to remove (caller super admin: {who.SuperAdmin})");
            (await Row(filed))!.Value.IsDelete.Should().BeFalse();
        }

        var content = await Content(AdminOf(_orgA), filed);
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        (await content.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
    }

    [Fact]
    public async Task Audit_an_upload_under_the_same_document_never_replaces_or_hides_the_filed_copy()
    {
        var invoice = Guid.NewGuid();
        var filed = await FileGenerated(_orgA, invoice);

        await Upload(AdminOf(_orgA), "SALES_INVOICE", invoice, fileName: "SINV-20261002-0001.pdf",
                     bytes: "%PDF-1.7\n% a forged copy\n%%EOF"u8.ToArray());

        var listed = await ListedIn(await List(AdminOf(_orgA), "SALES_INVOICE", invoice));
        listed.Should().Contain(l => l.Uuid == filed && l.FileUrl == $"/api/attachments/{filed}/content");
        (await (await Content(AdminOf(_orgA), filed)).Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
    }

    // ══ 4. Upload safety ══════════════════════════════════════════════════════

    public static IEnumerable<object[]> TraversingTypes()
    {
        yield return ["../../../../escape"];
        yield return ["..\\..\\..\\..\\escape"];
        yield return [".."];
        yield return ["PO/../../../../escape"];
        yield return ["<absolute>"];
    }

    [Theory]
    [MemberData(nameof(TraversingTypes))]
    public async Task Audit_a_document_type_shaped_like_a_path_cannot_put_a_file_outside_the_attachments_folder(string interfaceCode)
    {
        if (interfaceCode == "<absolute>") interfaceCode = Path.Combine(_root, "absolute-escape");

        var response = await Upload(AdminOf(_orgA), interfaceCode, Guid.NewGuid(), fileName: "x.pdf");

        FilesOutsideTheAttachmentFolders().Should().BeEmpty("the interface code is a client value and must never become a path");
        IsClientError(response).Should().BeTrue($"got {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("statement.html", "text/html")]
    [InlineData("statement.htm", "text/html")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("helper.js", "application/javascript")]
    [InlineData("page.xhtml", "application/xhtml+xml")]
    [InlineData("invoice.pdf.html", "application/pdf")]
    public async Task Audit_a_web_page_or_script_is_refused_because_wwwroot_would_serve_it_from_the_api_origin(string fileName, string contentType)
    {
        var response = await Upload(AdminOf(_orgA), "PO", Guid.NewGuid(), fileName: fileName, contentType: contentType,
                                    bytes: "<html><script>fetch('/api/auth/me')</script></html>"u8.ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        FilesOnDisk().Should().BeEmpty();
    }

    /// <summary>
    /// What a browser will run script in when SMS.API's UseStaticFiles() hands it out: HTML, XHTML, any XML type
    /// (an element in the XHTML namespace inside text/xml or *+xml executes), SVG, script, and the legacy IE/Flash
    /// carriers.
    /// </summary>
    private static bool IsScriptCapable(string contentType)
    {
        var t = contentType.ToLowerInvariant();
        return t.Contains("html") || t == "text/xml" || t == "application/xml" || t.EndsWith("+xml")
            || t.Contains("javascript") || t.Contains("ecmascript") || t.Contains("xsl")
            || t is "text/x-component" or "application/hta" or "message/rfc822" or "application/x-shockwave-flash";
    }

    [Fact]
    public async Task Audit_no_extension_the_static_file_middleware_serves_as_a_script_capable_type_is_accepted()
    {
        // Enumerated from the provider UseStaticFiles() actually uses, so a deny-list cannot silently miss one.
        var dangerous = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().Mappings
            .Where(m => IsScriptCapable(m.Value)).Select(m => m.Key).OrderBy(k => k).ToList();
        dangerous.Should().Contain([".html", ".svg", ".xml"], "sanity: the provider's map is what this test thinks it is");

        var accepted = new List<string>();
        foreach (var ext in dangerous)
        {
            // An innocuous declared type: the extension alone decides how wwwroot serves the file.
            var response = await Upload(AdminOf(_orgA), "PO", Guid.NewGuid(), fileName: "statement" + ext,
                                        contentType: "application/octet-stream",
                                        bytes: "<html xmlns=\"http://www.w3.org/1999/xhtml\"><script>alert(document.domain)</script></html>"u8.ToArray());
            if (response.IsSuccessStatusCode) accepted.Add(ext);
            foreach (var upper in new[] { ext.ToUpperInvariant() }.Where(u => u != ext))
            {
                var shouted = await Upload(AdminOf(_orgA), "PO", Guid.NewGuid(), fileName: "statement" + upper, contentType: "application/octet-stream");
                if (shouted.IsSuccessStatusCode) accepted.Add(upper);
            }
        }

        string.Join(" ", accepted).Should().BeEmpty(
            "each of these is served from the API origin as a type the browser executes script in (checked: {0})", string.Join(" ", dangerous));
        FilesOnDisk().Where(f => dangerous.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Should().BeEmpty();
    }

    [Fact]
    public async Task Audit_a_file_over_20_MB_is_refused_and_nothing_is_written()
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await Upload(AdminOf(_orgA), "PO", Guid.NewGuid(), bytes: new byte[20 * 1024 * 1024 + 1]);
        }
        catch (HttpRequestException)
        {
            // Kestrel answers 413 and closes the connection while the client is still sending: refused.
        }

        if (response is not null) IsClientError(response).Should().BeTrue($"got {(int)response.StatusCode}");
        FilesOnDisk().Should().BeEmpty();
    }

    [Fact(Skip = "Open: see docs/security/attachments.md")]
    public async Task Audit_an_uploaded_file_is_served_only_through_the_checked_content_route_not_as_a_public_static_file()
    {
        // SMS.API runs UseStaticFiles() before authentication, so a url under /uploads is readable by anybody who has it:
        // no sign-in, no organization, no document-type permission. Only /api/attachments/{uuid}/content checks them.
        var po = Guid.NewGuid();
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po));

        var listed = (await ListedIn(await List(AdminOf(_orgA), "PO", po))).Single(l => l.Uuid == uploaded);

        listed.FileUrl.Should().Be($"/api/attachments/{uploaded}/content");
        FilesOnDisk().Should().NotContain(f => f.StartsWith(Path.GetFullPath(_webRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "nothing a user uploads may sit under the web root, where the static-file middleware serves it");
        (await Content(AdminOf(_orgA), uploaded)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Content(AdminOf(_orgB), uploaded)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Content(RequesterOf(_orgA), uploaded)).IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task Audit_the_stored_url_and_disk_name_are_never_taken_from_the_client()
    {
        var po = Guid.NewGuid();

        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "..\\..\\..\\web.config.pdf",
            extraFields: new Dictionary<string, string> { ["fileUrl"] = "/api/attachments/x/content", ["FileUrl"] = "https://evil.example/x.pdf" }));

        var listed = (await ListedIn(await List(AdminOf(_orgA), "PO", po))).Single(l => l.Uuid == uploaded);
        listed.FileUrl.Should().MatchRegex("^/uploads/attachments/po/[0-9a-f-]{36}(\\.[a-z0-9]+)?$|^/api/attachments/[0-9a-f-]{36}/content$");
        FilesOutsideTheAttachmentFolders().Should().BeEmpty();
        var diskNames = FilesOnDisk().Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        diskNames.Should().NotBeEmpty().And.OnlyContain(n => IsGuid(n), "the name on disk is always the server's own GUID");
    }

    [Fact]
    public async Task Audit_an_uploaded_files_display_name_is_reduced_to_a_safe_leaf()
    {
        var po = Guid.NewGuid();

        // CR/LF cannot travel in a multipart header; a tab can, and is as much a control character.
        var uploaded = await UploadedId(await Upload(AdminOf(_orgA), "PO", po, fileName: "..\\..\\evil\t<img src=x onerror=alert(1)>.pdf"));

        var listed = (await ListedIn(await List(AdminOf(_orgA), "PO", po))).Single(l => l.Uuid == uploaded);
        listed.FileName.Should().NotContain("..").And.NotContain("\\").And.NotContain("/").And.NotContain("\t")
            .And.NotContain("<").And.NotContain(">");
    }

    public static IEnumerable<object[]> OverLongMetadata()
    {
        yield return ["notes over the 300-character column", "a.pdf", "application/pdf", new string('n', 301)];
        yield return ["file name over the 255-character column", new string('f', 300) + ".pdf", "application/pdf", null!];
        yield return ["content type over the 100-character column", "a.pdf", "application/" + new string('x', 120), null!];
    }

    [Theory]
    [MemberData(nameof(OverLongMetadata))]
    public async Task Audit_metadata_too_long_for_its_column_is_refused_or_cut_before_it_reaches_sql_server(
        string why, string fileName, string contentType, string? notes)
    {
        _ = why;
        var po = Guid.NewGuid();

        var response = await Upload(AdminOf(_orgA), "PO", po, fileName: fileName, contentType: contentType, notes: notes);

        if (response.IsSuccessStatusCode)
        {
            // On SQL Server this row is a "String or binary data would be truncated" 500 that names the
            // database, schema, table and column in the body, with the file already orphaned on disk.
            await using var db = new WorkflowDbContext(RawOptions(), new StaticTenantContext { IsSuperAdmin = true });
            var row = await db.DocumentAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.DocumentId == po);
            row.FileName.Length.Should().BeLessThanOrEqualTo(255);
            (row.Notes?.Length ?? 0).Should().BeLessThanOrEqualTo(300);
            (row.ContentType?.Length ?? 0).Should().BeLessThanOrEqualTo(100);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            FilesOnDisk().Should().BeEmpty("a refused upload must leave no orphan on disk");
        }
    }

    // ══ 5. Leaks ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("PO|<>")]
    [InlineData("PO\"*?")]
    public async Task Audit_a_bad_document_type_is_a_400_that_names_no_server_path(string interfaceCode)
    {
        var response = await Upload(AdminOf(_orgA), interfaceCode, Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync();

        IsClientError(response).Should().BeTrue($"got {(int)response.StatusCode}: {body}");
        body.Should().NotContainEquivalentOf(_root.Replace("\\", "\\\\")).And.NotContainEquivalentOf(_root)
            .And.NotContainEquivalentOf("wwwroot");
    }

    // ── Test host plumbing (as ReverseEndpointHttpTests) ─────────────────────

    private sealed class OnlyTheAttachmentsController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(AttachmentsController).GetTypeInfo());
        }
    }

    /// <summary>X-User → sub, X-Org → organizationId, X-SuperAdmin → is_super_admin, X-Permissions → permission claims.</summary>
    private sealed class HeaderAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", user.ToString()) };
            if (Request.Headers.TryGetValue("X-Org", out var org)) claims.Add(new Claim("organizationId", org.ToString()));
            if (Request.Headers.TryGetValue("X-SuperAdmin", out var super)) claims.Add(new Claim("is_super_admin", super.ToString()));
            if (Request.Headers.TryGetValue("X-Permissions", out var permissions))
                claims.AddRange(permissions.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class PermissionClaimPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionClaimPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName.StartsWith("Permission:", StringComparison.Ordinal)
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().RequireClaim("permission", policyName["Permission:".Length..]).Build())
                : base.GetPolicyAsync(policyName);
    }
}
