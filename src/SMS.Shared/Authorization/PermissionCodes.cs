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
    public const string DELIVERY_TRACK     = "DELIVERY_TRACK";

    // ── Reports ───────────────────────────────────────────────────────────────
    public const string REPORT_VIEW        = "REPORT_VIEW";
    public const string REPORT_EXPORT      = "REPORT_EXPORT";

    // ── Workflow Engine ───────────────────────────────────────────────────────
    public const string WORKFLOW_ADMIN = "WORKFLOW_ADMIN";
    public const string WORKFLOW_VIEW  = "WORKFLOW_VIEW";

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
        DELIVERY_TRACK,
        REPORT_VIEW, REPORT_EXPORT,
        WORKFLOW_ADMIN, WORKFLOW_VIEW,
    ];
}
