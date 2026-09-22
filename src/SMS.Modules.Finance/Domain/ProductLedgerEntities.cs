using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// A29-P8-01 §11 — the per-variant financial ledger: what a variant cost and what it earned, entry by
// entry, with a running quantity and a weighted-average running value. It is not MasterProductLedger
// (Addendum 24), which mirrors every warehouse movement for the stock register and keeps no running
// cost; this one is organization-wide per variant, and its running figures are the source of the WAC.
// The entry-type and direction vocabularies are public, in SMS.Shared.Common beside the writer's
// contract (IProductLedgerService), because the modules that post to it need to name them.

/// <summary>
/// One line of a variant's history. <see cref="Quantity"/> is always positive and
/// <see cref="Direction"/> says which way it moved; <see cref="RunningQty"/> and
/// <see cref="RunningValue"/> are the totals after this entry, so the weighted-average cost at that
/// moment is <c>RunningValue / RunningQty</c>.
/// <para>
/// <b>Append-only by construction:</b> no modified or deleted columns exist, so a correction is a new
/// entry that offsets the old one.
/// </para>
/// </summary>
internal class ProductLedgerEntry : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The variant. Bare UUID onto inventory.ProductVariants — no FK across modules.</summary>
    public Guid VariantUuid { get; set; }

    /// <summary>The variant's product, copied here so per-product totals need no join into Inventory.</summary>
    public Guid ProductUuid { get; set; }

    /// <summary>
    /// Position in this variant's ledger, 1, 2, 3…. Unique per (organization, variant): two writers
    /// racing for the next entry both compute the same number, one loses on the unique index and
    /// retries against the fresh last row — which is what keeps the running quantity and value from forking.
    /// </summary>
    public int SequenceNo { get; set; }

    public DateTime EntryDate { get; set; }

    /// <summary>See <see cref="ProductLedgerEntryTypes"/>.</summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>The document behind the entry — e.g. GRN, SalesInvoice. Bare reference, no FK.</summary>
    public string ReferenceType   { get; set; } = string.Empty;
    public Guid   ReferenceId     { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>The supplier or customer on the other side; none for an adjustment or a write-off.</summary>
    public Guid? PartnerId { get; set; }

    public decimal Quantity  { get; set; }
    public decimal UnitCost  { get; set; }
    public decimal TotalCost { get; set; }

    /// <summary>See <see cref="ProductLedgerDirections"/>.</summary>
    public string Direction { get; set; } = string.Empty;

    public decimal RunningQty   { get; set; }
    public decimal RunningValue { get; set; }

    public string? Narration { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
