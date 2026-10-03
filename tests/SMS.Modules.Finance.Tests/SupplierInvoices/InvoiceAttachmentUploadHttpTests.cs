using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// POST /api/finance/invoices/{uuid}/attachment/upload writes the file under wwwroot/uploads/invoices and the API
/// serves it back from its own origin, typed by its extension. It used to keep whatever extension the client sent,
/// so an .html, .svg or .hxt "invoice scan" was stored cross-site scripting. Now the shared allow-list
/// (SMS.Shared.Files.UploadRules) decides, before anything touches the disk or the invoice.
/// </summary>
public class InvoiceAttachmentUploadHttpTests : IAsyncLifetime
{
    private static readonly Guid Invoice = Guid.NewGuid();

    private readonly Mock<IInvoiceService> _invoices = new();
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "sms-invoice-upload-" + Guid.NewGuid().ToString("N"));
    private IHost _host = null!;
    private HttpClient _client = null!;

    private string UploadsDir => Path.Combine(_webRoot, "uploads", "invoices");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_webRoot);
        _invoices.Setup(s => s.UploadAttachmentAsync(Invoice, It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(true);

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseWebRoot(_webRoot);
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddRouting();
                services.AddControllers(options =>
                {
                    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
                    options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheInvoicesController()));

                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, AnyOfPermissionPolicyProvider>();

                services.AddSingleton(_invoices.Object);
                services.AddSingleton(Mock.Of<IInvoiceDocumentService>());
                services.AddSingleton(Mock.Of<INotificationService>());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapControllers());
            });
        }).StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task<HttpResponseMessage> UploadAsync(string fileName, string contentType, byte[]? bytes = null, Guid? invoice = null)
    {
        var part = new ByteArrayContent(bytes ?? "%PDF-1.7 scan"u8.ToArray());
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var form = new MultipartFormDataContent { { part, "file", fileName } };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/finance/invoices/{invoice ?? Invoice}/attachment/upload") { Content = form };
        request.Headers.Add("X-User", "42");
        request.Headers.Add("X-Permissions", PermissionCodes.INVOICE_PROCESS);
        return _client.SendAsync(request);
    }

    private string[] StoredFiles() => Directory.Exists(UploadsDir) ? Directory.GetFiles(UploadsDir) : [];

    [Theory]
    [InlineData("invoice.html",  "text/html")]
    [InlineData("invoice.html",  "application/octet-stream")]
    [InlineData("invoice.svg",   "image/svg+xml")]
    [InlineData("invoice.svg",   "image/png")]
    [InlineData("invoice.hxt",   "application/octet-stream")]
    [InlineData("invoice.htm",   "application/octet-stream")]
    [InlineData("invoice.xhtml", "application/octet-stream")]
    [InlineData("invoice.js",    "application/octet-stream")]
    [InlineData("invoice.exe",   "application/octet-stream")]
    [InlineData("invoice",       "application/pdf")]
    [InlineData("invoice.pdf",   "text/html")]          // a "PDF" that says it is a web page is refused, not renamed
    public async Task A_file_a_browser_could_run_as_a_page_is_refused_and_nothing_is_written_or_recorded(string fileName, string contentType)
    {
        var response = await UploadAsync(fileName, contentType, "<html><script>alert(document.cookie)</script></html>"u8.ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{fileName} as {contentType}");
        StoredFiles().Should().BeEmpty("a refused file must leave nothing behind to be served");
        _invoices.Verify(s => s.UploadAttachmentAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData("scan.pdf",     "application/pdf",  ".pdf")]
    [InlineData("SCAN.PDF",     "application/pdf",  ".pdf")]
    [InlineData("photo.png",    "image/png",        ".png")]
    [InlineData("photo.jpg",    "image/jpeg",       ".jpg")]
    [InlineData("lines.xlsx",   "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("letter.docx",  "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")]
    [InlineData("scan.pdf",     "application/octet-stream", ".pdf")]   // browsers send all sorts for an ordinary file
    [InlineData(@"C:\fakepath\..\scan.pdf", "application/pdf", ".pdf")]
    public async Task An_ordinary_document_is_stored_under_a_server_chosen_name_with_its_lower_cased_extension(string fileName, string contentType, string extension)
    {
        string? url = null;
        _invoices.Setup(s => s.UploadAttachmentAsync(Invoice, It.IsAny<string>(), 42)).Callback<Guid, string, int>((_, u, _) => url = u).ReturnsAsync(true);

        var response = await UploadAsync(fileName, contentType);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{fileName} as {contentType}");
        url.Should().MatchRegex($@"^/uploads/invoices/[0-9a-f]{{8}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{12}}\{extension}$");
        var stored = StoredFiles().Should().ContainSingle().Subject;
        Path.GetFileName(stored).Should().Be(Path.GetFileName(url!));
        (await File.ReadAllBytesAsync(stored)).Should().Equal("%PDF-1.7 scan"u8.ToArray());
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var response = await UploadAsync("scan.pdf", "application/pdf", []);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _invoices.Verify(s => s.UploadAttachmentAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_invoice_is_a_404()
    {
        _invoices.Setup(s => s.UploadAttachmentAsync(It.Is<Guid>(g => g != Invoice), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);

        (await UploadAsync("scan.pdf", "application/pdf", invoice: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Test host plumbing (as ReverseEndpointHttpTests) ─────────────────────

    private sealed class OnlyTheInvoicesController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(InvoicesController).GetTypeInfo());
        }
    }

    private sealed class HeaderAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", user.ToString()) };
            if (Request.Headers.TryGetValue("X-Permissions", out var permissions))
                claims.AddRange(permissions.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class AnyOfPermissionPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public AnyOfPermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            RequirePermissionAttribute.CodesOf(policyName) is { Count: > 0 } codes
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => codes.Any(context.User.HasPermission))
                    .Build())
                : base.GetPolicyAsync(policyName);
    }
}
