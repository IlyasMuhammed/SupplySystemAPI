namespace SMS.Shared.Files;

/// <summary>
/// What a file uploaded by a user must be before it is written to disk and recorded — for every upload
/// endpoint in the app (document attachments, invoice scans, supplier documents, the public portals). Checked
/// <em>before</em> anything touches the disk, so a refused file leaves nothing behind.
/// <para>
/// <b>Why an allow-list.</b> An uploaded file is served back as a static file from the API's own origin,
/// typed by its extension. The static file middleware knows several hundred extensions, and more than a
/// dozen of them (<c>.html</c>, <c>.svg</c>, <c>.hxt</c>, <c>.xsd</c>, <c>.htc</c>…) come back as something a browser
/// runs or renders as a page of that origin. A list of what to refuse cannot keep up with that, so this is
/// a list of what to accept; and the content type a file is recorded with comes from the same list, never
/// from the client.
/// </para>
/// <para>
/// <b>Never the client's name on disk.</b> The stored name is a server-chosen GUID plus
/// <see cref="DiskExtension"/>; the client's name is only ever shown, after <see cref="DisplayName"/>.
/// </para>
/// </summary>
public static class UploadRules
{
    /// <summary>The largest file accepted (20 MB) — the figure the frontend checks and the error message names.</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>The usual width of a file-name column.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>What may be uploaded, by extension, and the content type each is recorded and served as.</summary>
    private static readonly Dictionary<string, string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"]  = "application/pdf",
        [".png"]  = "image/png",
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"]  = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"]  = "image/bmp",
        [".tif"]  = "image/tiff",
        [".tiff"] = "image/tiff",
        [".heic"] = "image/heic",
        [".doc"]  = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"]  = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"]  = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"]  = "application/vnd.oasis.opendocument.text",
        [".ods"]  = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"]  = "application/vnd.oasis.opendocument.presentation",
        [".rtf"]  = "application/rtf",
        [".csv"]  = "text/csv",
        [".txt"]  = "text/plain",
        [".msg"]  = "application/vnd.ms-outlook",
        [".zip"]  = "application/zip",
        [".7z"]   = "application/x-7z-compressed",
        [".rar"]  = "application/vnd.rar",
        [".dwg"]  = "image/vnd.dwg",
        [".dxf"]  = "image/vnd.dxf",
    };

    /// <summary>Pictures every browser draws in an <c>&lt;img&gt;</c>.</summary>
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    /// <summary>
    /// What a client may say a file is that makes it a page or a script, whatever its name. The client's type
    /// is otherwise not trusted (see <see cref="ContentTypeOf"/>) — browsers send all sorts for an ordinary
    /// file — but one that announces itself as active content is refused rather than renamed.
    /// </summary>
    private static readonly HashSet<string> ActiveContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml", "text/xsl",
        "application/xslt+xml", "application/javascript", "text/javascript", "application/ecmascript",
        "text/ecmascript", "application/x-javascript", "text/x-component", "application/hta",
        "message/rfc822", "application/x-shockwave-flash"
    };

    // Windows' invalid file-name characters, spelled out rather than asked of the OS (Linux would allow all
    // but '/'), so a name is cleaned the same wherever this runs — and "<" and ">" never reach a page.
    private static readonly HashSet<char> NeverInAName = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Why this upload is refused, or <c>null</c> when it may be written. <paramref name="picturesOnly"/> is
    /// for a file the app shows straight from its URL in an <c>&lt;img&gt;</c> (a product image, a logo).
    /// </summary>
    public static string? Refusal(string? fileName, long length, string? contentType, bool picturesOnly = false)
    {
        if (length <= 0)
            return "No file was provided.";
        if (length > MaxFileBytes)
            return "File size must not exceed 20 MB.";
        if (Cleaned(fileName) is null)
            return "The file has no usable name.";

        var mediaType = (contentType ?? string.Empty).Split(';', 2)[0].Trim();
        if (ActiveContentTypes.Contains(mediaType) || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            return "Web pages, SVG images and scripts cannot be uploaded. Save the file as a PDF or an image first.";

        var extension = Extension(fileName);
        if (picturesOnly)
            return Pictures.Contains(extension) ? null : "Only a PNG, JPEG, GIF, WebP or BMP picture can be used here.";

        return Accepted.ContainsKey(extension)
            ? null
            : "This type of file cannot be uploaded. Use a PDF, an image, an Office or OpenDocument file, a text or CSV file, an Outlook message, a drawing or an archive.";
    }

    /// <summary>
    /// The extension the stored file gets after its server-chosen GUID name: an accepted one, lower-cased;
    /// otherwise none at all (a file with no extension is never served by the static file middleware).
    /// </summary>
    public static string DiskExtension(string? fileName)
    {
        var extension = Extension(fileName);
        return Accepted.ContainsKey(extension) ? extension.ToLowerInvariant() : string.Empty;
    }

    /// <summary>The content type a file is recorded as — decided by its extension, never by what the client said.</summary>
    public static string ContentTypeOf(string? fileName) =>
        Accepted.TryGetValue(Extension(fileName), out var type) ? type : "application/octet-stream";

    /// <summary>
    /// The name a file is shown under: the leaf only, with no path, control or other character a file name
    /// cannot hold, cut to <paramref name="maxLength"/> keeping its extension. <c>null</c> when nothing usable is left.
    /// </summary>
    public static string? DisplayName(string? fileName, int maxLength = MaxFileNameLength)
    {
        var cleaned = Cleaned(fileName);
        if (cleaned is null || cleaned.Length <= maxLength)
            return cleaned;

        // Cut from the middle of a long name, not its end, so it is still a ".pdf".
        var extension = Path.GetExtension(cleaned);
        return extension.Length is > 0 and <= 16 && extension.Length < maxLength
            ? cleaned[..(maxLength - extension.Length)] + extension
            : cleaned[..maxLength];
    }

    private static string? Cleaned(string? fileName)
    {
        var cleaned = new string(Leaf(fileName).Where(c => !char.IsControl(c) && !NeverInAName.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) || cleaned.Trim('.').Length == 0 ? null : cleaned;
    }

    // The final extension of the cleaned leaf, trimmed: "x.html " is ".html", as Windows would store it.
    private static string Extension(string? fileName) =>
        Path.GetExtension(Cleaned(fileName) ?? string.Empty).Trim();

    // Both separators, whatever the server's own: a name from a Windows browser arrives with backslashes.
    private static string Leaf(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        var cut = name.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? name : name[(cut + 1)..];
    }
}
