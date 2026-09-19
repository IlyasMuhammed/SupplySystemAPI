using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

// A29-P3-02 §3.2 — one row per org: the Supply Department Admin's standing policy for auto-PO,
// fulfillment mode and reservation behaviour on sale orders. Change auditing ("every change
// audit-trailed" per §3.2) and write-side enforcement ("updated_by must be SUPPLY_DEPT_ADMIN") are
// a later task's concern — this task's own title scopes it to the migration, matching how
// A29-P2-01/P2-02 stayed schema-only in the same way.
internal class SaleOrderConfig : ITenantScopedEntity
{
    public int      Id                        { get; set; }
    public Guid     Uuid                      { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId            { get; set; }

    public bool     AutoPoEnabled             { get; set; } = true;
    public string   SupplierSelectionMode     { get; set; } = SupplierSelectionModes.BestMatch;
    public string   AutoPoApprovalMode        { get; set; } = AutoPoApprovalModes.RequireWorkflow;
    public bool     DropShipEnabled           { get; set; }
    public bool     SelfPickupEnabled         { get; set; } = true;
    public string   DefaultFulfillmentMode    { get; set; } = FulfillmentModes.InStock;
    public int      ReservationTtlHours       { get; set; } = 72;
    public bool     PartialFulfillmentAllowed { get; set; } = true;
    public bool     EmailIntimationEnabled    { get; set; } = true;
    // Unenforced scalar reference to Auth's Department.DepartmentId — an int, not the usual
    // cross-module Guid, because Department (SMS.Modules.Auth.Domain.UserAccount.cs) carries no
    // Uuid of its own to match; its only real identity is this int.
    public int?     IntimationDepartmentId    { get; set; }
    public string?  IntimationCcEmails        { get; set; }
    public bool     ShipmentRequiredDefault   { get; set; } = true;

    public int?      UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<SaleOrderConfigAudit> AuditEntries { get; set; } = new List<SaleOrderConfigAudit>();
}

// A29-P3-03 §3.2's "every change audit-trailed" — field-level change log, written to the SAME
// DbContext as the config row it describes (one SaveChangesAsync commits both), mirroring
// SupplierRateHistory's existing pattern in the Inventory module for VariantSupplier.
internal class SaleOrderConfigAudit : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     OrganizationId   { get; set; }
    public int      SaleOrderConfigId { get; set; }
    public string   FieldChanged     { get; set; } = string.Empty;
    public string?  OldValue         { get; set; }
    public string?  NewValue         { get; set; }
    public int      ChangedBy        { get; set; }
    public DateTime ChangedAt        { get; set; }

    public SaleOrderConfig SaleOrderConfig { get; set; } = null!;
}

// §3.2 doesn't state a default for these three — §3.3 explicitly calls BEST_MATCH "(recommended)",
// so that one is the recommendation itself; RequireWorkflow and InStock are this task's own choice
// of the safest, most conservative starting behaviour (auto-PO still goes through normal approval,
// and fulfillment doesn't assume back-to-back purchasing or drop-ship until an org opts in).
internal static class SupplierSelectionModes
{
    public const string DefaultSupplier = "DEFAULT_SUPPLIER";
    public const string BestMatch       = "BEST_MATCH";
    public const string Manual          = "MANUAL";
}

internal static class AutoPoApprovalModes
{
    public const string RequireWorkflow = "REQUIRE_WORKFLOW";
    public const string AutoSend        = "AUTO_SEND";
    public const string DraftOnly       = "DRAFT_ONLY";
}

internal static class FulfillmentModes
{
    public const string InStock     = "IN_STOCK";
    public const string BackToBack  = "BACK_TO_BACK";
    public const string DropShip    = "DROP_SHIP";
}
