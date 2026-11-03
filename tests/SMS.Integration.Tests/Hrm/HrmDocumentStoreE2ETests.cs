using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 (CORE) — the real IHrmDocumentStore over the real IAttachmentService and SQL Server: every code HRM files under is stored and read
/// back in-process (offer letters file under HRM_LETTER), while api/attachments refuses them even for an Org Admin. Unit tests fake the
/// store, which is how "Attachments cannot be kept on this kind of document." reached the real host.
/// </summary>
public sealed class HrmDocumentStoreE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.7\n% hrm\n%%EOF"u8.ToArray();
    private readonly SapWebApplicationFactory _factory;
    private readonly SapKit _root;

    public HrmDocumentStoreE2ETests(SapWebApplicationFactory factory)
    {
        _factory = factory;
        _root = new SapKit(factory, "A49D");
    }

    private async Task<Guid> StoreAsync(Guid org, string code, Guid document)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHrmDocumentStore>();
        return await store.StorePdfAsync(new HrmCaller(org, 1, [], "Test"), code, document, "doc.pdf", Pdf, PermissionCodes.HRM_LETTER_ISSUE);
    }

    private async Task<byte[]?> ReadAsync(Guid attachment)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IHrmDocumentStore>().ReadAsync(attachment))?.Content;
    }

    [Theory]
    [InlineData(HrmAttachmentCodes.Payslip)]
    [InlineData(HrmAttachmentCodes.Letter)]
    [InlineData(HrmAttachmentCodes.TaxCertificate)]
    public async Task Every_generated_kind_stores_and_reads_back_and_api_attachments_refuses_it(string code)
    {
        var o = await _root.OrgAsync("HRD", currency: false);
        var document = Guid.NewGuid();

        // The job scope gives the store the organization outside a request, as TenantContext reads it.
        HangfireTenantScope.OrganizationId = o.OrgId;
        Guid uuid;
        try
        {
            uuid = await StoreAsync(o.OrgId, code, document);
            (await ReadAsync(uuid)).Should().Equal(Pdf);
            (await StoreAsync(o.OrgId, code, document)).Should().Be(uuid, "the same bytes on the same document are not filed twice");
        }
        finally
        {
            HangfireTenantScope.OrganizationId = null;
        }

        // Org Admin holds every HRM code, and still gets nothing through the generic endpoint.
        (await o.K.Get($"/api/attachments/{uuid}/content")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await o.K.Get($"/api/attachments?interface={code}&documentId={document}")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await o.K.Delete($"/api/attachments/{uuid}")).Status.Should().Be(HttpStatusCode.Conflict);

        var other = await _root.OrgAsync("HRE", currency: false);
        (await other.K.Get($"/api/attachments/{uuid}/content")).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
