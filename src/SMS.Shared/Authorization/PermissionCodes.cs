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
    /// <summary>
    /// A34 D-24 — changing the organization's lead-time defaults (PUT api/lead-time/defaults: pick/pack, shipping,
    /// sales buffer, manufacturing buffer, QC, transfer days). Reading them accepts any of this, INVENTORY_VIEW,
    /// STOCK_MANAGE or SALE_ORDER_VIEW.
    /// </summary>
    public const string LEAD_TIME_DEFAULTS_MANAGE = "LEAD_TIME_DEFAULTS_MANAGE";

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
    /// <summary>
    /// A33 D-7 — "Approve dispatch" on a STAGED delivery whose fulfillment route has the APPROVAL step; goods
    /// issue is refused until it is approved. A manager's act, so separate from DELIVERY_EDIT.
    /// </summary>
    public const string DELIVERY_APPROVE = "DELIVERY_APPROVE";
    // Codes for work that does not exist yet — DELIVERY_RELEASE, DELIVERY_PACK,
    // DELIVERY_GOODS_ISSUE, SHIPMENT_CANCEL — are added with the features they gate.
    // Listing them early would put switches in the role editor that grant nothing.

    // ── Fulfillment routes (Addendum 33, D-9) ─────────────────────────────────
    /// <summary>Opening the routes settings screen. Reading routes (pickers) also accepts SALE_ORDER_VIEW, INVENTORY_VIEW or DELIVERY_VIEW.</summary>
    public const string FULFILLMENT_ROUTE_VIEW   = "FULFILLMENT_ROUTE_VIEW";
    /// <summary>Creating, editing, (de)activating and deleting routes, and choosing the organization's defaults.</summary>
    public const string FULFILLMENT_ROUTE_MANAGE = "FULFILLMENT_ROUTE_MANAGE";
    /// <summary>Setting a product variant's default route, one by one or in bulk by category. A sale order line's override needs only SALE_ORDER_EDIT.</summary>
    public const string FULFILLMENT_ROUTE_ASSIGN = "FULFILLMENT_ROUTE_ASSIGN";

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
    /// <summary>A32 C4 — holding stock against a sale order line by hand (the spec's sales_reserve_inventory).</summary>
    public const string SALE_ORDER_RESERVE = "SALE_ORDER_RESERVE";
    /// <summary>A32 C4 — giving a sale order line's held stock back (the spec's sales_release_reservation).</summary>
    public const string SALE_ORDER_RELEASE_RESERVATION = "SALE_ORDER_RELEASE_RESERVATION";

    // ── Sales pre-order pipeline (Addendum 32) ────────────────────────────────
    public const string SALE_INQUIRY_VIEW   = "SALE_INQUIRY_VIEW";
    public const string SALE_INQUIRY_CREATE = "SALE_INQUIRY_CREATE";
    /// <summary>Editing lines, evaluating them and moving the inquiry through its states.</summary>
    public const string SALE_INQUIRY_EDIT   = "SALE_INQUIRY_EDIT";
    public const string SALE_QUOTATION_VIEW   = "SALE_QUOTATION_VIEW";
    /// <summary>Creating a quotation, on its own or from a reviewed inquiry.</summary>
    public const string SALE_QUOTATION_CREATE = "SALE_QUOTATION_CREATE";
    /// <summary>Editing a draft, recording the customer's responses, accepting or rejecting it.</summary>
    public const string SALE_QUOTATION_EDIT   = "SALE_QUOTATION_EDIT";
    /// <summary>Sending a quotation to the customer — it then can no longer change.</summary>
    public const string SALE_QUOTATION_SEND   = "SALE_QUOTATION_SEND";
    /// <summary>Adding, renaming, reordering and deactivating the organization's rejection reasons.</summary>
    public const string SALE_REJECTION_REASON_MANAGE = "SALE_REJECTION_REASON_MANAGE";

    // ── Receivables (Addendum 29 §9–§10) ──────────────────────────────────────
    // Deliberately not INVOICE_* / PAYMENT_*: those are seeded and described as the supplier side —
    // approving and executing what the company pays out. Being allowed to pay a supplier is not the
    // same trust as being allowed to record that a customer has paid, and a receipt that is not real
    // is a way of making a debt disappear.
    /// <summary>Reading sales invoices and printing them.</summary>
    public const string SALES_INVOICE_VIEW    = "SALES_INVOICE_VIEW";
    /// <summary>Raising a sales invoice from a delivery, amending or deleting a draft, and issuing it — issuing books the receivable.</summary>
    public const string SALES_INVOICE_MANAGE  = "SALES_INVOICE_MANAGE";
    /// <summary>Reading customer payments and how they were applied.</summary>
    public const string CUSTOMER_PAYMENT_VIEW = "CUSTOMER_PAYMENT_VIEW";
    /// <summary>Recording money received from a customer and applying it to their invoices — which is what settles them.</summary>
    public const string CUSTOMER_PAYMENT_RECORD = "CUSTOMER_PAYMENT_RECORD";
    /// <summary>Reading a customer's receivables ledger — what they owe, entry by entry.</summary>
    public const string CUSTOMER_LEDGER_VIEW  = "CUSTOMER_LEDGER_VIEW";

    // ── Product ledger (Addendum 29 §11) ──────────────────────────────────────
    /// <summary>
    /// Reading a variant's cost history, its stock value and weighted-average cost, and the product
    /// profitability report. Cost and margin, not stock levels, so it is not <c>INVENTORY_VIEW</c>: being
    /// allowed to see what is on the shelf is not being allowed to see what it cost and what it earned.
    /// </summary>
    public const string PRODUCT_LEDGER_VIEW   = "PRODUCT_LEDGER_VIEW";

    // ── Allocation engine (Addendum 30 §14, §25) ──────────────────────────────
    // The rest of Addendum 30's codes (BOM_*, PROD_*, MI_*, QI_*, FGR_*, SUPPLY_*, PROD_LEDGER_VIEW)
    // arrive with the phases that build what they gate, as the delivery codes above did.
    /// <summary>Reading who holds which stock, open demands, incoming supply and availability.</summary>
    public const string ALLOCATION_VIEW  = "ALLOCATION_VIEW";
    /// <summary>Registering a demand or expected supply and running the engine by hand.</summary>
    public const string ALLOCATION_RUN   = "ALLOCATION_RUN";
    /// <summary>Releasing or moving a firm/reserved allocation from one demand to another, and editing the priority rules — this decides who gets scarce stock.</summary>
    public const string ALLOCATION_ADMIN = "ALLOCATION_ADMIN";

    // ── Bills of materials (Addendum 30 §9.2) ─────────────────────────────────
    public const string BOM_VIEW     = "BOM_VIEW";
    public const string BOM_CREATE   = "BOM_CREATE";
    /// <summary>Changing a DRAFT or REJECTED recipe in place, or deleting it.</summary>
    public const string BOM_EDIT     = "BOM_EDIT";
    public const string BOM_SUBMIT   = "BOM_SUBMIT";
    /// <summary>Approving or rejecting a submitted recipe. Never the person who submitted it (four eyes).</summary>
    public const string BOM_APPROVE  = "BOM_APPROVE";
    /// <summary>Making an approved recipe the one production uses — retires the previous one.</summary>
    public const string BOM_ACTIVATE = "BOM_ACTIVATE";
    public const string BOM_OBSOLETE = "BOM_OBSOLETE";
    /// <summary>Moving an unreleased production order onto a newer recipe (§8.3).</summary>
    public const string BOM_ADMIN    = "BOM_ADMIN";

    // ── Production orders, material issue and supply requirements (Addendum 30 §25) ──
    public const string PROD_VIEW     = "PROD_VIEW";
    public const string PROD_CREATE   = "PROD_CREATE";
    /// <summary>Planning (releasing) an order: exploding its recipe, holding materials, raising supply.</summary>
    public const string PROD_PLAN     = "PROD_PLAN";
    public const string PROD_START    = "PROD_START";
    /// <summary>Reporting output and marking production complete.</summary>
    public const string PROD_REPORT   = "PROD_REPORT";
    public const string PROD_CANCEL   = "PROD_CANCEL";
    /// <summary>The production manager: issues beyond the requirement and other calls a supervisor makes.</summary>
    public const string PROD_MANAGER  = "PROD_MANAGER";
    public const string MI_CREATE     = "MI_CREATE";
    /// <summary>Confirming an issue is what moves the stock.</summary>
    public const string MI_CONFIRM    = "MI_CONFIRM";
    public const string MI_REVERSE    = "MI_REVERSE";
    public const string SUPPLY_VIEW   = "SUPPLY_VIEW";
    public const string SUPPLY_CREATE = "SUPPLY_CREATE";
    public const string SUPPLY_CANCEL = "SUPPLY_CANCEL";

    // ── Quality inspection and finished goods receipt (Addendum 30 §18, §19, §25) ──
    /// <summary>Reading an inspection or a receipt, and creating one — recording a decision is the narrower QI_APPROVE.</summary>
    public const string QI_CREATE  = "QI_CREATE";
    /// <summary>Recording the pass/reject/hold/rework decision. Separate from creating one so the inspector need not be who logged it (§18.4 separation of duties).</summary>
    public const string QI_APPROVE = "QI_APPROVE";
    public const string FGR_CREATE  = "FGR_CREATE";
    /// <summary>Confirming a receipt is what credits inventory and completes the production order.</summary>
    public const string FGR_CONFIRM = "FGR_CONFIRM";

    // ── Production ledger (Addendum 30 §19A, §25) — a read of MI/FGR/SR history already gated by
    // PROD_VIEW; this is the narrower cross-order and summary view, kept apart from
    // PRODUCT_LEDGER_VIEW (Finance's cost/margin ledger — a different book entirely).
    public const string PROD_LEDGER_VIEW = "PROD_LEDGER_VIEW";

    // ── Accounting integration — QuickBooks Online (docs/quickbooks/QUICKBOOKS-INTEGRATION-PLAN.md) ──
    /// <summary>Seeing the connection, mappings, sync dashboard and each record's sync status.</summary>
    public const string INTEGRATION_VIEW   = "INTEGRATION_VIEW";
    /// <summary>Connecting and disconnecting a company, changing mappings and settings, issuing API keys, going live.</summary>
    public const string INTEGRATION_MANAGE = "INTEGRATION_MANAGE";
    /// <summary>Pushing a record now and retrying a failed one — operational, not configuration.</summary>
    public const string INTEGRATION_SYNC   = "INTEGRATION_SYNC";

    // ── Finance setup — tax codes and exchange rates (docs/finance/SAP-ALIGNMENT-PLAN.md) ──
    /// <summary>Creating and changing tax codes and exchange rates. Reading them (for pickers) needs only a sign-in.</summary>
    public const string FINANCE_SETUP_MANAGE = "FINANCE_SETUP_MANAGE";

    // ── Multi-currency (Addendum 35, D-16; docs/multi-currency/ADDENDUM-35-ANALYSIS.md) ──
    /// <summary>Reading the organization's currencies (every role — document forms need the picker).</summary>
    public const string CURRENCY_VIEW                 = "CURRENCY_VIEW";
    /// <summary>Adding an org currency, changing its formatting, activating/deactivating it.</summary>
    public const string CURRENCY_MANAGE               = "CURRENCY_MANAGE";
    /// <summary>Reading exchange rates and converting amounts (every role).</summary>
    public const string CURRENCY_RATE_VIEW            = "CURRENCY_RATE_VIEW";
    /// <summary>Inserting and correcting exchange rates.</summary>
    public const string CURRENCY_RATE_MANAGE          = "CURRENCY_RATE_MANAGE";
    /// <summary>Changing the organization's sale/purchase/service base currencies and FX account codes.</summary>
    public const string ORG_CURRENCY_SETTINGS_MANAGE  = "ORG_CURRENCY_SETTINGS_MANAGE";
    /// <summary>Running the unrealized exchange revaluation by hand.</summary>
    public const string EXCHANGE_REVALUATION_RUN      = "EXCHANGE_REVALUATION_RUN";

    // ── Service orders (Addendum 36, D-12; docs/service-orders/ADDENDUM-36-ANALYSIS.md) ──
    /// <summary>Reading service orders, their materials, ledger and the dashboard.</summary>
    public const string SERVICE_ORDER_VIEW     = "SERVICE_ORDER_VIEW";
    public const string SERVICE_ORDER_CREATE   = "SERVICE_ORDER_CREATE";
    /// <summary>Updating, planning, starting, ad-hoc materials, reserving and closing a service order.</summary>
    public const string SERVICE_ORDER_EDIT     = "SERVICE_ORDER_EDIT";
    /// <summary>Recording completion: consumed materials, actual hours, signature.</summary>
    public const string SERVICE_ORDER_COMPLETE = "SERVICE_ORDER_COMPLETE";
    public const string SERVICE_ORDER_CANCEL   = "SERVICE_ORDER_CANCEL";

    // ── Customer master (Addendum 37, D-17; docs/module-registry/API-CONTRACT.md §5) — added by CUS, seeded by REG ──
    /// <summary>Reading customers (list, detail, search, balance, sync).</summary>
    public const string CUSTOMER_VIEW       = "CUSTOMER_VIEW";
    public const string CUSTOMER_CREATE     = "CUSTOMER_CREATE";
    public const string CUSTOMER_EDIT       = "CUSTOMER_EDIT";
    /// <summary>Deactivating and reactivating a customer.</summary>
    public const string CUSTOMER_DEACTIVATE = "CUSTOMER_DEACTIVATE";

    // ── Module registry (Addendum 37, D-17; docs/module-registry/API-CONTRACT.md §1.1) ──
    /// <summary>Seeing the organization's modules, their history and the impact of switching one off.</summary>
    public const string MODULES_VIEW   = "MODULES_VIEW";
    /// <summary>Switching licensed modules and their features on and off for the organization.</summary>
    public const string MODULES_MANAGE = "MODULES_MANAGE";

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
        INVENTORY_VIEW, STOCK_MANAGE, STOCK_ADJUST, REORDER_MANAGE, LEAD_TIME_DEFAULTS_MANAGE,
        WAREHOUSE_TRANSFER, GOODS_RECEIVE, PUTAWAY, PICKING, DISPATCH, STOCK_LOCATION_UPDATE,
        GRN_QC_CONFIRM, GRN_APPROVE, GRN_FINANCE_APPROVE,
        MATERIAL_VIEW, MATERIAL_MANAGE,
        INVOICE_VIEW, INVOICE_PROCESS, PAYMENT_VIEW, PAYMENT_PROCESS, PAYMENT_APPROVE, RECONCILIATION,
        DELIVERY_TRACK, DELIVERY_VIEW, DELIVERY_CREATE, DELIVERY_EDIT, SHIPMENT_BOOK,
        CARRIER_MANAGE, CARRIER_CREDENTIAL_MANAGE, SHIPMENT_RATE_VIEW, RATE_CARD_MANAGE,
        SHIPPING_RULE_MANAGE, FREIGHT_INVOICE_VIEW, FREIGHT_INVOICE_RECONCILE, POD_CAPTURE,
        DELIVERY_APPROVE, FULFILLMENT_ROUTE_VIEW, FULFILLMENT_ROUTE_MANAGE, FULFILLMENT_ROUTE_ASSIGN,
        REPORT_VIEW, REPORT_EXPORT,
        WORKFLOW_ADMIN, WORKFLOW_VIEW,
        SALE_ORDER_CONFIG_READ, SALE_ORDER_CONFIG_WRITE,
        SALE_ORDER_VIEW, SALE_ORDER_CREATE, SALE_ORDER_EDIT, SALE_ORDER_CONFIRM, SALE_ORDER_CANCEL,
        SALE_ORDER_RESERVE, SALE_ORDER_RELEASE_RESERVATION,
        SALE_INQUIRY_VIEW, SALE_INQUIRY_CREATE, SALE_INQUIRY_EDIT,
        SALE_QUOTATION_VIEW, SALE_QUOTATION_CREATE, SALE_QUOTATION_EDIT, SALE_QUOTATION_SEND,
        SALE_REJECTION_REASON_MANAGE,
        SALES_INVOICE_VIEW, SALES_INVOICE_MANAGE, CUSTOMER_PAYMENT_VIEW, CUSTOMER_PAYMENT_RECORD, CUSTOMER_LEDGER_VIEW,
        PRODUCT_LEDGER_VIEW,
        ALLOCATION_VIEW, ALLOCATION_RUN, ALLOCATION_ADMIN,
        BOM_VIEW, BOM_CREATE, BOM_EDIT, BOM_SUBMIT, BOM_APPROVE, BOM_ACTIVATE, BOM_OBSOLETE, BOM_ADMIN,
        PROD_VIEW, PROD_CREATE, PROD_PLAN, PROD_START, PROD_REPORT, PROD_CANCEL, PROD_MANAGER,
        MI_CREATE, MI_CONFIRM, MI_REVERSE, SUPPLY_VIEW, SUPPLY_CREATE, SUPPLY_CANCEL,
        QI_CREATE, QI_APPROVE, FGR_CREATE, FGR_CONFIRM, PROD_LEDGER_VIEW,
        INTEGRATION_VIEW, INTEGRATION_MANAGE, INTEGRATION_SYNC,
        FINANCE_SETUP_MANAGE,
        CURRENCY_VIEW, CURRENCY_MANAGE, CURRENCY_RATE_VIEW, CURRENCY_RATE_MANAGE,
        ORG_CURRENCY_SETTINGS_MANAGE, EXCHANGE_REVALUATION_RUN,
        SERVICE_ORDER_VIEW, SERVICE_ORDER_CREATE, SERVICE_ORDER_EDIT, SERVICE_ORDER_COMPLETE, SERVICE_ORDER_CANCEL,
        CUSTOMER_VIEW, CUSTOMER_CREATE, CUSTOMER_EDIT, CUSTOMER_DEACTIVATE,
        MODULES_VIEW, MODULES_MANAGE,
    ];
}
