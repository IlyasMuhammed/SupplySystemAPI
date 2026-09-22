using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Domain;

/// <summary>
/// The bytes of a document the system generated and filed as an attachment — an issued invoice's PDF,
/// a collection gate pass.
/// <para>
/// <b>In the database, not under <c>wwwroot</c></b>, for the reason Logistics gave for its shipping
/// labels (decision G8) and its proofs of delivery (finding F47): a file under <c>wwwroot</c> is
/// served to anyone holding the URL, and these carry a customer's name and amounts or the ID number
/// of the person collecting goods; and it lasts only until the directory is next replaced by a
/// deploy, leaving an attachment row that points at nothing. Here it is read only through an
/// authenticated, organization-scoped endpoint that also checks the permission the filing module
/// named.
/// </para>
/// <para>
/// A table of its own so that listing a document's attachments — which every attachment panel does —
/// never loads the bytes.
/// </para>
/// </summary>
internal class DocumentAttachmentContent : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid OrganizationId { get; set; }

    public int                DocumentAttachmentId { get; set; }
    public DocumentAttachment DocumentAttachment   { get; set; } = null!;

    public byte[] Content { get; set; } = [];

    /// <summary>SHA-256 of <see cref="Content"/>, lower-case hex. The same bytes filed twice on one document are stored once.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// The permission a caller must hold to read the file, named by the module that filed it —
    /// this service knows nothing of invoices or deliveries. <c>null</c> means any signed-in user of
    /// the organization.
    /// </summary>
    public string? RequiredPermission { get; set; }
}
