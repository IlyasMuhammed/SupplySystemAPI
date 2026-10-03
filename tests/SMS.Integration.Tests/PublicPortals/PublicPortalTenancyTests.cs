using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.PublicPortals;

/// <summary>
/// The real host, one LocalDB database, and a second organization (ENTERPRISE) whose admin has signed in — the
/// seeded admin's own organization is SCM-DEMO, the one an anonymous request falls back to. Uploads land in a
/// throwaway web root, never in src/SMS.API/wwwroot.
/// <para>
/// <b>Built on first use, not by xUnit.</b> While these tests are skipped (see docs/security/public-portals.md) a
/// full run must not pay for a LocalDB database it never uses — nor construct a <see cref="SapWebApplicationFactory"/>,
/// whose constructor and dispose set and clear process-wide environment variables other hosts read.
/// </para>
/// </summary>
public sealed class PublicPortalFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SapWebApplicationFactory? _host;
    private bool _ready;

    public SapWebApplicationFactory Host => _host ?? throw new InvalidOperationException("Call EnsureReadyAsync first.");

    /// <summary>Where the portal writes uploads for this run.</summary>
    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "sms-portal-e2e-" + Guid.NewGuid().ToString("N"), "wwwroot");

    public Guid OrganizationId { get; private set; }

    internal SapKit Org  { get; private set; } = null!;
    internal SapKit Root { get; private set; } = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task EnsureReadyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_ready) return;

            if (_host is null)
            {
                _host = new SapWebApplicationFactory();
                await _host.InitializeAsync();
            }

            Root = new SapKit(_host, "PP");

            // Every portal call below resolves the web root at request time, so pointing the host's environment at
            // a temporary folder keeps test uploads out of the source tree.
            Directory.CreateDirectory(WebRoot);
            _host.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath = WebRoot;

            var email = $"portal-org-{Guid.NewGuid():N}@portal-e2e.test";
            var created = await Root.Ok(Root.Post("/api/system/organizations", new
            {
                OrgCode = $"P{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"Portal Org {Guid.NewGuid():N}"[..30], Plan = "ENTERPRISE",
                AdminFirstName = "Portal", AdminLastName = "Admin", AdminEmail = email
            }), "create a second organization");

            OrganizationId = created.G("organizationId");
            OrganizationId.Should().NotBe(TenantDefaults.ScmDemoOrganizationId);
            OrganizationId.Should().NotBe(_host.OrganizationId);

            await _host.SetPasswordAsync(email, "Portal@12345!");
            await _host.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
            Org = new SapKit(_host, "PO", _host.CreateBearerClient(await _host.LoginAsync(email, "Portal@12345!")));

            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(WebRoot)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The public portals write on behalf of the organization that owns the link — not the one an anonymous request
/// happens to default to.
/// <para>
/// An anonymous request has no signed-in user, so <c>TenantContext</c> reports SCM-DEMO as its organization and
/// bypasses the tenant filter. Every row such a request added without naming its organization was stamped SCM-DEMO:
/// a vendor's quote and spec sheets for another organization's RFQ landed in SCM-DEMO, invisible to the people who
/// sent the RFQ (and visible to SCM-DEMO's), and a supplier's return acknowledgement went into SCM-DEMO's audit trail.
/// </para>
/// <para>
/// <b>Skipped: the fix is a separate, later project.</b> Every test here was run against the current code on
/// 2026-10-02 and failed for exactly the reason it names (the rows came back stamped SCM-DEMO; the portal accepted
/// .html, .svg, .hxt, .xsd, .js, .exe and a text/html "PDF"). They are kept as the proof, and as the acceptance
/// tests for that fix: remove <see cref="Open"/> from each attribute once it lands.
/// </para>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~PublicPortalTenancyTests</c>.</para>
/// </summary>
public sealed class PublicPortalTenancyTests : IClassFixture<PublicPortalFixture>
{
    /// <summary>Why these are skipped, and where the open work is described.</summary>
    private const string Open = "Open: see docs/security/public-portals.md";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PublicPortalFixture _p;
    private SapWebApplicationFactory F => _p.Host;
    private SapKit Org => _p.Org;

