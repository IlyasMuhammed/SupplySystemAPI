using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Controllers;

[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentService _svc;
    private readonly IWebHostEnvironment _env;

    public AttachmentsController(IAttachmentService svc, IWebHostEnvironment env)
    {
        _svc = svc;
        _env = env;
    }

    // Local-disk upload + metadata record in one round trip — same pattern as the existing
    // Invoice attachment upload (SMS.Modules.Finance/Controllers/FinanceController.cs), rather
    // than SMS.FileStore's Azure Blob path, which requires the Azurite emulator locally.
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        IFormFile file, [FromForm] string interfaceCode, [FromForm] Guid documentId, [FromForm] string? notes)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));
        if (string.IsNullOrWhiteSpace(interfaceCode) || documentId == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Both interfaceCode and documentId are required."));

        const long maxBytes = 20 * 1024 * 1024;
        if (file.Length > maxBytes)
            return BadRequest(ApiResponse.Fail("File size must not exceed 20 MB."));

        var folder = interfaceCode.ToLowerInvariant().Replace('_', '-');
        var uploadsDir = Path.Combine(
            _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            "uploads", "attachments", folder);
        Directory.CreateDirectory(uploadsDir);

        var ext      = Path.GetExtension(file.FileName);
        var safeName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(uploadsDir, safeName);

        await using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);

        var url = $"/uploads/attachments/{folder}/{safeName}";
        var uuid = await _svc.CreateAsync(new CreateAttachmentRequest
        {
            InterfaceCode = interfaceCode,
            DocumentId    = documentId,
            FileName      = file.FileName,
            FileUrl       = url,
            FileSize      = file.Length,
            ContentType   = file.ContentType,
            Notes         = notes
        }, User.GetUserId());

        return Ok(ApiResponse<Guid>.Ok(uuid, "Attachment uploaded."));
    }

    /// <summary>
    /// A document the system generated and filed (see <see cref="IAttachmentService.StoreGeneratedAsync"/>).
    /// Read through here rather than as a static file, so it is only ever served to a signed-in user of
    /// the organization that owns it — and only to one holding the permission the filing module named.
    /// </summary>
    [HttpGet("{uuid:guid}/content")]
    public async Task<IActionResult> GetContent(Guid uuid)
    {
        var file = await _svc.GetContentAsync(uuid);
        if (file is null)
            return NotFound(ApiResponse.Fail("Attachment not found."));

        if (file.RequiredPermission is not null && !User.HasPermission(file.RequiredPermission))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("You do not have permission to open this document."));

        // Sensitive and regenerable: never left in a shared cache, never sniffed into another type.
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    // GET /api/attachments?interface={code}&documentId={id}
    [HttpGet]
    public async Task<IActionResult> GetByDocument(
        [FromQuery(Name = "interface")] string interfaceCode,
        [FromQuery] Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(interfaceCode) || documentId == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Both interface and documentId are required."));

        var list = await _svc.GetByDocumentAsync(interfaceCode, documentId);
        return Ok(ApiResponse<List<AttachmentModel>>.Ok(list));
    }

    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        await _svc.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Attachment removed."));
    }
}