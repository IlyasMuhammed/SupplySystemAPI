namespace SMS.Shared.Authorization;

/// <summary>
/// Canonical permission code constants used in JWT claims, role-permission seeds,
/// and [RequirePermission] attributes throughout the application.
/// </summary>
public static class PermissionCodes
{
    // ── System / Administration ───────────────────────────────────────────────
    public const string SYSTEM_CONFIGURE   = "SYSTEM_CONFIGURE";
    public const string USER_MANAGE        = "USER_MANAGE";
    public const string AUDIT_LOG_VIEW     = "AUDIT_LOG_VIEW";
    // Adding new Countries/Cities specifically — appends to the shared global catalog without the
    // ability to rename/delete existing entries (which stays SYSTEM_CONFIGURE-only, since that can
    // break every other organization relying on the current name/code).
    public const string LOCATION_MANAGE    = "LOCATION_MANAGE";
    // Reserved exclusively for the platform System Admin role — gates api/system/* (Organizations,
    // Feature Configuration). Distinct from SYSTEM_CONFIGURE, which other admin screens use and
    // which tenant-scoped admin roles may eventually also hold.
    public const string PLATFORM_SUPER_ADMIN = "PLATFORM_SUPER_ADMIN";

    // ── Suppliers ─────────────────────────────────────────────────────────────
    public const string SUPPLIER_VIEW      = "SUPPLIER_VIEW";
    public const string SUPPLIER_CREATE    = "SUPPLIER_CREATE";
    public const string SUPPLIER_EDIT      = "SUPPLIER_EDIT";
    public const string SUPPLIER_MANAGE    = "SUPPLIER_MANAGE";

    // ── RFQ ───────────────────────────────────────────────────────────────────
    public const string RFQ_VIEW           = "RFQ_VIEW";
    public const string RFQ_CREATE         = "RFQ_CREATE";
    public const string RFQ_MANAGE         = "RFQ_MANAGE";

    // ── Contracts ─────────────────────────────────────────────────────────────
    public const string CONTRACT_VIEW      = "CONTRACT_VIEW";
    public const string CONTRACT_MANAGE    = "CONTRACT_MANAGE";

    // ── Purchase Orders ───────────────────────────────────────────────────────
    public const string PO_VIEW            = "PO_VIEW";
    public const string PO_CREATE          = "PO_CREATE";
    public const string PO_EDIT            = "PO_EDIT";
    public const string PO_APPROVE         = "PO_APPROVE";
    public const string PO_CANCEL          = "PO_CANCEL";
    // Editing this org's own PO letterhead/branding — distinct from SYSTEM_CONFIGURE, which gates
    // genuinely global reference data (Lookup Types/Values) shared across every organization.
    public const string PO_TEMPLATE_MANAGE = "PO_TEMPLATE_MANAGE";

    // ── Requisitions ──────────────────────────────────────────────────────────
    public const string REQUISITION_CREATE    = "REQUISITION_CREATE";
    public const string REQUISITION_VIEW_OWN  = "REQUISITION_VIEW_OWN";
    public const string REQUISITION_VIEW_ALL  = "REQUISITION_VIEW_ALL";
    public const string REQUISITION_APPROVE   = "REQUISITION_APPROVE";

    // ── Budget ────────────────────────────────────────────────────────────────
    public const string BUDGET_VIEW        = "BUDGET_VIEW";
    public const string BUDGET_MANAGE      = "BUDGET_MANAGE";
    public const string BUDGET_MONITOR     = "BUDGET_MONITOR";

    // ── Inventory ─────────────────────────────────────────────────────────────
    public const string INVENTORY_VIEW     = "INVENTORY_VIEW";
    public const string STOCK_MANAGE       = "STOCK_MANAGE";
    public const string STOCK_ADJUST       = "STOCK_ADJUST";
    public const string REORDER_MANAGE     = "REORDER_MANAGE";

    // ── Warehouse ─────────────────────────────────────────────────────────────
    public const string WAREHOUSE_TRANSFER    = "WAREHOUSE_TRANSFER";
    public const string GOODS_RECEIVE         = "GOODS_RECEIVE";
    public const string PUTAWAY               = "PUTAWAY";
    public const string PICKING               = "PICKING";
    public const string DISPATCH              = "DISPATCH";
    public const string STOCK_LOCATION_UPDATE = "STOCK_LOCATION_UPDATE";

