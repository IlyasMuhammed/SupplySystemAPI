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
        await SeedAdminUserAsync();
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

        ("View Sales Invoices",     PermissionCodes.SALES_INVOICE_VIEW,     "Read and print sales invoices"),
        ("Manage Sales Invoices",   PermissionCodes.SALES_INVOICE_MANAGE,   "Raise a sales invoice from a delivery, amend or delete a draft, and issue it — issuing books the receivable"),
        ("View Customer Payments",  PermissionCodes.CUSTOMER_PAYMENT_VIEW,  "Read money received from customers and how it was applied"),
        ("Record Customer Payments", PermissionCodes.CUSTOMER_PAYMENT_RECORD, "Record a customer's payment and apply it to their invoices — this is what settles them"),
        ("View Customer Ledger",    PermissionCodes.CUSTOMER_LEDGER_VIEW,   "Read a customer's receivables ledger"),

        ("View Product Ledger",     PermissionCodes.PRODUCT_LEDGER_VIEW,    "Read what a product variant cost, its stock value and weighted-average cost, and the product profitability report"),
    ];

    private async Task SeedPermissionsAsync()
    {
        foreach (var (name, code, desc) in PermissionSeed)
        {
            if (!await _db.Permissions.AnyAsync(p => p.Code == code))
                _db.Permissions.Add(new Permission { Name = name, Code = code, Description = desc });
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
                var newId = existing.Any(r => r.RoleID == id) ? existing.Max(r => r.RoleID) + 1 : id;
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
            PermissionCodes.REPORT_VIEW,      PermissionCodes.REPORT_EXPORT,
            PermissionCodes.WORKFLOW_VIEW,
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
        ],

        [(int)EnumRole.InventoryManager] =
        [
            PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE,
            PermissionCodes.STOCK_ADJUST,   PermissionCodes.REORDER_MANAGE,
            PermissionCodes.WAREHOUSE_TRANSFER,
            PermissionCodes.GOODS_RECEIVE,  PermissionCodes.PUTAWAY,
            PermissionCodes.GRN_APPROVE,
            PermissionCodes.REPORT_VIEW,
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
            PermissionCodes.AUDIT_LOG_VIEW,
            PermissionCodes.REPORT_VIEW,    PermissionCodes.REPORT_EXPORT,
            PermissionCodes.WORKFLOW_VIEW,
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
        ],
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
