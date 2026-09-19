using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Cash a carrier collects on delivery, and what became of it.
/// <para>
/// <b>The half of decision G2 that was never built.</b> <c>CodAmount</c> and <c>CodCurrency</c> have
/// been on consignments since T-07, and nothing has ever recorded whether the money came back.
/// </para>
/// <para>
/// <b>This is the opposite of a freight accrual.</b> An accrual is what we owe a carrier; this is
/// cash the carrier is holding on our behalf — money already taken from a customer and not yet
/// passed on. Outstanding COD is a receivable, and one nobody was counting.
/// </para>
/// </summary>
internal class CodCollection : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>One per consignment, enforced. Two would count the same cash twice.</summary>
    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    public int?     CarrierId   { get; set; }
    public Carrier? Carrier     { get; set; }
    public string?  CarrierName { get; set; }

    /// <summary>
    /// What the consignment said should be collected, copied as at the moment the record was
    /// opened. Editing the consignment afterwards must not silently change what is owed to us.
    /// </summary>
    public decimal ExpectedAmount { get; set; }
    public string  Currency       { get; set; } = string.Empty;

    // ── What the carrier says it took ─────────────────────────────────────────

    public decimal?  CollectedAmount    { get; set; }
    public DateTime? CollectedAt        { get; set; }

    /// <summary>The carrier's own receipt or reference for the cash.</summary>
    public string? CollectionReference { get; set; }

    // ── What reached us ───────────────────────────────────────────────────────

    /// <summary>
    /// The sum of every remittance against this consignment. Stored as well as derivable, because
    /// "what is still outstanding" is the one question this table exists to answer quickly.
    /// </summary>
    public decimal RemittedAmount { get; set; }

    /// <summary>See <see cref="CodStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(CodStatus.Expected);

    public DateTime? SettledAt { get; set; }

    /// <summary>Required when accepting a shortfall — writing cash off without one is unexplainable.</summary>
    public string? WriteOffReason { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<CodRemittance> Remittances { get; set; } = new List<CodRemittance>();
}

/// <summary>
/// One payment from a carrier against collected cash.
/// <para>
/// Several are expected: carriers remit in batches, weekly or monthly, and one transfer commonly
/// covers many consignments. Recording them as a list rather than a single figure is what lets a
/// part payment be seen as a part payment rather than a settlement.
/// </para>
/// </summary>
internal class CodRemittance : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int           CodCollectionId { get; set; }
    public CodCollection CodCollection   { get; set; } = null!;

    public decimal  Amount     { get; set; }
    public DateTime ReceivedAt { get; set; }

    /// <summary>The transfer, cheque or batch the money arrived in.</summary>
    public string? Reference { get; set; }

    public string? Note { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
