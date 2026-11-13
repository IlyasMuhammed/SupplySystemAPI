using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A50 D-19 / D-57 — an organization's own prefix for a document type (the setup wizard's DOC_NUMBERING).
/// <para>
/// <see cref="DocumentNumberGenerator"/> maps the prefix a caller passes (<see cref="DefaultPrefix"/>, e.g. <c>PO</c>) to
/// <see cref="Prefix"/> (e.g. <c>PUR</c>) for rows with <see cref="IsApplied"/>. Year and the five-digit padding never change:
/// <c>PUR-2026-00001</c>. Each prefix has its own counter, so switching prefix can never reissue a number.
/// </para>
/// <para>
/// Rows for documents that are not numbered by the shared generator (sales invoices, GRNs, payments… — COUNT+1 or their
/// own schemes) are stored with <see cref="IsApplied"/> = false and do nothing (R-3).
/// </para>
/// </summary>
internal class DocumentNumberFormat : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The wizard's document type, e.g. <c>PurchaseOrder</c>. One row per type per organization.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>The prefix the code passes today, e.g. <c>PO</c>.</summary>
    public string DefaultPrefix { get; set; } = string.Empty;

    /// <summary>The organization's prefix, e.g. <c>PUR</c>. 1–10 capital letters or digits.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>The pattern as the admin saw it (informational only).</summary>
    public string? Pattern { get; set; }

    /// <summary>True when the document is numbered by the shared generator, so the prefix takes effect.</summary>
    public bool IsApplied { get; set; }

    public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}