    // ── GRN Approvals ─────────────────────────────────────────────────────────
    public const string GRN_QC_CONFIRM      = "GRN_QC_CONFIRM";      // QC Officer
    public const string GRN_APPROVE         = "GRN_APPROVE";          // Inventory Manager
    public const string GRN_FINANCE_APPROVE = "GRN_FINANCE_APPROVE";  // Finance Officer

    // ── Material Management (Projects, MIR, MIV, Wastage, Returns, Cost Ledger) ─
    public const string MATERIAL_VIEW   = "MATERIAL_VIEW";
    public const string MATERIAL_MANAGE = "MATERIAL_MANAGE";

    // ── Finance ───────────────────────────────────────────────────────────────
    public const string INVOICE_VIEW       = "INVOICE_VIEW";
    public const string INVOICE_PROCESS    = "INVOICE_PROCESS";
    public const string PAYMENT_VIEW       = "PAYMENT_VIEW";
    public const string PAYMENT_PROCESS    = "PAYMENT_PROCESS";
    public const string PAYMENT_APPROVE    = "PAYMENT_APPROVE";  // Finance Manager
    public const string RECONCILIATION     = "RECONCILIATION";

    // ── Logistics / Delivery ──────────────────────────────────────────────────
    public const string DELIVERY_TRACK     = "DELIVERY_TRACK";   // legacy shipment + carrier screens
    public const string DELIVERY_VIEW      = "DELIVERY_VIEW";
    public const string DELIVERY_CREATE    = "DELIVERY_CREATE";
    public const string DELIVERY_EDIT      = "DELIVERY_EDIT";
    public const string SHIPMENT_BOOK      = "SHIPMENT_BOOK";    // spending money with a carrier
    /// <summary>
    /// Configuring how a carrier is dealt with — its accounts, which contract bookings go out on,
    /// and what each account may be asked for. Separate from DELIVERY_TRACK, which is reading
    /// shipments: somebody who watches parcels has no business changing the contracts they ship on.
    /// </summary>
    public const string CARRIER_MANAGE     = "CARRIER_MANAGE";
    /// <summary>
    /// Setting the secrets a carrier authenticates with. Separate from CARRIER_MANAGE because
    /// configuring which contract a parcel ships on and holding the keys to a carrier account are
    /// different levels of trust — and because the smaller the group that can write a credential,
    /// the shorter the list of people who could have leaked one.
    /// </summary>
    public const string CARRIER_CREDENTIAL_MANAGE = "CARRIER_CREDENTIAL_MANAGE";
    /// <summary>
    /// Seeing what a consignment costs to carry — chargeable weight now, rates and rate shopping as
    /// Phase 3 continues. Separate from DELIVERY_VIEW because a picker needs to see the parcel and
    /// has no business seeing what the company pays to move it.
    /// </summary>
    public const string SHIPMENT_RATE_VIEW = "SHIPMENT_RATE_VIEW";
    /// <summary>
    /// Editing a negotiated tariff. Separate from viewing rates for the same reason
    /// <c>CARRIER_MANAGE</c> is separate from <c>DELIVERY_TRACK</c>: changing what the company is
    /// deemed to pay for carriage is a commercial act, and it flows straight into Phase 4's
    /// invoice reconciliation.
    /// </summary>
    public const string RATE_CARD_MANAGE   = "RATE_CARD_MANAGE";
    /// <summary>
    /// Editing the standing decisions that route goods automatically. A rule is applied without
    /// anybody looking at it, which is precisely why changing one is a larger act than choosing a
    /// carrier for a single consignment.
    /// </summary>
    public const string SHIPPING_RULE_MANAGE = "SHIPPING_RULE_MANAGE";
    /// <summary>Seeing carrier bills and what is accrued against them.</summary>
    public const string FREIGHT_INVOICE_VIEW = "FREIGHT_INVOICE_VIEW";
    /// <summary>
    /// Recording, correcting and settling a carrier's bill. Separate from viewing one because this
    /// is the permission that decides what the company accepts it owes.
    /// </summary>
    public const string FREIGHT_INVOICE_RECONCILE = "FREIGHT_INVOICE_RECONCILE";
    /// <summary>
    /// Recording that goods were handed over, and attaching the evidence. Separate from
    /// <c>DELIVERY_EDIT</c> because it is the act that closes a movement and, for a manual carrier,
    /// the only thing that ever marks one delivered — and because the people who capture a signature
    /// are drivers and gate staff, who have no business editing the delivery itself.
    /// </summary>
    public const string POD_CAPTURE = "POD_CAPTURE";
    // Codes for work that does not exist yet — DELIVERY_RELEASE, DELIVERY_PACK,
    // DELIVERY_GOODS_ISSUE, SHIPMENT_CANCEL — are added with the features they gate.
    // Listing them early would put switches in the role editor that grant nothing.