    public PublicPortalTenancyTests(PublicPortalFixture fixture) => _p = fixture;

    // ── RFQ portal ───────────────────────────────────────────────────────────────

    [Fact(Skip = Open)]
    public async Task A_vendors_quote_and_files_through_the_rfq_portal_belong_to_the_rfqs_organization()
    {
        await _p.EnsureReadyAsync();
        var (quotation, token) = await SendRfqAsync();
        using var vendor = F.CreateAnonymousClient();

        var opened = await PortalAsync(vendor, HttpMethod.Get, $"api/public/rfq-portal/{token}");
        opened.S("status").Should().Be("VALID");
        var lineUuid = opened.P("payload").A("lines").Single().G("lineUuid");

        var responseUuid = Guid.NewGuid();
        var upload = await UploadAsync(vendor, token, responseUuid, "spec-sheet.pdf", "application/pdf", Pdf);
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());

        var submitted = await PortalAsync(vendor, HttpMethod.Post, $"api/public/rfq-portal/{token}/submit", new
        {
            ResponseUuid = responseUuid,
            Notes        = "Ex-works, 30 days credit",
            Lines        = new[] { new { LineUuid = lineUuid, UnitPrice = "12.50", DeliveryDays = "7", CanSupply = true } }
        });
        submitted.S("status").Should().Be("SUBMITTED", J.Short(submitted));

        // What was stored, and under whom.
        (await OrganizationsOf("SELECT OrganizationId FROM demand.vendor_responses WHERE UUID = @r", responseUuid))
            .Should().Equal(new[] { _p.OrganizationId }, "the vendor's response belongs to the organization that sent the RFQ, not SCM-DEMO");
        (await OrganizationsOf(
                "SELECT l.OrganizationId FROM demand.vendor_response_lines l JOIN demand.vendor_responses r ON r.Id = l.VendorResponseId WHERE r.UUID = @r",
                responseUuid))
            .Should().Equal(new[] { _p.OrganizationId }, "so do its lines");
        (await OrganizationsOf("SELECT OrganizationId FROM workflow_schema.document_attachments WHERE DocumentId = @r", responseUuid))
            .Should().Equal(new[] { _p.OrganizationId }, "so does the spec sheet the vendor attached");

        // What the RFQ's own people see.
        (await Org.Ok(Org.Get($"/api/quotations/{quotation}"), "read the RFQ")).I("submittedResponseCount")
            .Should().Be(1, "the RFQ's owner sees the response arrive");
        await Org.Ok(Org.Post($"/api/quotations/{quotation}/open-bids"), "open the bids — refused when no response is visible");
        var bids = await Org.Ok(Org.Get($"/api/quotations/{quotation}/comparison"), "compare the bids");
        bids.Items().Should().ContainSingle().Which.D("totalAmount").Should().Be(62.5m);

        var files = await Org.Ok(Org.Get($"/api/attachments?interface=RFQ_RESPONSE&documentId={responseUuid}"), "list the response's files");
        files.Items().Select(f => f.S("fileName")).Should().Equal("spec-sheet.pdf");

