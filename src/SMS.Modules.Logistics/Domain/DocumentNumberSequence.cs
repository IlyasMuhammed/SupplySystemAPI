using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One counter per organization, document prefix and year.
/// <para>
/// This exists to replace counting rows. The legacy
/// <c>ShipmentRepository.GenerateShipmentNumberAsync</c> produced a number with
/// <c>COUNT(*) + 1</c> over the year's shipments, which has two defects: two concurrent creates
/// read the same count and generate the same number — colliding on the unique index, so one
/// caller simply fails — and any row that is ever hard-deleted makes the next number a duplicate
/// of one already issued.
/// </para>
/// <para>
/// A counter is monotonic and never consults the documents, so neither failure is possible. The
/// cost is that numbers are consumed even when the document that asked for one is never saved —
/// gaps are normal and deliberate. A gap-free sequence and a concurrent system are mutually
/// exclusive, and for a delivery note, gaps are harmless while duplicates are not.
/// </para>
/// </summary>
internal class DocumentNumberSequence : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The document prefix, e.g. <c>DLV</c> or <c>SHP</c>. See <see cref="DocumentNumberPrefix"/>.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Calendar year the sequence belongs to; it restarts each January.</summary>
    public int Year { get; set; }

    /// <summary>The next number to hand out. Starts at 1.</summary>
    public int NextValue { get; set; } = 1;

    /// <summary>
    /// The whole correctness guarantee. Two callers that read the same counter cannot both
    /// commit: the second fails the concurrency check, rereads and takes the next value.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}

internal static class DocumentNumberPrefix
{
    internal const string Delivery    = "DLV";
    internal const string Consignment = "SHP";
    internal const string PickList    = "PCK";

    /// <summary>Handling unit — the barcode on a carton or pallet.</summary>
    internal const string HandlingUnit = "HU";
}
