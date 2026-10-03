using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Files;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Controllers;

/// <summary>
/// The files attached to any document, keyed by interface code + document id. What a caller may do with a
/// document's files is decided per kind of document by <see cref="AttachmentAccessPolicy"/>; a kind it does
/// not name is refused (400). Every read and every removal is limited to the caller's own organization by
/// the service — another organization's file is a 404, for a super admin too.
/// </summary>
[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    // The multipart envelope (boundaries, part headers, the form fields) counts against the request limit,
    // so a file of exactly the size AttachmentUploadRules allows needs a little room above it. Without this,
    // a 20 MB file — which the frontend and the error message both allow — was cut off mid-upload.
    private const long MultipartAllowance = 64 * 1024;

    private readonly IAttachmentService _svc;
    private readonly IWebHostEnvironment _env;

    public AttachmentsController(IAttachmentService svc, IWebHostEnvironment env)
    {
        _svc = svc;
        _env = env;
    }

    /// <summary>
    /// Every kind of document's rule, for the frontend's attachment panel to offer only what this controller
    /// will allow. A sign-in is enough: it is the same for every caller, carries no data, and every page with
    /// an attachment panel reads it.
    /// </summary>
    [HttpGet("policy")]
    public IActionResult GetPolicy() =>
        Ok(ApiResponse<List<AttachmentAccessRuleModel>>.Ok(AttachmentAccessPolicy.Rules
            .Select(r => new AttachmentAccessRuleModel
            {
                InterfaceCode = r.InterfaceCode, View = [.. r.View], Upload = [.. r.Upload],
                Delete = [.. r.Delete], DeleteOwn = [.. r.DeleteOwn]
            })
            .ToList()));

    // Local-disk upload + metadata record in one round trip — same pattern as the existing
    // Invoice attachment upload (SMS.Modules.Finance/Controllers/FinanceController.cs), rather
    // than SMS.FileStore's Azure Blob path, which requires the Azurite emulator locally.
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadRules.MaxFileBytes + MultipartAllowance)]
    public async Task<IActionResult> Upload(
        IFormFile file, [FromForm] string interfaceCode, [FromForm] Guid documentId, [FromForm] string? notes)
    {
        if (string.IsNullOrWhiteSpace(interfaceCode) || documentId == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Both interfaceCode and documentId are required."));

        // Everything is decided before a byte reaches the disk, so a refused upload leaves nothing behind.
        var (rule, refused) = Authorize(interfaceCode, AttachmentAction.Upload);
        if (refused is not null) return refused;

        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));
        if (UploadRules.Refusal(file.FileName, file.Length, file.ContentType,
                picturesOnly: AttachmentAccessPolicy.TakesPicturesOnly(rule!.InterfaceCode)) is { } reason)
            return BadRequest(ApiResponse.Fail(reason));
        if (notes is { Length: > AttachmentService.MaxNotesLength })
            return BadRequest(ApiResponse.Fail($"Notes must not exceed {AttachmentService.MaxNotesLength} characters."));

        // The folder is spelled from the policy's own code — one of a fixed set — never from the request's
        // text, which used to become a path as it came ("../..", or an absolute path, wrote anywhere).
        var folder = rule.InterfaceCode.ToLowerInvariant().Replace('_', '-');
        var uploadsDir = Path.Combine(
            _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            "uploads", "attachments", folder);
        Directory.CreateDirectory(uploadsDir);

        var safeName = $"{Guid.NewGuid()}{UploadRules.DiskExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsDir, safeName);

        await using (var stream = new FileStream(filePath, FileMode.CreateNew))
            await file.CopyToAsync(stream);

        var url = $"/uploads/attachments/{folder}/{safeName}";
        var uuid = await _svc.CreateAsync(new CreateAttachmentRequest
        {
            InterfaceCode = rule.InterfaceCode,
            DocumentId    = documentId,
            FileName      = UploadRules.DisplayName(file.FileName)!,
            FileUrl       = url,
            FileSize      = file.Length,
            ContentType   = UploadRules.ContentTypeOf(file.FileName),
            Notes         = notes
        }, User.GetUserId());

        return Ok(ApiResponse<Guid>.Ok(uuid, "Attachment uploaded."));
    }

    /// <summary>
    /// A document the system generated and filed (see <see cref="IAttachmentService.StoreGeneratedAsync"/>).
    /// Read through here rather than as a static file, so it is only ever served to a signed-in user of
    /// the organization that owns it — one who may see that kind of document, and who also holds the
    /// permission the filing module named.
    /// </summary>
    [HttpGet("{uuid:guid}/content")]
    public async Task<IActionResult> GetContent(Guid uuid)
    {
        var file = await _svc.GetContentAsync(uuid);
        if (file is null)
            return NotFound(ApiResponse.Fail("Attachment not found."));

        var rule = AttachmentAccessPolicy.Find(file.InterfaceCode);
        if (rule is null || !AttachmentAccessPolicy.Allows(User, rule, AttachmentAction.View)
            || (file.RequiredPermission is not null && !User.HasPermission(file.RequiredPermission)))
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

        var (rule, refused) = Authorize(interfaceCode, AttachmentAction.View);
        if (refused is not null) return refused;

        var list = await _svc.GetByDocumentAsync(rule!.InterfaceCode, documentId);

        // Per file, because it can turn on who uploaded it: the panel offers "remove" exactly where this
        // controller's Delete would allow it.
        foreach (var attachment in list)
            attachment.CanRemove = !attachment.IsGenerated && AttachmentAccessPolicy.MayRemove(User, rule, attachment.UploadedBy);

        return Ok(ApiResponse<List<AttachmentModel>>.Ok(list));
    }

    /// <summary>
    /// Removes an uploaded file. A document the system filed is refused with 409 whoever asks — before the
    /// caller's permission is even considered, because no permission makes it removable.
    /// </summary>
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var attachment = await _svc.FindAsync(uuid);
        if (attachment is null)
            return NotFound(ApiResponse.Fail("Attachment not found."));

        if (attachment.IsGenerated)
            return Conflict(ApiResponse.Fail(AttachmentService.FiledDocumentCannotBeRemoved));

        var rule = AttachmentAccessPolicy.Find(attachment.InterfaceCode);
        if (rule is null || !AttachmentAccessPolicy.MayRemove(User, rule, attachment.UploadedBy))
            return Forbidden(AttachmentAction.Delete);

        await _svc.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Attachment removed."));
    }

    /// <summary>
    /// The rule for this kind of document, or why the request stops here: 400 when nothing may attach files to
    /// it (the code is not one the policy names), 403 when this caller may not do this with them.
    /// </summary>
    private (AttachmentAccessRule? Rule, IActionResult? Refused) Authorize(string interfaceCode, AttachmentAction action)
    {
        var rule = AttachmentAccessPolicy.Find(interfaceCode);
        if (rule is null)
            return (null, BadRequest(ApiResponse.Fail("Attachments cannot be kept on this kind of document.")));

        return AttachmentAccessPolicy.Allows(User, rule, action) ? (rule, null) : (rule, Forbidden(action));
    }

    private ObjectResult Forbidden(AttachmentAction action) =>
        StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail(action switch
        {
            AttachmentAction.Upload => "You do not have permission to add attachments to this document.",
            AttachmentAction.Delete => "You do not have permission to remove attachments from this document.",
            _                       => "You do not have permission to see this document's attachments."
        }));
}
