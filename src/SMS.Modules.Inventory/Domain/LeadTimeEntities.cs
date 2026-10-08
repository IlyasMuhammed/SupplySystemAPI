using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Domain;

/// <summary>
/// A34 C3 (D-10) — an organization's lead-time defaults: the fallback for a variant component left NULL (BR-C3-02).
/// One row per organization (unique OrganizationId). A missing row reads as <see cref="SystemDefaults"/>; the row is
/// created by the first PUT and never by provisioning or a migration (no backfill).
/// </summary>
internal class LeadTimeDefaults : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int PickPackDays            { get; set; } = SystemDefaults.PickPackDays;
    public int ShippingLeadTimeDays    { get; set; } = SystemDefaults.ShippingLeadTimeDays;
    public int SalesBufferDays         { get; set; } = SystemDefaults.SalesBufferDays;
    public int ManufacturingBufferDays { get; set; } = SystemDefaults.ManufacturingBufferDays;
    public int QualityInspectionDays   { get; set; } = SystemDefaults.QualityInspectionDays;
    public int InternalTransferDays    { get; set; } = SystemDefaults.InternalTransferDays;

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public byte[]    RowVersion   { get; set; } = [];

    /// <summary>The spec's §5.4 column defaults (1/3/1/0/0/0): what an organization without a row gets.</summary>
    internal static class SystemDefaults
    {
        public const int PickPackDays            = 1;
        public const int ShippingLeadTimeDays    = 3;
        public const int SalesBufferDays         = 1;
        public const int ManufacturingBufferDays = 0;
        public const int QualityInspectionDays   = 0;
        public const int InternalTransferDays    = 0;
    }
}