        // …and nobody else does, the platform admin's own organization (SCM-DEMO) included.
        var elsewhere = await _p.Root.Ok(_p.Root.Get($"/api/attachments?interface=RFQ_RESPONSE&documentId={responseUuid}"), "SCM-DEMO lists them");
        elsewhere.Items().Should().BeEmpty("another organization's quote documents are not SCM-DEMO's");
    }

    [Theory(Skip = Open)]
    [InlineData("quote.html", "text/html")]
    [InlineData("quote.htm",  "text/html")]
    [InlineData("quote.svg",  "image/svg+xml")]
    [InlineData("quote.hxt",  "application/octet-stream")]   // text/html in the static file map
    [InlineData("quote.xsd",  "application/octet-stream")]   // text/xml: an XHTML-namespace script runs
    [InlineData("quote.js",   "application/javascript")]
    [InlineData("quote.exe",  "application/octet-stream")]
    [InlineData("quote.pdf",  "text/html")]                  // the right extension, but the browser said it was a web page
    [InlineData("logo.png",   "image/svg+xml")]
    public async Task The_rfq_portal_refuses_an_upload_a_browser_would_run_as_a_page(string fileName, string contentType)
    {
        await _p.EnsureReadyAsync();
        var (_, token) = await SendRfqAsync();
        using var vendor = F.CreateAnonymousClient();
        var responseUuid = Guid.NewGuid();
        var before = FilesInWebRoot();

        var upload = await UploadAsync(vendor, token, responseUuid, fileName, contentType, Page);

        upload.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{fileName} as {contentType} — {await upload.Content.ReadAsStringAsync()}");
        FilesInWebRoot().Should().BeEquivalentTo(before, "a refused file never reaches the disk");
        (await F.QueryAsync("SELECT Id FROM workflow_schema.document_attachments WHERE DocumentId = @r", ("@r", responseUuid)))
            .Should().BeEmpty("nor is it recorded");
    }

    [Theory(Skip = Open)]
    [InlineData("spec.pdf",       "application/pdf")]
    [InlineData("photo.png",      "image/png")]
    [InlineData("photo.jpg",      "image/jpeg")]
    [InlineData("prices.xlsx",    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("terms.docx",     "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("SCAN 01.PDF",    "application/pdf")]
    public async Task The_rfq_portal_accepts_documents_and_pictures(string fileName, string contentType)
    {
        await _p.EnsureReadyAsync();
        var (_, token) = await SendRfqAsync();
        using var vendor = F.CreateAnonymousClient();
        var responseUuid = Guid.NewGuid();

        var upload = await UploadAsync(vendor, token, responseUuid, fileName, contentType, BytesFor(fileName));

        upload.StatusCode.Should().Be(HttpStatusCode.OK, $"{fileName} — {await upload.Content.ReadAsStringAsync()}");
        var rows = await F.QueryAsync(
            "SELECT OrganizationId, FileName, FileUrl FROM workflow_schema.document_attachments WHERE DocumentId = @r", ("@r", responseUuid));
        var row = rows.Should().ContainSingle().Subject;
        row["OrganizationId"].Should().Be(_p.OrganizationId);
        row["FileName"].Should().Be(fileName);

        var stored = Path.Combine(_p.WebRoot, ((string)row["FileUrl"]!).TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        File.Exists(stored).Should().BeTrue($"the file is kept at {row["FileUrl"]}");
        Path.GetExtension(stored).Should().Be(Path.GetExtension(fileName).ToLowerInvariant(), "it keeps its own (lower-cased) extension");
    }

    // ── SRO acknowledgement portal ───────────────────────────────────────────────

    [Fact(Skip = Open)]
    public async Task A_suppliers_return_acknowledgement_through_the_sro_portal_belongs_to_the_returns_organization()
    {
        await _p.EnsureReadyAsync();
        var vendor = await Org.CreateVendorAsync("Return Vendor");
        var sro = (await Org.Ok(Org.Post("/api/sros", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, SroType = "POST_RECEIPT_DEFECT", ReturnReason = "DAMAGED",
            Lines = new[] { new { ItemDescription = "Bent bracket", UnitOfMeasure = "PCS", QtyToReturn = 2m, ReturnReason = "DAMAGED", Condition = "DAMAGED" } }
        }), "create a return")).GetGuid();
        await Org.Ok(Org.Post($"/api/sros/{sro}/approve", new { Notes = "ok" }), "approve the return");
        await Org.Ok(Org.Post($"/api/sros/{sro}/dispatch", new
        {
            RmaNumber = "RMA-1", DispatchDate = DateTime.UtcNow.Date, DispatchCarrier = "TCS", DispatchTrackingRef = "TRK-1"
        }), "dispatch the return");

        var link = (await F.QueryAsync(
            "SELECT l.PortalLinkUrl, l.OrganizationId FROM warehouse.sro_acknowledgment_links l " +
            "JOIN warehouse.supplier_return_orders s ON s.Id = l.ReturnOrderId WHERE s.UUID = @s", ("@s", sro))).Should().ContainSingle().Subject;
        link["OrganizationId"].Should().Be(_p.OrganizationId, "dispatch is a signed-in action of the return's organization");
        var token = ((string)link["PortalLinkUrl"]!).Split('/').Last();

        using var supplier = F.CreateAnonymousClient();
        (await PortalAsync(supplier, HttpMethod.Get, $"api/public/sro-portal/{token}")).S("status").Should().Be("VALID");
        var acknowledged = await PortalAsync(supplier, HttpMethod.Post, $"api/public/sro-portal/{token}/acknowledge", new
        {
            Remarks = "Both brackets received", ReceivedDate = DateTime.UtcNow.Date
        });
        acknowledged.S("status").Should().Be("ACKNOWLEDGED", J.Short(acknowledged));

        (await OrganizationsOf("SELECT OrganizationId FROM reports.audit_logs WHERE EntityId = @r AND Action = 'ACKNOWLEDGE'", sro))
            .Should().Equal(new[] { _p.OrganizationId }, "the supplier's acknowledgement is in the return's organization's audit trail, not SCM-DEMO's");

        (await Org.Ok(Org.Get($"/api/sros/{sro}"), "read the return")).S("status").Should().Be("SUPPLIER_RECEIVED");
        var trail = await Org.Ok(Org.Get($"/api/reports/audit-trail?EntityId={sro}&PageSize=50"), "read the return's audit trail");
        trail.A("data").Select(e => e.S("action")).Should().Contain("ACKNOWLEDGE", "the return's own people see the supplier's acknowledgement");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>A one-line RFQ in the second organization, sent to a new vendor with a portal link; returns the vendor's raw token.</summary>
    private async Task<(Guid Quotation, string Token)> SendRfqAsync()
    {
        var vendor = await Org.CreateVendorAsync("RFQ Vendor");
        var quotation = (await Org.Ok(Org.Post("/api/quotations", new
        {
            Title = Org.Next("Brackets RFQ"), SourceType = "STANDALONE", DueDate = DateTime.UtcNow.Date.AddDays(10),
            Lines = new[] { new { ItemDescription = "Steel bracket", UnitOfMeasure = "PCS", Quantity = 5m } }
        }), "create an RFQ")).GetGuid();

        var sent = await Org.Ok(Org.Post($"/api/quotations/{quotation}/send-with-link", new
        {
            Suppliers = new[] { new { SupplierId = vendor.Uuid, SupplierName = vendor.Name, ContactId = 0 } }
        }), "send the RFQ with a portal link");

        var url = sent.A("links").Single().S("linkUrl")!;
        return (quotation, url.Split('/').Last());
    }

    private static async Task<JsonElement> PortalAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{method} {url} — {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("result").Clone();
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string token, Guid responseUuid, string fileName, string contentType, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(responseUuid.ToString()), "responseUuid");
        return await client.PostAsync($"api/public/rfq-portal/{token}/attachments", form);
    }

    private async Task<List<Guid>> OrganizationsOf(string sql, Guid id) =>
        (await F.QueryAsync(sql, ("@r", id))).Select(r => (Guid)r["OrganizationId"]!).ToList();

    private List<string> FilesInWebRoot() =>
        Directory.EnumerateFiles(_p.WebRoot, "*", SearchOption.AllDirectories).ToList();

    private static readonly byte[] Pdf  = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
    private static readonly byte[] Png  = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] Zip  = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00, 0x00, 0x00];
    private static readonly byte[] Page = Encoding.UTF8.GetBytes(
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(document.domain)</script></svg></html>");

    private static byte[] BytesFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf"            => Pdf,
        ".png"            => Png,
        ".jpg" or ".jpeg" => Jpeg,
        _                 => Zip
    };
}
