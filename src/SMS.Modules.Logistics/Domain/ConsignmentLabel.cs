using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A shipping label the carrier issued, kept so it can be printed again without asking the
/// carrier — and so the label on the box can be proven months later.
/// <para>
/// <b>Stored in the database, not under <c>wwwroot</c></b> (decision G8). Files there are served
/// to anyone with the URL, and a label is the recipient's name, phone number and address. Here it
/// is only ever read through an authenticated, organization-scoped endpoint. Labels are tens to
/// hundreds of kilobytes, and they live in their own table so nothing else ever loads the bytes.
/// </para>
/// <para>
/// <b>Tied to an airway bill.</b> A consignment booked again gets a new airway bill, and the old
/// label must never print for it — a parcel carrying a label for a cancelled booking goes nowhere,
/// or somewhere else.
/// </para>
/// </summary>
internal class ConsignmentLabel : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    /// <summary>The airway bill this label is for.</summary>
    public string AwbNumber { get; set; } = string.Empty;

    /// <summary>Normalised media type, from the allowed set — never whatever the carrier claimed.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Built here from the consignment and airway bill, never taken from the carrier.</summary>
    public string FileName { get; set; } = string.Empty;

    public byte[] Content   { get; set; } = [];
    public int    SizeBytes { get; set; }

    /// <summary>SHA-256 of <see cref="Content"/>. The same label fetched twice is stored once.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>See <see cref="ConsignmentLabelSource"/>. Stored as its code.</summary>
    public string Source { get; set; } = string.Empty;

    public string ProviderKey { get; set; } = string.Empty;

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
