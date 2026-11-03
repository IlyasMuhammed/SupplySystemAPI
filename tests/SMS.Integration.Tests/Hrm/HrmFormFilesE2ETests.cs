using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 FE-1 / FE-4 on the real host: a file uploaded for a row that does not exist yet (a client-made documentId) works for every HRM upload
/// kind, reads back by its attachment uuid under the kind's rule, and never lands under wwwroot. The HR scope picker works with
/// HRM_SETTINGS_MANAGE alone.
/// </summary>
public sealed class HrmFormFilesE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.7\n% hr file\n%%EOF"u8.ToArray();
    private readonly SapWebApplicationFactory _factory;
    private readonly SapKit _root;

    public HrmFormFilesE2ETests(SapWebApplicationFactory factory)
    {
        _factory = factory;
        _root = new SapKit(factory, "A49F");
    }

    private static async Task<(HttpStatusCode Status, Guid Uuid)> UploadAsync(HttpClient client, string code, Guid documentId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "scan.pdf");
        form.Add(new StringContent(code), "interfaceCode");
        form.Add(new StringContent(documentId.ToString()), "documentId");
        var response = await client.PostAsync("/api/attachments/upload", form);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, Guid.Empty);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.GetProperty("result").GetGuid());
    }

    [Theory]
    [InlineData("HRM_EMPLOYEE")]
    [InlineData("HRM_LEAVE_REQUEST")]
    [InlineData("HRM_EXPENSE_CLAIM")]
    [InlineData("HRM_CANDIDATE")]
    [InlineData("HRM_ENROLLMENT")]
    public async Task A_file_for_a_row_not_yet_made_uploads_and_reads_back_by_its_uuid(string code)
    {
        var o = await _root.OrgAsync("HFF", currency: false);
        var admin = _factory.CreateBearerClient(await _factory.LoginAsync(o.Email, "A37Qa@12345!"));

        var (status, uuid) = await UploadAsync(admin, code, Guid.NewGuid());
        status.Should().Be(HttpStatusCode.OK);

        await _root.EnableHrmAsync(o);
        var read = await admin.GetAsync($"/api/hrm/attachments/{uuid}/content");
        read.StatusCode.Should().Be(HttpStatusCode.OK, "an unlinked upload reads back to its uploader");
        (await read.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        read.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await admin.GetAsync($"/api/attachments/{uuid}/content")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the generic route cannot check the row");

        // Nothing under the static folder: it would be served to anyone with the URL.
        var folder = code.ToLowerInvariant().Replace('_', '-');
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot", "uploads", "attachments", folder);
        if (Directory.Exists(root)) Directory.GetFiles(root).Should().BeEmpty();

        var nobody = await o.K.LoginWithPermissionsAsync("hfnone", "CURRENCY_VIEW");
        (await o.K.Get($"/api/hrm/attachments/{uuid}/content", nobody)).Status.Should().Be(HttpStatusCode.Forbidden);
        var other = await _root.OrgAsync("HFX", currency: false);
        (await other.K.Get($"/api/attachments/{uuid}/content")).Status.Should().Be(HttpStatusCode.NotFound);
        await _root.EnableHrmAsync(other);
        (await other.K.Get($"/api/hrm/attachments/{uuid}/content")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_row_saved_with_the_attachment_uuid_of_a_generated_document_id_is_accepted()
    {
        var o = await _root.OrgAsync("HFR", currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o);
        var admin = _factory.CreateBearerClient(await _factory.LoginAsync(o.Email, "A37Qa@12345!"));
        var employee = await k.Ok(k.Post("/api/hrm/employees", new { firstName = "Ali", lastName = "Khan" }), "employee");

        var (_, uuid) = await UploadAsync(admin, "HRM_EMPLOYEE", Guid.NewGuid());
        var document = await k.Ok(k.Post($"/api/hrm/employees/{employee.G("uuid")}/documents", new { title = "CNIC scan", documentType = "CNIC", attachmentUuid = uuid }), "document");
        document.G("attachmentUuid").Should().Be(uuid);
    }

    [Fact]
    public async Task The_scope_user_picker_works_with_settings_manage_alone()
    {
        var o = await _root.OrgAsync("HFU", currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o);

        var manager = await k.LoginWithPermissionsAsync("hfset", "HRM_SETTINGS_MANAGE");
        var users = (await k.Ok(k.Get($"/api/hrm/user-scopes/users?search={Uri.EscapeDataString(o.Email)}", manager), "picker")).EnumerateArray().ToList();
        users.Should().ContainSingle();
        users[0].S("email").Should().Be(o.Email);
        users[0].GetProperty("userId").GetInt32().Should().BeGreaterThan(0);
        users[0].S("name").Should().Be("Module Admin");

        var noCode = await k.LoginWithPermissionsAsync("hfno", "HRM_EMPLOYEE_VIEW");
        (await k.Get("/api/hrm/user-scopes/users", noCode)).Status.Should().Be(HttpStatusCode.Forbidden);

        var other = await _root.OrgAsync("HFV", currency: false);
        await _root.EnableHrmAsync(other);
        (await other.K.Ok(other.K.Get($"/api/hrm/user-scopes/users?search={Uri.EscapeDataString(o.Email)}"), "other org")).GetArrayLength().Should().Be(0);
    }
}
