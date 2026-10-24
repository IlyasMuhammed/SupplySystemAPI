using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Data;

internal sealed class AuthDataSeeder
{
    private readonly AuthDbContext _db;
    private readonly IPasswordHasher<UserAccount> _hasher;

    public AuthDataSeeder(AuthDbContext db, IPasswordHasher<UserAccount> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task SeedAsync()
    {
        await _db.Database.MigrateAsync();
        await SeedPermissionsAsync();
        await SeedRolesAsync();
        await SeedRolePermissionsAsync();
        await SeedCurrencyGrantsAsync();
        await SeedCustomerGrantsAsync();
        await SeedImportGrantsAsync();
        await SeedGlGrantsAsync();
        await SeedCrmViewGrantsAsync();
        await SeedAdminUserAsync();
    }

    /// <summary>
    /// A35 D-16 / REV-09 — every role (built-in and custom, any organization) gets <see cref="EveryRoleCodes"/>, and every
    /// role allowed FINANCE_SETUP_MANAGE gets <see cref="FinanceSetupCodes"/>. Only MISSING grants are added: a row an
    /// administrator switched off (IsAllowed = false) is left as they set it. Idempotent; runs on every start, so roles that
    /// already exist in a database pick the grants up on the next start.
    /// </summary>
    internal async Task SeedCurrencyGrantsAsync()
    {
        var permIds = await _db.Permissions
            .Where(p => EveryRoleCodes.Contains(p.Code) || FinanceSetupCodes.Contains(p.Code) || p.Code == PermissionCodes.FINANCE_SETUP_MANAGE)
            .ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        if (!permIds.TryGetValue(PermissionCodes.FINANCE_SETUP_MANAGE, out var setupId)) setupId = -1;

        var roleIds  = await _db.Roles.IgnoreQueryFilters().Select(r => r.RoleID).ToListAsync();
        var existing = (await _db.RolePermissions.IgnoreQueryFilters().Select(rp => new { rp.RoleID, rp.PermissionID }).ToListAsync())
            .Select(x => (x.RoleID, x.PermissionID)).ToHashSet();
        var setupRoles = (await _db.RolePermissions.IgnoreQueryFilters()
            .Where(rp => rp.PermissionID == setupId && rp.IsAllowed).Select(rp => rp.RoleID).ToListAsync()).ToHashSet();

        foreach (var roleId in roleIds)
        {
            var codes = setupRoles.Contains(roleId) ? EveryRoleCodes.Concat(FinanceSetupCodes) : EveryRoleCodes;
            foreach (var code in codes)
            {
                if (!permIds.TryGetValue(code, out var permId) || !existing.Add((roleId, permId))) continue;
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleID = roleId, PermissionID = permId, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    // ── 1. Permissions ────────────────────────────────────────────────────────

    private static readonly (string Name, string Code, string Description)[] PermissionSeed =
    [
        ("Configure System",              PermissionCodes.SYSTEM_CONFIGURE,      "Configure system-wide settings and integrations"),
        ("Manage Users",                  PermissionCodes.USER_MANAGE,           "Create, update and deactivate user accounts"),
        ("View Audit Logs",               PermissionCodes.AUDIT_LOG_VIEW,        "Access full application audit trail"),
        ("Platform Super Admin",          PermissionCodes.PLATFORM_SUPER_ADMIN,  "Manage tenant organizations and platform-wide feature configuration"),
        ("Manage Locations",              PermissionCodes.LOCATION_MANAGE,       "Add new countries and cities to the shared location catalog"),

        ("View Suppliers",                PermissionCodes.SUPPLIER_VIEW,         "Read supplier records"),
        ("Create Suppliers",              PermissionCodes.SUPPLIER_CREATE,       "Add new suppliers to the system"),
        ("Edit Suppliers",                PermissionCodes.SUPPLIER_EDIT,         "Update existing supplier information"),
        ("Manage Suppliers",              PermissionCodes.SUPPLIER_MANAGE,       "Full supplier lifecycle management"),

        ("View RFQs",                     PermissionCodes.RFQ_VIEW,              "Read request-for-quotation records"),
        ("Create RFQs",                   PermissionCodes.RFQ_CREATE,            "Issue new requests for quotation"),
        ("Manage RFQs",                   PermissionCodes.RFQ_MANAGE,            "Evaluate, negotiate and close RFQs"),

        ("View Contracts",                PermissionCodes.CONTRACT_VIEW,         "Read supplier contracts"),
        ("Manage Contracts",              PermissionCodes.CONTRACT_MANAGE,       "Create, amend and close contracts"),

        ("View Purchase Orders",          PermissionCodes.PO_VIEW,               "Read PO records"),
        ("Create Purchase Orders",        PermissionCodes.PO_CREATE,             "Issue new purchase orders"),
        ("Edit Purchase Orders",          PermissionCodes.PO_EDIT,               "Amend draft purchase orders"),
        ("Approve Purchase Orders",       PermissionCodes.PO_APPROVE,            "Authorise POs within budget limits"),
        ("Cancel Purchase Orders",        PermissionCodes.PO_CANCEL,             "Cancel or reject purchase orders"),
        ("Manage PO Document Template",   PermissionCodes.PO_TEMPLATE_MANAGE,    "Edit this organization's PO letterhead/branding template"),

        ("Create Requisitions",           PermissionCodes.REQUISITION_CREATE,    "Raise purchase requisitions"),
        ("View Own Requisitions",         PermissionCodes.REQUISITION_VIEW_OWN,  "View requisitions created by the user"),
        ("View All Requisitions",         PermissionCodes.REQUISITION_VIEW_ALL,  "View all purchase requisitions"),
        ("Approve Requisitions",          PermissionCodes.REQUISITION_APPROVE,   "Approve or reject purchase requisitions"),

        ("View Budgets",                  PermissionCodes.BUDGET_VIEW,           "Read budget allocations"),
        ("Manage Budgets",                PermissionCodes.BUDGET_MANAGE,         "Create and modify budget lines"),
        ("Monitor Budgets",               PermissionCodes.BUDGET_MONITOR,        "Track budget utilisation and flag overruns"),

        ("View Inventory",                PermissionCodes.INVENTORY_VIEW,        "Read stock levels and item records"),
        ("Manage Stock",                  PermissionCodes.STOCK_MANAGE,          "Add and remove stock items"),
        ("Adjust Stock",                  PermissionCodes.STOCK_ADJUST,          "Record stock adjustments and write-offs"),
        ("Manage Lead Time Defaults",     PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE, "Change the organization's default pick/pack, shipping, buffer, QC and transfer days used to calculate delivery dates"),
        ("Manage Reorder Rules",          PermissionCodes.REORDER_MANAGE,        "Configure reorder points and safety stock"),

        ("Warehouse Transfer",            PermissionCodes.WAREHOUSE_TRANSFER,    "Transfer stock between warehouse locations"),
        ("Receive Goods",                 PermissionCodes.GOODS_RECEIVE,         "Process incoming goods receipts"),
        ("Put-Away",                      PermissionCodes.PUTAWAY,               "Assign received goods to bin locations"),
        ("Picking",                       PermissionCodes.PICKING,               "Pick items to fulfil outbound orders"),
        ("Dispatch",                      PermissionCodes.DISPATCH,              "Despatch goods and update shipment status"),
        ("Update Stock Locations",        PermissionCodes.STOCK_LOCATION_UPDATE, "Reassign stock to different bin locations"),

        ("View Invoices",                 PermissionCodes.INVOICE_VIEW,          "Read supplier invoices"),
        ("Process Invoices",              PermissionCodes.INVOICE_PROCESS,       "Verify and approve invoices for payment"),
        ("View Payments",                 PermissionCodes.PAYMENT_VIEW,          "Read payment records"),
        ("Process Payments",              PermissionCodes.PAYMENT_PROCESS,       "Approve and execute supplier payments"),
        ("Approve Payments",              PermissionCodes.PAYMENT_APPROVE,       "Finance Manager sign-off on supplier payments"),
        ("Reconciliation",                PermissionCodes.RECONCILIATION,        "Perform statement and ledger reconciliation"),

        ("Track Deliveries",              PermissionCodes.DELIVERY_TRACK,        "Monitor inbound and outbound delivery status"),
        ("View Deliveries",               PermissionCodes.DELIVERY_VIEW,         "Open the delivery cockpit and read delivery orders"),
        ("Create Deliveries",             PermissionCodes.DELIVERY_CREATE,       "Raise deliveries, including from a PO, SRO or material issue"),
        ("Edit Deliveries",               PermissionCodes.DELIVERY_EDIT,         "Amend, hold, cancel or short-close a delivery"),
        ("Book Shipments",                PermissionCodes.SHIPMENT_BOOK,         "Commit a consignment to a carrier — this spends money"),
        ("Manage Carriers",               PermissionCodes.CARRIER_MANAGE,        "Configure carrier accounts and what each may be asked for"),
        ("Manage Carrier Credentials",    PermissionCodes.CARRIER_CREDENTIAL_MANAGE, "Set the secrets a carrier authenticates with — write only, never readable back"),
        ("View Shipping Rates",           PermissionCodes.SHIPMENT_RATE_VIEW,    "See what a consignment costs to carry — chargeable weight, and rates as Phase 3 lands"),
        ("Manage Rate Cards",             PermissionCodes.RATE_CARD_MANAGE,      "Edit negotiated carrier tariffs — this decides what carriage is deemed to cost"),
        ("Manage Shipping Rules",         PermissionCodes.SHIPPING_RULE_MANAGE,  "Edit the standing rules that route goods to a carrier automatically"),
        ("View Freight Invoices",         PermissionCodes.FREIGHT_INVOICE_VIEW,  "See carrier bills and what is accrued against them"),
        ("Reconcile Freight Invoices",    PermissionCodes.FREIGHT_INVOICE_RECONCILE, "Record and settle a carrier's bill — this decides what the company accepts it owes"),
        ("Capture Proof of Delivery",     PermissionCodes.POD_CAPTURE,           "Record who took the goods and attach the signature, photograph or delivery note"),
        ("Approve Dispatch",              PermissionCodes.DELIVERY_APPROVE,      "Approve a staged delivery for goods issue when its fulfillment route has an approval step"),
        ("View Fulfillment Routes",       PermissionCodes.FULFILLMENT_ROUTE_VIEW,   "Open the fulfillment routes settings screen"),
        ("Manage Fulfillment Routes",     PermissionCodes.FULFILLMENT_ROUTE_MANAGE, "Create, edit, deactivate and delete fulfillment routes, and choose the organization's default routes"),
        ("Assign Fulfillment Routes",     PermissionCodes.FULFILLMENT_ROUTE_ASSIGN, "Set a product variant's default fulfillment route, one by one or in bulk by category"),

        ("View Reports",                  PermissionCodes.REPORT_VIEW,           "Access standard reports and dashboards"),
        ("Export Reports",                PermissionCodes.REPORT_EXPORT,         "Download report data to CSV / Excel"),

        ("Manage Workflow Definitions",   PermissionCodes.WORKFLOW_ADMIN,        "Configure workflow definitions and approval steps"),
        ("View Workflow Status",          PermissionCodes.WORKFLOW_VIEW,         "View workflow approval status and history"),
        ("QC Confirm GRN",                PermissionCodes.GRN_QC_CONFIRM,        "Quality-control sign-off on goods receipt notes"),
        ("Approve GRN",                   PermissionCodes.GRN_APPROVE,           "Final inventory manager approval of GRNs"),
        ("Finance Approve GRN",           PermissionCodes.GRN_FINANCE_APPROVE,   "Finance sign-off on GRN value"),

        ("View Material Management",      PermissionCodes.MATERIAL_VIEW,         "Read projects, material issue requests/vouchers, wastage, returns, and cost ledgers"),
        ("Manage Material Management",    PermissionCodes.MATERIAL_MANAGE,       "Create/update projects, issue and post MIRs/MIVs, approve wastage, and process returns"),

        ("View Sale Order Configuration",   PermissionCodes.SALE_ORDER_CONFIG_READ,  "Read the organization's sale order / auto-PO configuration"),
        ("Change Sale Order Configuration", PermissionCodes.SALE_ORDER_CONFIG_WRITE, "Change the organization's sale order / auto-PO configuration"),

        ("View Sale Orders",    PermissionCodes.SALE_ORDER_VIEW,    "Read sale order records"),
        ("Create Sale Orders",  PermissionCodes.SALE_ORDER_CREATE,  "Raise new sale orders"),
        ("Edit Sale Orders",    PermissionCodes.SALE_ORDER_EDIT,    "Amend a draft sale order"),
        ("Confirm Sale Orders", PermissionCodes.SALE_ORDER_CONFIRM, "Confirm a sale order for fulfilment"),
        ("Cancel Sale Orders",  PermissionCodes.SALE_ORDER_CANCEL,  "Cancel a sale order"),
        ("Reserve Stock for Sale Orders", PermissionCodes.SALE_ORDER_RESERVE, "Hold inventory against a confirmed sale order's lines"),
        ("Release Sale Order Reservations", PermissionCodes.SALE_ORDER_RELEASE_RESERVATION, "Give back inventory held for a sale order's lines"),

        ("View Sale Inquiries",   PermissionCodes.SALE_INQUIRY_VIEW,   "Read customer inquiries and their line evaluations"),
        ("Create Sale Inquiries", PermissionCodes.SALE_INQUIRY_CREATE, "Record a new customer inquiry"),
        ("Edit Sale Inquiries",   PermissionCodes.SALE_INQUIRY_EDIT,   "Change inquiry lines, evaluate them, and complete or decline an inquiry"),
        ("View Sale Quotations",   PermissionCodes.SALE_QUOTATION_VIEW,   "Read quotations sent or to be sent to customers"),
        ("Create Sale Quotations", PermissionCodes.SALE_QUOTATION_CREATE, "Raise a quotation, on its own or from a reviewed inquiry"),
        ("Edit Sale Quotations",   PermissionCodes.SALE_QUOTATION_EDIT,   "Change a draft quotation, record the customer's responses, accept or reject it"),
        ("Send Sale Quotations",   PermissionCodes.SALE_QUOTATION_SEND,   "Send a quotation to the customer — it can no longer be changed"),
        ("Manage Rejection Reasons", PermissionCodes.SALE_REJECTION_REASON_MANAGE, "Add, rename, reorder and deactivate the reasons inquiry and quotation lines are declined for"),

        ("View Sales Invoices",     PermissionCodes.SALES_INVOICE_VIEW,     "Read and print sales invoices"),
        ("Manage Sales Invoices",   PermissionCodes.SALES_INVOICE_MANAGE,   "Raise a sales invoice from a delivery, amend or delete a draft, and issue it — issuing books the receivable"),
        ("View Customer Payments",  PermissionCodes.CUSTOMER_PAYMENT_VIEW,  "Read money received from customers and how it was applied"),
        ("Record Customer Payments", PermissionCodes.CUSTOMER_PAYMENT_RECORD, "Record a customer's payment and apply it to their invoices — this is what settles them"),
        ("View Customer Ledger",    PermissionCodes.CUSTOMER_LEDGER_VIEW,   "Read a customer's receivables ledger"),

        ("View Product Ledger",     PermissionCodes.PRODUCT_LEDGER_VIEW,    "Read what a product variant cost, its stock value and weighted-average cost, and the product profitability report"),

        ("View Allocations",        PermissionCodes.ALLOCATION_VIEW,  "Read which demand holds which stock, open demands, expected supply and availability"),
        ("Run Allocation",          PermissionCodes.ALLOCATION_RUN,   "Register demands and expected supply, and run the allocation engine by hand"),
        ("Administer Allocations",  PermissionCodes.ALLOCATION_ADMIN, "Release or move a firm allocation between demands and edit the priority rules — this decides who gets scarce stock"),

        ("View Bills of Materials",     PermissionCodes.BOM_VIEW,     "Read recipes, their versions and what each version changed"),
        ("Create Bills of Materials",   PermissionCodes.BOM_CREATE,   "Draft a new recipe or a new version of one"),
        ("Edit Bills of Materials",     PermissionCodes.BOM_EDIT,     "Change or delete a recipe that is still a draft"),
        ("Submit Bills of Materials",   PermissionCodes.BOM_SUBMIT,   "Send a draft recipe for approval"),
        ("Approve Bills of Materials",  PermissionCodes.BOM_APPROVE,  "Approve or reject a submitted recipe — never one's own submission"),
        ("Activate Bills of Materials", PermissionCodes.BOM_ACTIVATE, "Make an approved recipe the one production uses; retires the previous one"),
        ("Obsolete Bills of Materials", PermissionCodes.BOM_OBSOLETE, "Retire a recipe so no new production order uses it"),
        ("Administer Bills of Materials", PermissionCodes.BOM_ADMIN,  "Move an unreleased production order onto a newer recipe"),

        ("View Production Orders",      PermissionCodes.PROD_VIEW,     "Read production orders, their material requirements, shortages and issues"),
        ("Create Production Orders",    PermissionCodes.PROD_CREATE,   "Raise and amend draft production orders"),
        ("Plan Production Orders",      PermissionCodes.PROD_PLAN,     "Release an order: explode its recipe, hold materials and raise supply for shortages"),
        ("Start Production",            PermissionCodes.PROD_START,    "Start an order whose materials are ready"),
        ("Report Production Output",    PermissionCodes.PROD_REPORT,   "Report quantities produced and mark production complete"),
        ("Cancel Production Orders",    PermissionCodes.PROD_CANCEL,   "Cancel an order and free what was held for it"),
        ("Manage Production",           PermissionCodes.PROD_MANAGER,  "Production manager decisions, including issuing beyond the requirement"),
        ("Create Material Issues",      PermissionCodes.MI_CREATE,     "Draft an issue of materials to the production floor, or a return from it"),
        ("Confirm Material Issues",     PermissionCodes.MI_CONFIRM,    "Confirm an issue — this moves the stock"),
        ("Reverse Material Issues",     PermissionCodes.MI_REVERSE,    "Undo a confirmed issue, putting the stock back"),
        ("View Supply Requirements",    PermissionCodes.SUPPLY_VIEW,   "Read what is short and what was raised to cover it"),
        ("Create Supply Requirements",  PermissionCodes.SUPPLY_CREATE, "Raise a supply requirement by hand"),
        ("Cancel Supply Requirements",  PermissionCodes.SUPPLY_CANCEL, "Cancel a supply requirement"),

        ("Record Quality Inspections",  PermissionCodes.QI_CREATE,     "Log an inspection of produced output and read one back"),
        ("Decide Quality Inspections",  PermissionCodes.QI_APPROVE,    "Record the pass/reject/hold/rework decision — never the person who ran production"),
        ("Create Finished Goods Receipts", PermissionCodes.FGR_CREATE,  "Draft a receipt of accepted production output into inventory"),
        ("Confirm Finished Goods Receipts", PermissionCodes.FGR_CONFIRM, "Confirm a receipt — credits inventory and completes the production order"),
        ("View Production Ledger",      PermissionCodes.PROD_LEDGER_VIEW, "Read the debit/credit history of material consumed and goods produced, across production orders"),

        ("View Accounting Integration",   PermissionCodes.INTEGRATION_VIEW,   "See the QuickBooks connection, mappings, sync dashboard and each record's sync status"),
        ("Manage Accounting Integration", PermissionCodes.INTEGRATION_MANAGE, "Connect or disconnect QuickBooks, change account/tax mappings, issue API keys and switch syncing live"),
        ("Run Accounting Sync",           PermissionCodes.INTEGRATION_SYNC,   "Push a record to QuickBooks now and retry one that failed"),

        ("Manage Finance Setup",          PermissionCodes.FINANCE_SETUP_MANAGE, "Create and change tax codes and exchange rates — these decide the tax and conversion every new document uses"),

        // A35 D-16 — multi-currency.
        ("View Currencies",               PermissionCodes.CURRENCY_VIEW,         "See the organization's currencies (every document form's currency picker needs it)"),
        ("Manage Currencies",             PermissionCodes.CURRENCY_MANAGE,       "Add currencies to the organization, change their formatting, activate or deactivate them"),
        ("View Exchange Rates",           PermissionCodes.CURRENCY_RATE_VIEW,    "See exchange rates and convert amounts between currencies"),
        ("Manage Exchange Rates",         PermissionCodes.CURRENCY_RATE_MANAGE,  "Enter new exchange rates and correct historical ones"),
        ("Manage Base Currencies",        PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE, "Change the organization's sale/purchase/service base currencies and exchange-difference account codes"),
        ("Run Exchange Revaluation",      PermissionCodes.EXCHANGE_REVALUATION_RUN, "Revalue open foreign-currency receivables and payables at a date's rates (unrealized exchange differences)"),

        // A36 D-12 — service orders.
        ("View Service Orders",           PermissionCodes.SERVICE_ORDER_VIEW,     "Read service orders, their materials and ledger, and the service dashboard"),
        ("Create Service Orders",         PermissionCodes.SERVICE_ORDER_CREATE,   "Raise a service order by hand"),
        ("Edit Service Orders",           PermissionCodes.SERVICE_ORDER_EDIT,     "Update, plan and start a service order, add or remove ad-hoc materials, reserve stock and close it"),
        ("Complete Service Orders",       PermissionCodes.SERVICE_ORDER_COMPLETE, "Record what a service order consumed, its actual hours and the customer's sign-off"),
        ("Cancel Service Orders",         PermissionCodes.SERVICE_ORDER_CANCEL,   "Cancel a service order, releasing its reservations and returning issued material"),

        // A37 D-17 — module registry and the customer master.
        ("View Modules",                  PermissionCodes.MODULES_VIEW,           "See the organization's modules, their history and what switching one off affects"),
        ("Manage Modules",                PermissionCodes.MODULES_MANAGE,         "Switch licensed modules and their features on and off for the organization"),
        ("View Customers",                PermissionCodes.CUSTOMER_VIEW,          "Read customers, their balances and the customer search"),
        ("Create Customers",              PermissionCodes.CUSTOMER_CREATE,        "Add new customers"),
        ("Edit Customers",                PermissionCodes.CUSTOMER_EDIT,          "Update existing customers"),
        ("Deactivate Customers",          PermissionCodes.CUSTOMER_DEACTIVATE,    "Deactivate and reactivate customers"),

        // A38 D-13 — point of sale.
        ("View POS Stores",               PermissionCodes.POS_STORE_VIEW,         "See the organization's stores, their configuration and staff"),
        ("Manage POS Stores",             PermissionCodes.POS_STORE_MANAGE,       "Create, change and deactivate stores and assign their staff and PINs"),
        ("View POS Terminals",            PermissionCodes.POS_TERMINAL_VIEW,      "See a store's terminals and their status"),
        ("Manage POS Terminals",          PermissionCodes.POS_TERMINAL_MANAGE,    "Create terminals, bind and unbind devices, force a sync or a logout"),
        ("Open POS Shifts",               PermissionCodes.POS_SHIFT_OPEN,         "Open a shift on a terminal with an opening cash float"),
        ("Close POS Shifts",              PermissionCodes.POS_SHIFT_CLOSE,        "Start closing, resume and close a shift with the counted cash"),
        ("Reconcile POS Shifts",          PermissionCodes.POS_SHIFT_RECONCILE,    "Resolve a disputed shift's cash variance and add notes to reconciled shifts"),
        ("View POS Shifts",               PermissionCodes.POS_SHIFT_VIEW,         "Read shifts, their cash movements and summaries"),
        ("Sell at POS",                   PermissionCodes.POS_ORDER_CREATE,       "Sign in at a terminal with a PIN and ring up sales"),
        ("View POS Orders",               PermissionCodes.POS_ORDER_VIEW,         "Read every POS order and receipt of the organization"),
        ("Void POS Orders",               PermissionCodes.POS_ORDER_VOID,         "Void a sale within the same shift — reverses its invoice, payments and stock"),
        ("Process POS Returns",           PermissionCodes.POS_ORDER_RETURN,       "Take goods back against a POS sale and refund the customer"),
        ("Give POS Discounts",            PermissionCodes.POS_ORDER_DISCOUNT,     "Apply line and order discounts at the till"),
        ("Record POS Cash Movements",     PermissionCodes.POS_CASH_MOVEMENT,      "Record cash paid into or taken out of a till during a shift"),
        ("Manage POS Product Settings",   PermissionCodes.POS_PRODUCT_SETTINGS,   "Change how products appear on the POS (name, category, colour, favourites)"),
        ("Manage POS Customer Settings",  PermissionCodes.POS_CUSTOMER_SETTINGS,  "Change a customer's POS credit settings"),
        ("View POS Reports",              PermissionCodes.POS_REPORTS_VIEW,       "Read POS sales, shift and cashier reports"),

        // A39 D-9 — POS returns, exchanges and store credit.
        ("Process Cross-Store Returns",   PermissionCodes.POS_RETURN_CROSS_STORE,   "Take back goods sold at another store of the organization"),
        ("Override POS Return Policy",    PermissionCodes.POS_RETURN_OVERRIDE,      "Authorize a return outside policy with a manager PIN — past the window, above the no-authorization limit or for a manager-only reason"),
        ("Manage POS Return Policies",    PermissionCodes.POS_RETURN_POLICY_MANAGE, "Set the organization's default return policy and each store's own (windows, restocking fee, refund methods, store credit expiry)"),
        ("Manage POS Return Reasons",     PermissionCodes.POS_RETURN_REASON_MANAGE, "Add, change, reorder and deactivate the reasons a return or exchange is made for"),
        ("View POS Return Reports",       PermissionCodes.POS_RETURN_REPORTS,       "Read return and exchange reports and the cross-store reconciliation report"),
        ("Issue Store Credit",            PermissionCodes.POS_CREDIT_NOTE_ISSUE,    "Issue store credit, standalone or as the refund of a return"),
        ("Redeem Store Credit",           PermissionCodes.POS_CREDIT_NOTE_REDEEM,   "Accept store credit as payment at the till"),
        ("Cancel Store Credit",           PermissionCodes.POS_CREDIT_NOTE_CANCEL,   "Cancel a store credit note's remaining balance"),
        ("View Store Credit",             PermissionCodes.POS_CREDIT_NOTE_VIEW,     "Read store credit notes, their balances and transactions"),
        ("View Store Credit Reports",     PermissionCodes.POS_CREDIT_NOTE_REPORTS,  "Read store credit reports (outstanding liability, issued, redeemed, expired)"),

        // A40 D-9 — data import.
        ("View Import Profiles",          PermissionCodes.IMPORT_PROFILE_VIEW,    "See what can be imported, the fields of each import and the migration checklist"),
        ("Manage Import Profiles",        PermissionCodes.IMPORT_PROFILE_MANAGE,  "Change an import's duplicate handling and file limits for the organization"),
        ("Upload Import Files",           PermissionCodes.IMPORT_UPLOAD,          "Download import templates, upload files, map their columns and follow one's own imports"),
        ("Execute Imports",               PermissionCodes.IMPORT_EXECUTE,         "Validate and run an import — this creates and updates the organization's records"),
        ("Manage All Imports",            PermissionCodes.IMPORT_MANAGE,          "See and cancel every user's imports"),
        ("Import Opening Balances",       PermissionCodes.IMPORT_OPENING_BALANCE, "Import opening stock and customer/supplier opening balances"),

        // A41 D-3 — CRM.
        ("View Leads",                    PermissionCodes.CRM_VIEW_LEADS,          "See leads, their timeline, the lead kanban and the CRM lookups (sources, stages, teams)"),
        ("Create Leads",                  PermissionCodes.CRM_CREATE_LEAD,         "Capture new leads"),
        ("Edit Leads",                    PermissionCodes.CRM_EDIT_LEAD,           "Change a lead and move it through its stages — sales reps only their own"),
        ("Assign Leads",                  PermissionCodes.CRM_ASSIGN_LEAD,         "Assign and reassign leads to salespeople and teams; may change any lead or opportunity"),
        ("Qualify Leads",                 PermissionCodes.CRM_QUALIFY_LEAD,        "Mark a contacted lead as qualified"),
        ("Convert Leads",                 PermissionCodes.CRM_CONVERT_LEAD,        "Turn a qualified lead into a customer, a contact person and an opportunity"),
        ("Disqualify Leads",              PermissionCodes.CRM_DISQUALIFY_LEAD,     "Close a lead as not a valid prospect, with a reason"),
        ("Re-open Leads",                 PermissionCodes.CRM_REOPEN_LEAD,         "Bring a disqualified lead back to New"),
        ("Delete Leads",                  PermissionCodes.CRM_DELETE_LEAD,         "Remove a lead that was not converted"),
        ("View Opportunities",            PermissionCodes.CRM_VIEW_OPPORTUNITIES,  "See opportunities, their pipeline and timeline"),
        ("Edit Opportunities",            PermissionCodes.CRM_EDIT_OPPORTUNITY,    "Change an opportunity, move its stage and raise its inquiry, quotation or sale order — sales reps only their own"),
        ("Close Opportunities",           PermissionCodes.CRM_CLOSE_OPPORTUNITY,   "Mark an opportunity won or lost — sales reps only their own"),
        ("Re-open Opportunities",         PermissionCodes.CRM_REOPEN_OPPORTUNITY,  "Bring a lost opportunity back to Open"),
        ("View Activities",               PermissionCodes.CRM_VIEW_ACTIVITIES,     "See calls, meetings, notes, tasks, documents and follow-ups"),
        ("Log Activities",                PermissionCodes.CRM_CREATE_ACTIVITY,     "Record calls, meetings, notes, tasks and documents shared with a prospect"),
        ("Edit Activities",               PermissionCodes.CRM_EDIT_ACTIVITY,       "Change or complete an activity — sales reps only their own"),
        ("Delete Activities",             PermissionCodes.CRM_DELETE_ACTIVITY,     "Remove an activity"),
        ("Export Timelines",              PermissionCodes.CRM_EXPORT_TIMELINE,     "Download a lead's or opportunity's timeline as an Excel workbook"),
        ("View Campaigns",                PermissionCodes.CRM_VIEW_CAMPAIGNS,      "See marketing campaigns and the leads they brought"),
        ("Manage Campaigns",              PermissionCodes.CRM_MANAGE_CAMPAIGNS,    "Create, change and remove campaigns"),
        ("Administer CRM",                PermissionCodes.CRM_ADMIN,               "Configure lead sources, pipeline stages, sales teams and the public lead API keys"),

        // A42 D-14 — general ledger setup.
        ("View General Ledger",           PermissionCodes.GL_VIEW,                 "See the chart of accounts, templates, fiscal years, accounting defaults, opening balances, external mappings and the GL setup status"),
        ("Manage Chart of Accounts",      PermissionCodes.GL_ACCOUNT_MANAGE,       "Add, change and remove accounts in the chart of accounts"),
        ("Manage General Ledger Setup",   PermissionCodes.GL_SETUP_MANAGE,         "Apply or upload a chart of accounts, set fiscal years and periods, accounting defaults and opening balances, complete or reset the GL setup, map accounts to external systems, and manage journal types"),

        // A47 D-15 — general ledger journal engine.
        ("Create Journal Entries",        PermissionCodes.GL_JOURNAL_CREATE,       "Enter manual journal entries (saved as drafts)"),
        ("Edit Journal Entries",          PermissionCodes.GL_JOURNAL_EDIT,         "Change draft manual journal entries"),
        ("Delete Journal Entries",        PermissionCodes.GL_JOURNAL_DELETE,       "Delete draft manual journal entries"),
        ("Post Journal Entries",          PermissionCodes.GL_JOURNAL_POST,         "Post draft journal entries to the general ledger (account balances move)"),
        ("Reverse Journal Entries",       PermissionCodes.GL_JOURNAL_REVERSE,      "Reverse posted journal entries with a mirror entry, including entries posted from source documents"),
        ("View Reconciliation",           PermissionCodes.GL_RECONCILE_VIEW,       "See open (unreconciled) journal lines and reconciliation suggestions"),
        ("Reconcile Journal Lines",       PermissionCodes.GL_RECONCILE_EXECUTE,    "Reconcile and unreconcile journal lines"),
        ("View GL Reports",               PermissionCodes.GL_REPORTS_VIEW,         "See the trial balance, account balances and account ledgers, and export them"),
        ("View Period Close",             PermissionCodes.GL_PERIOD_CLOSE_VIEW,    "See the close status of fiscal periods and preview the year-end closing entry"),
        ("Close Periods",                 PermissionCodes.GL_PERIOD_CLOSE_EXECUTE, "Lock periods, set them to adjustment-only and reopen locked periods"),
        ("Administer Period Close",       PermissionCodes.GL_PERIOD_CLOSE_ADMIN,   "Close periods finally (irreversible) and run the year-end close"),
    ];

    /// <summary>
    /// A42 D-14 — GL grants by built-in role CODE: the Finance Manager (holds FINANCE_SETUP_MANAGE) gets all three; the
    /// Finance Officer (INVOICE_VIEW) and the Auditor get GL_VIEW. System Admin and Org Admin hold every code through
    /// <see cref="PermissionCodes.All"/>. <see cref="SeedGlGrantsAsync"/> also extends the rule to custom roles by what
    /// they hold. Only MISSING grants are added.
    /// </summary>
    internal static readonly (string RoleCode, string[] Codes)[] GlGrants =
    [
        ("FINANCE_MANAGER", [PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE]),
        ("FINANCE_OFFICER", [PermissionCodes.GL_VIEW]),
        ("AUDITOR",         [PermissionCodes.GL_VIEW]),
    ];

    /// <summary>A42 D-14 — a role (built-in or custom) allowed FINANCE_SETUP_MANAGE gets every GL code.</summary>
    internal static readonly string[] GlSetupHolderCodes = [PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE];

    /// <summary>A42 D-14 — a role (built-in or custom) allowed INVOICE_VIEW or SALES_INVOICE_VIEW gets GL_VIEW.</summary>
    internal static readonly string[] GlViewSourceCodes = [PermissionCodes.INVOICE_VIEW, PermissionCodes.SALES_INVOICE_VIEW];

    /// <summary>
    /// A47 D-15 — wherever GL_VIEW is granted by the rules above, or a role is allowed GL_VIEW, the three journal-engine
    /// read codes come with it (unless the role's GL_VIEW was switched off). The journal write / reconcile / close codes
    /// go to System Admin and Org Admin only (through <see cref="PermissionCodes.All"/>); others are granted by hand.
    /// </summary>
    internal static readonly string[] GlEngineViewCodes = [PermissionCodes.GL_RECONCILE_VIEW, PermissionCodes.GL_REPORTS_VIEW, PermissionCodes.GL_PERIOD_CLOSE_VIEW];

    /// <summary>A42 — see <see cref="GlGrants"/>. Idempotent; a grant an administrator switched off stays off.</summary>
    internal async Task SeedGlGrantsAsync()
    {
        var codes = GlSetupHolderCodes.Concat(GlViewSourceCodes).Concat(GlEngineViewCodes).Append(PermissionCodes.FINANCE_SETUP_MANAGE).Distinct().ToList();
        var permIds = await _db.Permissions.Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        var grants = await _db.RolePermissions.IgnoreQueryFilters().Select(rp => new { rp.RoleID, rp.PermissionID, rp.IsAllowed }).ToListAsync();
        var existing = grants.Select(g => (g.RoleID, g.PermissionID)).ToHashSet();

        var wanted = new List<(int RoleId, string Code)>();
        var roleCodes = GlGrants.Select(g => g.RoleCode).ToList();
        var roleIds = await _db.Roles.IgnoreQueryFilters().Where(r => roleCodes.Contains(r.RoleCode))
            .ToDictionaryAsync(r => r.RoleCode, r => r.RoleID);
        foreach (var (roleCode, grantCodes) in GlGrants)
            if (roleIds.TryGetValue(roleCode, out var roleId)) wanted.AddRange(grantCodes.Select(c => (roleId, c)));

        int? Id(string code) => permIds.TryGetValue(code, out var id) ? id : null;
        var setupId = Id(PermissionCodes.FINANCE_SETUP_MANAGE);
        var viewSourceIds = GlViewSourceCodes.Select(Id).Where(i => i is not null).Select(i => i!.Value).ToHashSet();
        foreach (var g in grants.Where(g => g.IsAllowed))
        {
            if (g.PermissionID == setupId) wanted.AddRange(GlSetupHolderCodes.Select(c => (g.RoleID, c)));
            else if (viewSourceIds.Contains(g.PermissionID)) wanted.Add((g.RoleID, PermissionCodes.GL_VIEW));
        }

        // A47 D-15 — the journal-engine read codes follow GL_VIEW (wanted above, or already allowed).
        var viewId = Id(PermissionCodes.GL_VIEW);
        var viewOff = grants.Where(g => g.PermissionID == viewId && !g.IsAllowed).Select(g => g.RoleID).ToHashSet();
        var viewRoles = wanted.Where(w => w.Code == PermissionCodes.GL_VIEW).Select(w => w.RoleId)
            .Concat(grants.Where(g => g.PermissionID == viewId && g.IsAllowed).Select(g => g.RoleID))
            .Where(r => !viewOff.Contains(r)).Distinct().ToList();
        foreach (var roleId in viewRoles) wanted.AddRange(GlEngineViewCodes.Select(c => (roleId, c)));

        foreach (var (roleId, code) in wanted)
        {
            if (Id(code) is not { } permId || !existing.Add((roleId, permId))) continue;
            _db.RolePermissions.Add(new RolePermission
            {
                RoleID = roleId, PermissionID = permId, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId
            });
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>A41 D-4 — RoleSeed / RolePermissionSeed labels of the CRM built-in roles (resolved by code like the rest).</summary>
    internal const int SalesRepSeedId = 15, SalesManagerSeedId = 16;

    /// <summary>A41 D-4 — Appendix A "Sales Rep" codes plus what raising a sales document from an opportunity needs.</summary>
    internal static readonly string[] SalesRepCodes =
    [
        PermissionCodes.CRM_VIEW_LEADS, PermissionCodes.CRM_CREATE_LEAD, PermissionCodes.CRM_EDIT_LEAD,
        PermissionCodes.CRM_QUALIFY_LEAD, PermissionCodes.CRM_CONVERT_LEAD, PermissionCodes.CRM_DISQUALIFY_LEAD,
        PermissionCodes.CRM_VIEW_OPPORTUNITIES, PermissionCodes.CRM_EDIT_OPPORTUNITY, PermissionCodes.CRM_CLOSE_OPPORTUNITY,
        PermissionCodes.CRM_VIEW_ACTIVITIES, PermissionCodes.CRM_CREATE_ACTIVITY, PermissionCodes.CRM_EDIT_ACTIVITY,
        PermissionCodes.CRM_EXPORT_TIMELINE, PermissionCodes.CRM_VIEW_CAMPAIGNS,
        PermissionCodes.CUSTOMER_VIEW,
        PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_CREATE,
        PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_QUOTATION_CREATE,
        PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE,
    ];

    /// <summary>A41 D-4 — every CRM code but CRM_ADMIN, the rep's sales codes, and editing/sending/confirming them.</summary>
    internal static readonly string[] SalesManagerCodes =
    [
        .. SalesRepCodes,
        PermissionCodes.CRM_ASSIGN_LEAD, PermissionCodes.CRM_REOPEN_LEAD, PermissionCodes.CRM_DELETE_LEAD,
        PermissionCodes.CRM_REOPEN_OPPORTUNITY, PermissionCodes.CRM_DELETE_ACTIVITY, PermissionCodes.CRM_MANAGE_CAMPAIGNS,
        PermissionCodes.CUSTOMER_CREATE, PermissionCodes.CUSTOMER_EDIT,
        PermissionCodes.SALE_INQUIRY_EDIT, PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.SALE_QUOTATION_SEND,
        PermissionCodes.SALE_ORDER_EDIT, PermissionCodes.SALE_ORDER_CONFIRM,
    ];

    /// <summary>
    /// A41 follow-up — the read-only Auditor ("view all records, export reports, no data modification") sees CRM: leads,
    /// opportunities, activities, campaigns and the timeline export, nothing that changes data. Only MISSING grants are
    /// added (see <see cref="SeedCrmViewGrantsAsync"/>); a grant an administrator switched off stays off.
    /// </summary>
    internal static readonly (string RoleCode, string[] Codes)[] CrmViewGrants =
    [
        ("AUDITOR", [PermissionCodes.CRM_VIEW_LEADS, PermissionCodes.CRM_VIEW_OPPORTUNITIES, PermissionCodes.CRM_VIEW_ACTIVITIES,
                     PermissionCodes.CRM_VIEW_CAMPAIGNS, PermissionCodes.CRM_EXPORT_TIMELINE]),
    ];

    /// <summary>Idempotent; runs on every start so an existing database's Auditor picks the grants up on the next start.</summary>
    internal async Task SeedCrmViewGrantsAsync()
    {
        var codes = CrmViewGrants.SelectMany(g => g.Codes).Distinct().ToList();
        var permIds = await _db.Permissions.Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        var roleCodes = CrmViewGrants.Select(g => g.RoleCode).ToList();
        var roleIds = await _db.Roles.IgnoreQueryFilters().Where(r => roleCodes.Contains(r.RoleCode))
            .ToDictionaryAsync(r => r.RoleCode, r => r.RoleID);
        var existing = (await _db.RolePermissions.IgnoreQueryFilters().Select(rp => new { rp.RoleID, rp.PermissionID }).ToListAsync())
            .Select(x => (x.RoleID, x.PermissionID)).ToHashSet();

        foreach (var (roleCode, grantCodes) in CrmViewGrants)
        {
            if (!roleIds.TryGetValue(roleCode, out var roleId)) continue;
            foreach (var code in grantCodes)
            {
                if (!permIds.TryGetValue(code, out var permId) || !existing.Add((roleId, permId))) continue;
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleID = roleId, PermissionID = permId, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// A40 D-9 / D-11 (IMP) — import grants by built-in role CODE: every built-in role holding a write permission gets
    /// view + upload; the finance, inventory and procurement managers/officers also execute; the Finance Manager imports
    /// opening balances. System Admin and Org Admin hold every code through <see cref="PermissionCodes.All"/>. The read-only
    /// Auditor and the POS till roles get none. Only MISSING grants are added (see <see cref="SeedImportGrantsAsync"/>).
    /// </summary>
    internal static readonly (string RoleCode, string[] Codes)[] ImportGrants =
    [
        ("PROCUREMENT_MANAGER", [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE]),
        ("PURCHASE_OFFICER",    [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD]),
        ("INVENTORY_MANAGER",   [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE]),
        ("WAREHOUSE_OPERATOR",  [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD]),
        ("FINANCE_OFFICER",     [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE]),
        ("REQUESTER",           [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD]),
        ("FINANCE_MANAGER",     [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE,
                                 PermissionCodes.IMPORT_OPENING_BALANCE]),
        ("SUPPLY_DEPT_ADMIN",   [PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD]),
    ];

    /// <summary>A40 — see <see cref="ImportGrants"/>. Idempotent; a grant an administrator switched off stays off.</summary>
    internal async Task SeedImportGrantsAsync()
    {
        var codes = ImportGrants.SelectMany(g => g.Codes).Distinct().ToList();
        var permIds = await _db.Permissions.Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        var roleCodes = ImportGrants.Select(g => g.RoleCode).ToList();
        var roleIds = await _db.Roles.IgnoreQueryFilters().Where(r => roleCodes.Contains(r.RoleCode))
            .ToDictionaryAsync(r => r.RoleCode, r => r.RoleID);
        var existing = (await _db.RolePermissions.IgnoreQueryFilters().Select(rp => new { rp.RoleID, rp.PermissionID }).ToListAsync())
            .Select(x => (x.RoleID, x.PermissionID)).ToHashSet();

        foreach (var (roleCode, grantCodes) in ImportGrants)
        {
            if (!roleIds.TryGetValue(roleCode, out var roleId)) continue;
            foreach (var code in grantCodes)
            {
                if (!permIds.TryGetValue(code, out var permId) || !existing.Add((roleId, permId))) continue;
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleID = roleId, PermissionID = permId, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>A38 D-13 — RoleSeed / RolePermissionSeed labels of the POS built-in roles (resolved by code like the rest).</summary>
    internal const int PosCashierSeedId = 12, PosSupervisorSeedId = 13, StoreManagerSeedId = 14;

    internal static readonly string[] PosCashierCodes =
    [
        PermissionCodes.POS_ORDER_CREATE,
        PermissionCodes.POS_CREDIT_NOTE_REDEEM, // A39 D-9
    ];

    internal static readonly string[] PosSupervisorCodes =
    [
        .. PosCashierCodes,
        PermissionCodes.POS_ORDER_VIEW, PermissionCodes.POS_ORDER_DISCOUNT, PermissionCodes.POS_ORDER_VOID, PermissionCodes.POS_ORDER_RETURN,
        PermissionCodes.POS_SHIFT_OPEN, PermissionCodes.POS_SHIFT_CLOSE, PermissionCodes.POS_SHIFT_VIEW,
        PermissionCodes.POS_CASH_MOVEMENT, PermissionCodes.POS_TERMINAL_VIEW, PermissionCodes.POS_STORE_VIEW,
        // A39 D-9 (spec §16.2)
        PermissionCodes.POS_RETURN_CROSS_STORE, PermissionCodes.POS_RETURN_REPORTS,
        PermissionCodes.POS_CREDIT_NOTE_ISSUE, PermissionCodes.POS_CREDIT_NOTE_VIEW,
    ];

    internal static readonly string[] StoreManagerCodes =
    [
        .. PosSupervisorCodes,
        PermissionCodes.POS_SHIFT_RECONCILE, PermissionCodes.POS_TERMINAL_MANAGE,
        PermissionCodes.POS_PRODUCT_SETTINGS, PermissionCodes.POS_CUSTOMER_SETTINGS, PermissionCodes.POS_REPORTS_VIEW,
        PermissionCodes.CUSTOMER_VIEW,
        // A39 D-9 (spec §16.2)
        PermissionCodes.POS_RETURN_OVERRIDE, PermissionCodes.POS_CREDIT_NOTE_CANCEL, PermissionCodes.POS_CREDIT_NOTE_REPORTS,
    ];

    /// <summary>A35 D-16 — granted to every role, built-in or custom, existing or created later (document forms need them).</summary>
    internal static readonly string[] EveryRoleCodes = [PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW];

    /// <summary>A35 D-16 — granted to every role that holds FINANCE_SETUP_MANAGE (it already owned exchange rates).</summary>
    internal static readonly string[] FinanceSetupCodes = [PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.EXCHANGE_REVALUATION_RUN];

    /// <summary>
    /// A37 D-17 — customers are business partners (D-13), so whoever handles partners today handles customers: each
    /// customer code goes to every role (built-in or custom) allowed its supplier counterpart.
    /// </summary>
    internal static readonly (string Customer, string Supplier)[] CustomerGrantPairs =
    [
        (PermissionCodes.CUSTOMER_VIEW,       PermissionCodes.SUPPLIER_VIEW),
        (PermissionCodes.CUSTOMER_CREATE,     PermissionCodes.SUPPLIER_CREATE),
        (PermissionCodes.CUSTOMER_EDIT,       PermissionCodes.SUPPLIER_EDIT),
        (PermissionCodes.CUSTOMER_DEACTIVATE, PermissionCodes.SUPPLIER_MANAGE),
    ];

    private async Task SeedPermissionsAsync()
    {
        foreach (var (name, code, desc) in PermissionSeed)
        {
            if (!await _db.Permissions.AnyAsync(p => p.Code == code))
                _db.Permissions.Add(new Permission { Name = name, Code = code, Description = desc });
        }
        await _db.SaveChangesAsync();

        // A37 D-14 — every permission's module, from the code-prefix map (kept in step on every start).
        foreach (var permission in await _db.Permissions.ToListAsync())
        {
            var module = ModuleCodeMap.ForPermission(permission.Code);
            if (permission.ModuleCode != module) permission.ModuleCode = module;
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// A37 D-17 — see <see cref="CustomerGrantPairs"/>. Like <see cref="SeedCurrencyGrantsAsync"/>, only MISSING grants
    /// are added: a grant an administrator switched off (IsAllowed = false) stays off. Idempotent, every start.
    /// </summary>
    internal async Task SeedCustomerGrantsAsync()
    {
        var codes = CustomerGrantPairs.SelectMany(p => new[] { p.Customer, p.Supplier }).ToList();
        var permIds = await _db.Permissions.Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        var grants = await _db.RolePermissions.IgnoreQueryFilters().Select(rp => new { rp.RoleID, rp.PermissionID, rp.IsAllowed }).ToListAsync();
        var existing = grants.Select(g => (g.RoleID, g.PermissionID)).ToHashSet();

        foreach (var (customer, supplier) in CustomerGrantPairs)
        {
            if (!permIds.TryGetValue(customer, out var customerId) || !permIds.TryGetValue(supplier, out var supplierId)) continue;
            foreach (var roleId in grants.Where(g => g.PermissionID == supplierId && g.IsAllowed).Select(g => g.RoleID).Distinct())
            {
                if (!existing.Add((roleId, customerId))) continue;
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleID = roleId, PermissionID = customerId, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    // ── 2. Roles ──────────────────────────────────────────────────────────────

    private static readonly (int Id, string Name, string Code, string Description)[] RoleSeed =
    [
        ((int)EnumRole.SystemAdmin,        "System Admin",         "SYSTEM_ADMIN",         "Full access — configure system, manage users, all modules, audit logs, global settings"),
        ((int)EnumRole.ProcurementManager, "Procurement Manager",  "PROCUREMENT_MANAGER",  "Managerial — approve POs, manage suppliers, RFQs, contracts, budgets"),
        ((int)EnumRole.PurchaseOfficer,    "Purchase Officer",     "PURCHASE_OFFICER",     "Operational — create requisitions, issue POs, track deliveries, view inventory"),
        ((int)EnumRole.InventoryManager,   "Inventory Manager",    "INVENTORY_MANAGER",    "Operational — manage stock, adjustments, receipts, reorder rules, warehouse transfers"),
        ((int)EnumRole.WarehouseOperator,  "Warehouse Operator",   "WAREHOUSE_OPERATOR",   "Restricted — receive goods, put-away, picking, dispatch, update stock locations"),
        ((int)EnumRole.FinanceOfficer,     "Finance Officer",      "FINANCE_OFFICER",      "Operational — process invoices, payments, reconciliation, budget monitoring"),
        ((int)EnumRole.Requester,          "Requester",            "REQUESTER",            "Limited — raise purchase requisitions, view own order status"),
        ((int)EnumRole.Auditor,            "Read-Only / Auditor",  "AUDITOR",              "View only — view all records, export reports, no data modification"),
        ((int)EnumRole.FinanceManager,     "Finance Manager",      "FINANCE_MANAGER",      "Managerial — approve supplier payments, manage budgets, all Finance Officer permissions"),
        ((int)EnumRole.OrgAdmin,           "Organization Admin",   "ORG_ADMIN",            "Full owner/operator of a tenant organization — every task in the app except platform-wide administration"),
        ((int)EnumRole.SupplyDeptAdmin,    "Supply Department Administrator", "SUPPLY_DEPT_ADMIN", "Owns sale order administration & configuration — Deputy Director / Director of Supply (§3.1)"),
        // A38 D-13 — point of sale (spec §16.2).
        (PosCashierSeedId,    "POS Cashier",    "POS_CASHIER",    "Point of sale — signs in at a terminal with a PIN and rings up sales"),
        (PosSupervisorSeedId, "POS Supervisor", "POS_SUPERVISOR", "Point of sale — cashier plus discounts, voids, returns, shifts and cash movements"),
        (StoreManagerSeedId,  "Store Manager",  "STORE_MANAGER",  "Point of sale — supervisor plus shift reconciliation, terminals, POS settings and reports"),
        // A41 D-4 — CRM (spec Appendix A).
        (SalesRepSeedId,      "Sales Representative", "SALES_REP",     "CRM — captures and works own leads, logs activities, converts and closes own opportunities"),
        (SalesManagerSeedId,  "Sales Manager",        "SALES_MANAGER", "CRM — every lead and opportunity: assigns, re-opens, deletes, manages campaigns; edits sales documents"),
    ];

    // Matched by RoleCode, not RoleID: RoleID carries no DB identity (RoleMap.ValueGeneratedNever)
    // and the same integer space is shared with org-created custom roles, which take
    // MAX(RoleID)+1 (AuthRepository.CreateRoleAsync). A custom or test role can end up sitting on a
    // built-in role's intended id before this ever runs — SUPPLY_DEPT_ADMIN's id 11 collided with a
    // hand-created "Test Role" in exactly this way, silently granting that unrelated role
    // SALE_ORDER_CONFIG_WRITE the next time the seeder ran — so code, not position, decides whether
    // a built-in role already exists, and SeedRolePermissionsAsync below resolves the real RoleID
    // the same way rather than trusting RoleSeed's literal id.
    private async Task SeedRolesAsync()
    {
        var existing = await _db.Roles.IgnoreQueryFilters().ToListAsync();

        // A38 (XMOD) — RolePermissions and UserAccounts have no FK to Roles, and the live database has had roles deleted
        // by hand: an id still carrying orphaned grants or users must not be handed to a NEW built-in role, which would
        // silently inherit them. Taken = used by a role, a grant or a user.
        var taken = existing.Select(r => r.RoleID)
            .Concat(await _db.RolePermissions.IgnoreQueryFilters().Select(rp => rp.RoleID).Distinct().ToListAsync())
            .Concat(await _db.UserAccounts.IgnoreQueryFilters().Select(u => u.RoleID).Distinct().ToListAsync())
            .ToHashSet();

        foreach (var (id, name, code, desc) in RoleSeed)
        {
            var role = existing.FirstOrDefault(r => r.RoleCode == code)
                // A pre-RoleCode row (migration AddRoleCodeAndIsActive) sitting at the seed's own
                // id, waiting to be named — the only case where position still means identity.
                ?? existing.FirstOrDefault(r => r.RoleID == id && string.IsNullOrEmpty(r.RoleCode));

            if (role is null)
            {
                // The built-in catalog is global (IsGlobal=true, OrganizationId=null) — usable and
                // assignable by every organization. Org-owned custom roles (IsGlobal=false) are
                // only ever created at runtime via RolesController, never seeded here.
                // Pre-A38 built-in roles keep the old rule (only a role row blocks their seed id): users already pointing
                // at a missing System Admin's id 1 were meant to be System Admins.
                var free = id < PosCashierSeedId ? existing.All(r => r.RoleID != id) : !taken.Contains(id);
                var newId = free ? id : taken.Max() + 1;
                taken.Add(newId);
                role = new Role
                {
                    RoleID = newId, Name = name, RoleCode = code, Description = desc, IsActive = true,
                    IsGlobal = true, OrganizationId = null
                };
                _db.Roles.Add(role);
                existing.Add(role);
            }
            else if (string.IsNullOrEmpty(role.RoleCode))
            {
                role.RoleCode = code;
                role.IsActive = true;
            }
        }
        await _db.SaveChangesAsync();
    }

    // ── 3. Role → Permission mappings ─────────────────────────────────────────

    private static readonly Dictionary<int, string[]> RolePermissionSeed = new()
    {
        // §3.1: "IT Admin / Super Admin may VIEW config, may CHANGE it only if they also hold
        // [SUPPLY_DEPT_ADMIN]" — the one deliberate carve-out from System Admin's otherwise-blanket
        // grant. SALE_ORDER_CONFIG_READ still flows through .All; only WRITE is excluded.
        [(int)EnumRole.SystemAdmin] = PermissionCodes.All
            .Except([PermissionCodes.SALE_ORDER_CONFIG_WRITE])
            .ToArray(),

        [(int)EnumRole.ProcurementManager] =
        [
            PermissionCodes.SUPPLIER_VIEW,    PermissionCodes.SUPPLIER_CREATE,
            PermissionCodes.SUPPLIER_EDIT,    PermissionCodes.SUPPLIER_MANAGE,
            PermissionCodes.RFQ_VIEW,         PermissionCodes.RFQ_CREATE,    PermissionCodes.RFQ_MANAGE,
            PermissionCodes.CONTRACT_VIEW,    PermissionCodes.CONTRACT_MANAGE,
            PermissionCodes.PO_VIEW,          PermissionCodes.PO_CREATE,
            PermissionCodes.PO_EDIT,          PermissionCodes.PO_APPROVE,    PermissionCodes.PO_CANCEL,
            PermissionCodes.BUDGET_VIEW,      PermissionCodes.BUDGET_MANAGE,
            PermissionCodes.REQUISITION_VIEW_ALL, PermissionCodes.REQUISITION_APPROVE,
            PermissionCodes.DELIVERY_TRACK,   PermissionCodes.DELIVERY_VIEW,
            PermissionCodes.DELIVERY_CREATE,  PermissionCodes.DELIVERY_EDIT,
            PermissionCodes.SHIPMENT_BOOK,    PermissionCodes.CARRIER_MANAGE,
            // Somebody who commits money to a carrier needs to see what it costs first.
            PermissionCodes.SHIPMENT_RATE_VIEW,
            // A30 — what a manufactured product needs, so the buyer knows what to source, and
            // the shortages that turn into purchase orders.
            PermissionCodes.BOM_VIEW, PermissionCodes.PROD_VIEW,
            PermissionCodes.SUPPLY_VIEW, PermissionCodes.SUPPLY_CREATE, PermissionCodes.SUPPLY_CANCEL,
            PermissionCodes.REPORT_VIEW,      PermissionCodes.REPORT_EXPORT,
            PermissionCodes.WORKFLOW_VIEW,
            // A36 D-12 — sees the service orders whose subcontracted labour turns into purchase orders.
            PermissionCodes.SERVICE_ORDER_VIEW,
        ],

        [(int)EnumRole.PurchaseOfficer] =
        [
            PermissionCodes.SUPPLIER_VIEW,
            PermissionCodes.REQUISITION_CREATE, PermissionCodes.REQUISITION_VIEW_OWN,
            PermissionCodes.RFQ_VIEW,
            PermissionCodes.PO_VIEW,  PermissionCodes.PO_CREATE,  PermissionCodes.PO_EDIT,
            PermissionCodes.INVENTORY_VIEW,
            PermissionCodes.DELIVERY_TRACK,   PermissionCodes.DELIVERY_VIEW,
            PermissionCodes.REPORT_VIEW,
            PermissionCodes.SERVICE_ORDER_VIEW, // A36 D-12
        ],

        [(int)EnumRole.InventoryManager] =
        [
            PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE,
            PermissionCodes.STOCK_ADJUST,   PermissionCodes.REORDER_MANAGE,
            PermissionCodes.WAREHOUSE_TRANSFER,
            PermissionCodes.GOODS_RECEIVE,  PermissionCodes.PUTAWAY,
            PermissionCodes.GRN_APPROVE,
            // A30 §25 — the Inventory Manager owns who gets scarce stock and how things are made.
            PermissionCodes.ALLOCATION_VIEW, PermissionCodes.ALLOCATION_RUN, PermissionCodes.ALLOCATION_ADMIN,
            PermissionCodes.BOM_VIEW, PermissionCodes.BOM_CREATE, PermissionCodes.BOM_EDIT, PermissionCodes.BOM_SUBMIT,
            PermissionCodes.BOM_APPROVE, PermissionCodes.BOM_ACTIVATE, PermissionCodes.BOM_OBSOLETE, PermissionCodes.BOM_ADMIN,
            PermissionCodes.PROD_VIEW, PermissionCodes.PROD_CREATE, PermissionCodes.PROD_PLAN, PermissionCodes.PROD_START,
            PermissionCodes.PROD_REPORT, PermissionCodes.PROD_CANCEL, PermissionCodes.PROD_MANAGER,
            PermissionCodes.MI_CREATE, PermissionCodes.MI_CONFIRM, PermissionCodes.MI_REVERSE,
            PermissionCodes.SUPPLY_VIEW, PermissionCodes.SUPPLY_CREATE, PermissionCodes.SUPPLY_CANCEL,
            PermissionCodes.QI_CREATE, PermissionCodes.QI_APPROVE, PermissionCodes.FGR_CREATE, PermissionCodes.FGR_CONFIRM,
            PermissionCodes.PROD_LEDGER_VIEW,
            PermissionCodes.REPORT_VIEW,
            // A32 §6.5 — the spec's WAREHOUSE_MANAGER: holds and frees stock for sale order lines, so it must be
            // able to open the order to reach the buttons.
            PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_RESERVE, PermissionCodes.SALE_ORDER_RELEASE_RESERVATION,
            // A33 D-9 — the built-in role holding STOCK_MANAGE: decides how each product is fulfilled.
            PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE, PermissionCodes.FULFILLMENT_ROUTE_ASSIGN,
            // A34 D-24 — owns the organization's lead-time defaults (variant overrides already ride on STOCK_MANAGE).
            PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE,
            // A36 D-12 — the spec's SERVICE_MANAGER: every service order action.
            PermissionCodes.SERVICE_ORDER_VIEW, PermissionCodes.SERVICE_ORDER_CREATE, PermissionCodes.SERVICE_ORDER_EDIT,
            PermissionCodes.SERVICE_ORDER_COMPLETE, PermissionCodes.SERVICE_ORDER_CANCEL,
        ],

        [(int)EnumRole.WarehouseOperator] =
        [
            PermissionCodes.GOODS_RECEIVE,  PermissionCodes.PUTAWAY,
            PermissionCodes.PICKING,        PermissionCodes.DISPATCH,
            PermissionCodes.STOCK_LOCATION_UPDATE,
            PermissionCodes.INVENTORY_VIEW,
            PermissionCodes.GRN_QC_CONFIRM,
            // The role that physically moves the goods works the delivery cockpit.
            PermissionCodes.DELIVERY_VIEW,  PermissionCodes.DELIVERY_EDIT,
            PermissionCodes.SHIPMENT_BOOK,
            PermissionCodes.ALLOCATION_VIEW, PermissionCodes.BOM_VIEW,
            // The floor: sees the orders, starts them, reports output, moves the materials.
            PermissionCodes.PROD_VIEW, PermissionCodes.PROD_START, PermissionCodes.PROD_REPORT,
            PermissionCodes.MI_CREATE, PermissionCodes.MI_CONFIRM,
            // Recording what a check found, and receiving the accepted output — the decision
            // (QI_APPROVE) stays with whoever supervises the floor, same separation as production.
            PermissionCodes.QI_CREATE, PermissionCodes.FGR_CREATE, PermissionCodes.FGR_CONFIRM,
            // A36 D-12 — the spec's TECHNICIAN: works the service order (plan, start, materials) and completes it.
            PermissionCodes.SERVICE_ORDER_VIEW, PermissionCodes.SERVICE_ORDER_EDIT, PermissionCodes.SERVICE_ORDER_COMPLETE,
        ],

        [(int)EnumRole.FinanceOfficer] =
        [
            PermissionCodes.INVOICE_VIEW,   PermissionCodes.INVOICE_PROCESS,
            PermissionCodes.PAYMENT_VIEW,   PermissionCodes.PAYMENT_PROCESS,
            PermissionCodes.SALES_INVOICE_VIEW,    PermissionCodes.SALES_INVOICE_MANAGE,
            PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_PAYMENT_RECORD,
            PermissionCodes.CUSTOMER_LEDGER_VIEW,  PermissionCodes.PRODUCT_LEDGER_VIEW,
            PermissionCodes.RECONCILIATION,
            PermissionCodes.BUDGET_VIEW,    PermissionCodes.BUDGET_MONITOR,
            PermissionCodes.REPORT_VIEW,    PermissionCodes.REPORT_EXPORT,
            PermissionCodes.GRN_FINANCE_APPROVE,
            // Sees what reached QuickBooks and can push/retry; connecting and mappings stay with management.
            PermissionCodes.INTEGRATION_VIEW, PermissionCodes.INTEGRATION_SYNC,
            PermissionCodes.SERVICE_ORDER_VIEW, // A36 D-12
        ],

        [(int)EnumRole.Requester] =
        [
            PermissionCodes.REQUISITION_CREATE,
            PermissionCodes.REQUISITION_VIEW_OWN,
        ],

        [(int)EnumRole.Auditor] =
        [
            PermissionCodes.SUPPLIER_VIEW,
            PermissionCodes.RFQ_VIEW,
            PermissionCodes.CONTRACT_VIEW,
            PermissionCodes.PO_VIEW,
            PermissionCodes.BUDGET_VIEW,
            PermissionCodes.REQUISITION_VIEW_ALL,
            PermissionCodes.INVENTORY_VIEW,
            PermissionCodes.INVOICE_VIEW,   PermissionCodes.PAYMENT_VIEW,
            PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW,
            PermissionCodes.PRODUCT_LEDGER_VIEW,
            PermissionCodes.ALLOCATION_VIEW, PermissionCodes.BOM_VIEW, PermissionCodes.PROD_VIEW, PermissionCodes.SUPPLY_VIEW,
            PermissionCodes.PROD_LEDGER_VIEW,
            PermissionCodes.AUDIT_LOG_VIEW,
            PermissionCodes.REPORT_VIEW,    PermissionCodes.REPORT_EXPORT,
            PermissionCodes.WORKFLOW_VIEW,
            PermissionCodes.INTEGRATION_VIEW,
            PermissionCodes.SERVICE_ORDER_VIEW, // A36 D-12
        ],

        [(int)EnumRole.FinanceManager] =
        [
            PermissionCodes.INVOICE_VIEW,   PermissionCodes.INVOICE_PROCESS,
            PermissionCodes.PAYMENT_VIEW,   PermissionCodes.PAYMENT_PROCESS, PermissionCodes.PAYMENT_APPROVE,
            PermissionCodes.SALES_INVOICE_VIEW,    PermissionCodes.SALES_INVOICE_MANAGE,
            PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_PAYMENT_RECORD,
            PermissionCodes.CUSTOMER_LEDGER_VIEW,  PermissionCodes.PRODUCT_LEDGER_VIEW,
            PermissionCodes.RECONCILIATION,
            PermissionCodes.BUDGET_VIEW,    PermissionCodes.BUDGET_MANAGE,
            PermissionCodes.REPORT_VIEW,    PermissionCodes.REPORT_EXPORT,
            PermissionCodes.GRN_FINANCE_APPROVE,
            // Owns the books' side of the QuickBooks connection: mappings, going live, API keys.
            PermissionCodes.INTEGRATION_VIEW, PermissionCodes.INTEGRATION_MANAGE, PermissionCodes.INTEGRATION_SYNC,
            // Owns the tax codes and exchange rates every document is priced and converted with.
            PermissionCodes.FINANCE_SETUP_MANAGE,
            // A35 D-16 — the role holding FINANCE_SETUP_MANAGE also enters rates and runs the revaluation.
            PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.EXCHANGE_REVALUATION_RUN,
            PermissionCodes.SERVICE_ORDER_VIEW, // A36 D-12
        ],

        // Org Admin is the full owner/operator of their own tenant: every business permission in
        // the catalog (requisitions, RFQs, POs, inventory, warehouse, finance, material management,
        // workflow configuration, reports, user/role management, location catalog, PO branding —
        // all of it), so there's no blockage managing their org's users or any task in the app.
        // This is safe to grant broadly because every one of these permissions gates data that's
        // already properly tenant-scoped (WorkflowDefinition/WorkflowStep/WorkflowGroup included —
        // an Org Admin configuring "their" workflow can only ever see/edit their own org's rows).
        // Excludes SYSTEM_CONFIGURE/PLATFORM_SUPER_ADMIN — the two codes that reach genuinely
        // cross-tenant surfaces (api/system/* platform administration, and shared global reference
        // data like Lookup Types/Currencies/editing existing Countries-and-Cities that every OTHER
        // organization also relies on). Granting either would let one org's admin affect every
        // other org. Uses .Except(...) rather than an explicit list so any future permission code
        // added to the catalog automatically flows to every existing Org Admin too — no seeder edit
        // required, satisfying "all necessary permissions granted on creation" for orgs that
        // already exist (this seeder is additive/idempotent — it re-runs and fills the gap on the
        // API's next startup, for every org's Org Admin at once, since they all share this one role).
        // Also excludes SALE_ORDER_CONFIG_WRITE, for the same §3.1 reason SystemAdmin does above —
        // Org Admin is each tenant's own "Super Admin", and the FSD names that role explicitly.
        [(int)EnumRole.OrgAdmin] = PermissionCodes.All
            .Except([
                PermissionCodes.SYSTEM_CONFIGURE, PermissionCodes.PLATFORM_SUPER_ADMIN,
                PermissionCodes.SALE_ORDER_CONFIG_WRITE
            ])
            .ToArray(),

        // A29-P3-01 §3.1 — the only role that can change sale order configuration.
        [(int)EnumRole.SupplyDeptAdmin] =
        [
            PermissionCodes.SALE_ORDER_CONFIG_READ,
            PermissionCodes.SALE_ORDER_CONFIG_WRITE,
            // A34 D-24 — the lead-time defaults drive every quoted delivery date, next to the sale order settings.
            PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE,
        ],

        // A38 D-13 — point of sale built-in roles (API-CONTRACT §9).
        [PosCashierSeedId]    = PosCashierCodes,
        [PosSupervisorSeedId] = PosSupervisorCodes,
        [StoreManagerSeedId]  = StoreManagerCodes,

        // A41 D-4 — CRM built-in roles.
        [SalesRepSeedId]      = SalesRepCodes,
        [SalesManagerSeedId]  = SalesManagerCodes,
    };

    private async Task SeedRolePermissionsAsync()
    {
        // Build a lookup: permissionCode → permissionId
        var permLookup = await _db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.PermissionID);

        // RolePermissionSeed's keys are RoleSeed's own literal ids — a fixed label for "which
        // built-in role", not necessarily that role's real RoleID in this database (see
        // SeedRolesAsync's remarks). Resolved here via RoleCode so a permission always reaches the
        // role that was actually seeded under that code, never whatever else happens to hold the id.
        var codeBySeedId = RoleSeed.ToDictionary(r => r.Id, r => r.Code);
        var roleIdByCode = await _db.Roles.IgnoreQueryFilters()
            .Where(r => codeBySeedId.Values.Contains(r.RoleCode))
            .ToDictionaryAsync(r => r.RoleCode, r => r.RoleID);

        foreach (var (seedId, codes) in RolePermissionSeed)
        {
            if (!codeBySeedId.TryGetValue(seedId, out var roleCode) || !roleIdByCode.TryGetValue(roleCode, out var roleId))
                continue; // the role this seed entry names was never created — nothing to grant it

            foreach (var code in codes)
            {
                if (!permLookup.TryGetValue(code, out var permId)) continue;

                if (!await _db.RolePermissions.AnyAsync(rp =>
                        rp.RoleID == roleId && rp.PermissionID == permId))
                {
                    _db.RolePermissions.Add(new RolePermission
                    {
                        RoleID = roleId,
                        PermissionID = permId,
                        IsAllowed = true,
                        OrganizationId = TenantDefaults.ScmDemoOrganizationId
                    });
                }
            }
        }
        await _db.SaveChangesAsync();
    }

    // ── 4. Seed admin user ────────────────────────────────────────────────────

    private async Task SeedAdminUserAsync()
    {
        const string adminEmail = "admin@sms.local";
        if (await _db.UserAccounts.AnyAsync(u => u.Email == adminEmail))
            return;

        var admin = new UserAccount
        {
            FirstName   = "System",
            LastName    = "Admin",
            Email       = adminEmail,
            RoleID      = (int)EnumRole.SystemAdmin,
            IsActive    = true,
            IsDelete    = false,
            CreatedBy   = 0,
            CreatedDate = DateTime.UtcNow
        };
        admin.Password = _hasher.HashPassword(admin, "Admin@12345");

        _db.UserAccounts.Add(admin);
        await _db.SaveChangesAsync();
    }
}