    // ── Reports ───────────────────────────────────────────────────────────────
    public const string REPORT_VIEW        = "REPORT_VIEW";
    public const string REPORT_EXPORT      = "REPORT_EXPORT";

    // ── Workflow Engine ───────────────────────────────────────────────────────
    public const string WORKFLOW_ADMIN = "WORKFLOW_ADMIN";
    public const string WORKFLOW_VIEW  = "WORKFLOW_VIEW";

    // ── Sale Order Administration (Addendum 29 §3.1-3.2) ─────────────────────
    /// <summary>Read demand.SaleOrderConfig. Granted broadly — IT/Org Admin get this automatically.</summary>
    public const string SALE_ORDER_CONFIG_READ  = "SALE_ORDER_CONFIG_READ";
    /// <summary>
    /// Change demand.SaleOrderConfig. Deliberately excluded from System Admin's and Org Admin's
    /// otherwise-blanket grants (see AuthDataSeeder.RolePermissionSeed) — §3.1: "IT Admin / Super
    /// Admin may VIEW config, may CHANGE it only if they also hold [SUPPLY_DEPT_ADMIN]."
    /// </summary>
    public const string SALE_ORDER_CONFIG_WRITE = "SALE_ORDER_CONFIG_WRITE";

    // ── Sale Orders (Addendum 29 §4) ──────────────────────────────────────────
    public const string SALE_ORDER_VIEW    = "SALE_ORDER_VIEW";
    public const string SALE_ORDER_CREATE  = "SALE_ORDER_CREATE";
    public const string SALE_ORDER_EDIT    = "SALE_ORDER_EDIT";
    /// <summary>Confirming an order — the action that will trigger §4.3's availability check, once that exists.</summary>
    public const string SALE_ORDER_CONFIRM = "SALE_ORDER_CONFIRM";
    public const string SALE_ORDER_CANCEL  = "SALE_ORDER_CANCEL";

    // ── All codes (used by System Admin seed) ─────────────────────────────────
    public static readonly IReadOnlyList<string> All =
    [
        SYSTEM_CONFIGURE, USER_MANAGE, AUDIT_LOG_VIEW, PLATFORM_SUPER_ADMIN, LOCATION_MANAGE,
        SUPPLIER_VIEW, SUPPLIER_CREATE, SUPPLIER_EDIT, SUPPLIER_MANAGE,
        RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE,
        CONTRACT_VIEW, CONTRACT_MANAGE,
        PO_VIEW, PO_CREATE, PO_EDIT, PO_APPROVE, PO_CANCEL, PO_TEMPLATE_MANAGE,
        REQUISITION_CREATE, REQUISITION_VIEW_OWN, REQUISITION_VIEW_ALL, REQUISITION_APPROVE,
        BUDGET_VIEW, BUDGET_MANAGE, BUDGET_MONITOR,
        INVENTORY_VIEW, STOCK_MANAGE, STOCK_ADJUST, REORDER_MANAGE,
        WAREHOUSE_TRANSFER, GOODS_RECEIVE, PUTAWAY, PICKING, DISPATCH, STOCK_LOCATION_UPDATE,
        GRN_QC_CONFIRM, GRN_APPROVE, GRN_FINANCE_APPROVE,
        MATERIAL_VIEW, MATERIAL_MANAGE,
        INVOICE_VIEW, INVOICE_PROCESS, PAYMENT_VIEW, PAYMENT_PROCESS, PAYMENT_APPROVE, RECONCILIATION,
        DELIVERY_TRACK, DELIVERY_VIEW, DELIVERY_CREATE, DELIVERY_EDIT, SHIPMENT_BOOK,
        CARRIER_MANAGE, CARRIER_CREDENTIAL_MANAGE, SHIPMENT_RATE_VIEW, RATE_CARD_MANAGE,
        SHIPPING_RULE_MANAGE, FREIGHT_INVOICE_VIEW, FREIGHT_INVOICE_RECONCILE, POD_CAPTURE,
        REPORT_VIEW, REPORT_EXPORT,
        WORKFLOW_ADMIN, WORKFLOW_VIEW,
        SALE_ORDER_CONFIG_READ, SALE_ORDER_CONFIG_WRITE,
        SALE_ORDER_VIEW, SALE_ORDER_CREATE, SALE_ORDER_EDIT, SALE_ORDER_CONFIRM, SALE_ORDER_CANCEL,
    ];
}
