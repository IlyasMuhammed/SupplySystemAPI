using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// Security audit of the anonymous RFQ portal upload (POST api/public/rfq-portal/{token}/attachments), which files
/// a vendor's documents as RFQ_RESPONSE attachments under wwwroot/uploads/attachments/rfq-response — served back by
/// SMS.API's UseStaticFiles() from the API's own origin, typed by extension, to anybody.
/// </summary>
public class RfqPortalAttachmentSecurityAuditTests : IDisposable
{
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "rfq-audit-" + Guid.NewGuid().ToString("N"), "wwwroot");
    private readonly Mock<IAttachmentService> _attachments = new();

    public RfqPortalAttachmentSecurityAuditTests()
    {
        Directory.CreateDirectory(_webRoot);
        _attachments.Setup(a => a.CreateAsync(It.IsAny<CreateAttachmentRequest>(), It.IsAny<int>())).ReturnsAsync(Guid.NewGuid());
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_webRoot)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private RfqPortalController Portal()
    {
        var validation = new Mock<IRfqLinkValidationService>();
        validation.Setup(v => v.ValidateAsync(It.IsAny<string>()))
                  .ReturnsAsync(new ValidationResult.Valid(new RfqPublicPayload { QuotationNumber = "RFQ-1", Title = "Steel" }));
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_webRoot);

        return new RfqPortalController(validation.Object, Mock.Of<IRfqSubmissionService>(), _attachments.Object, env.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static IFormFile File(string name, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };
    }

    private List<string> FilesOnDisk() =>
        Directory.EnumerateFiles(Path.GetDirectoryName(_webRoot)!, "*", SearchOption.AllDirectories).ToList();

    [Theory(Skip = "Open: see docs/security/attachments.md")]
    [InlineData("quote.html", "text/html")]
    [InlineData("quote.hxt", "application/octet-stream")]   // text/html in the static-file map
    [InlineData("quote.svg", "image/svg+xml")]
    [InlineData("quote.xsd", "application/octet-stream")]   // text/xml: XHTML-namespace script runs
    [InlineData("quote.js", "application/javascript")]
    public async Task Audit_an_anonymous_vendor_cannot_plant_a_web_page_or_script_on_the_api_origin(string name, string contentType)
    {
        var page = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><script>alert(document.domain)</script></html>"u8.ToArray();

        var result = await Portal().UploadAttachment("token", File(name, contentType, page), Guid.NewGuid());

        result.Should().BeOfType<BadRequestObjectResult>("the same upload rules as api/attachments must apply to the public portal");
        FilesOnDisk().Should().BeEmpty();
        _attachments.Verify(a => a.CreateAsync(It.IsAny<CreateAttachmentRequest>(), It.IsAny<int>()), Times.Never);
    }
}
