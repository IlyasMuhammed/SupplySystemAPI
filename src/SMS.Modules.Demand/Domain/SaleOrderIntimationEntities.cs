using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

// A29-P4-06 §5.3 — the log of every SO-related email a Hangfire job has queued, sent, or failed to
// send. SaleOrderId is a same-DbContext FK to demand.sale_orders (a real int FK, not the Guid
// convention cross-module references use), matching every other in-schema link here
// (PoLine.PurchaseRequisitionId and friends). Migration-and-entity only, per this task's own
// wording — nothing populates or reads this table yet; §5's actual email jobs (P4-03/04's
// placeholders, P4-05's real EXPIRING warning) still write nothing to it until a later task wires
// them up.
internal class SaleOrderIntimation : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      UUID           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       SaleOrderId    { get; set; }
    public string    EventType      { get; set; } = string.Empty;
    public string    Recipients     { get; set; } = string.Empty;
    public string    Subject        { get; set; } = string.Empty;
    public string    BodyHtml       { get; set; } = string.Empty;
    public DateTime? SentAt         { get; set; }
    public string    Status         { get; set; } = EnumCode<SaleOrderIntimationStatus>.Of(SaleOrderIntimationStatus.Queued);
    public string?   ErrorMessage   { get; set; }
    public string?   HangfireJobId  { get; set; }
    public DateTime  CreatedDate    { get; set; } = DateTime.UtcNow;

    public SaleOrder SaleOrder { get; set; } = null!;
}
