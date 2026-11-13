using Microsoft.AspNetCore.Hosting;
using SMS.Shared.Onboarding;

namespace SMS.WorkflowEngine.Services;

/// <summary>
/// A50 TPL (D-25) — the bytes of an uploaded picture (a document template's logo, interface code DOC_TEMPLATE_LOGO) for the
/// PDF renderers. Uploads of pictures live on disk under wwwroot (their FileUrl is web-relative), generated files in the
/// database; both are read in-process, never over HTTP. Only an attachment of the expected interface code is returned, so
/// a template cannot be pointed at somebody else's file.
/// </summary>
internal sealed class DocumentAssetReader : IDocumentAssetReader
{
    private readonly IAttachmentService _attachments;
    private readonly IWebHostEnvironment? _env;

    public DocumentAssetReader(IAttachmentService attachments, IWebHostEnvironment? env = null)
    {
        _attachments = attachments;
        _env = env;
    }

    public async Task<DocumentAsset?> ReadAsync(Guid attachmentUuid, string interfaceCode, CancellationToken ct = default)
    {
        var found = await _attachments.FindAsync(attachmentUuid);
        if (found is null || !string.Equals(found.InterfaceCode, interfaceCode, StringComparison.Ordinal)) return null;

        var stored = await _attachments.GetContentAsync(attachmentUuid);
        if (stored is not null) return new DocumentAsset(stored.Content, stored.ContentType, found.FileUrl);

        if (string.IsNullOrWhiteSpace(found.FileUrl) || !found.FileUrl.StartsWith('/') || found.FileUrl.StartsWith("/api/")) return null;

        var webRoot  = _env?.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, found.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

        // Never outside wwwroot, whatever the stored URL says. The root carries a trailing separator so a sibling folder
        // such as "wwwroot2" does not pass a prefix test (REV50-14).
        var root = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;

        return new DocumentAsset(await File.ReadAllBytesAsync(fullPath, ct), found.ContentType, found.FileUrl);
    }
}
