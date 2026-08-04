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

        ("View Reports",                  PermissionCodes.REPORT_VIEW,           "Access standard reports and dashboards"),
        ("Export Reports",                PermissionCodes.REPORT_EXPORT,         "Download report data to CSV / Excel"),

        ("Manage Workflow Definitions",   PermissionCodes.WORKFLOW_ADMIN,        "Configure workflow definitions and approval steps"),
        ("View Workflow Status",          PermissionCodes.WORKFLOW_VIEW,         "View workflow approval status and history"),
        ("QC Confirm GRN",                PermissionCodes.GRN_QC_CONFIRM,        "Quality-control sign-off on goods receipt notes"),
        ("Approve GRN",                   PermissionCodes.GRN_APPROVE,           "Final inventory manager approval of GRNs"),
        ("Finance Approve GRN",           PermissionCodes.GRN_FINANCE_APPROVE,   "Finance sign-off on GRN value"),

        ("View Material Management",      PermissionCodes.MATERIAL_VIEW,         "Read projects, material issue requests/vouchers, wastage, returns, and cost ledgers"),
        ("Manage Material Management",    PermissionCodes.MATERIAL_MANAGE,       "Create/update projects, issue and post MIRs/MIVs, approve wastage, and process returns"),
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
    ];

    private async Task SeedRolesAsync()
    {
        foreach (var (id, name, code, desc) in RoleSeed)
        {
            var role = await _db.Roles.FindAsync(id);
            if (role == null)
            {
                // The built-in catalog is global (IsGlobal=true, OrganizationId=null) — usable and
                // assignable by every organization. Org-owned custom roles (IsGlobal=false) are
                // only ever created at runtime via RolesController, never seeded here.
                _db.Roles.Add(new Role
                {
                    RoleID = id, Name = name, RoleCode = code, Description = desc, IsActive = true,
                    IsGlobal = true, OrganizationId = null
                });
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
        [(int)EnumRole.SystemAdmin] = PermissionCodes.All.ToArray(),

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
            PermissionCodes.DELIVERY_TRACK,
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
            PermissionCodes.DELIVERY_TRACK,
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
        ],

        [(int)EnumRole.FinanceOfficer] =
        [
            PermissionCodes.INVOICE_VIEW,   PermissionCodes.INVOICE_PROCESS,
            PermissionCodes.PAYMENT_VIEW,   PermissionCodes.PAYMENT_PROCESS,
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
            PermissionCodes.AUDIT_LOG_VIEW,
            PermissionCodes.REPORT_VIEW,    PermissionCodes.REPORT_EXPORT,
            PermissionCodes.WORKFLOW_VIEW,
        ],

        [(int)EnumRole.FinanceManager] =
        [
            PermissionCodes.INVOICE_VIEW,   PermissionCodes.INVOICE_PROCESS,
            PermissionCodes.PAYMENT_VIEW,   PermissionCodes.PAYMENT_PROCESS, PermissionCodes.PAYMENT_APPROVE,
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
        // Excludes ONLY SYSTEM_CONFIGURE/PLATFORM_SUPER_ADMIN — the two codes that reach genuinely
        // cross-tenant surfaces (api/system/* platform administration, and shared global reference
        // data like Lookup Types/Currencies/editing existing Countries-and-Cities that every OTHER
        // organization also relies on). Granting either would let one org's admin affect every
        // other org. Uses .Except(...) rather than an explicit list so any future permission code
        // added to the catalog automatically flows to every existing Org Admin too — no seeder edit
        // required, satisfying "all necessary permissions granted on creation" for orgs that
        // already exist (this seeder is additive/idempotent — it re-runs and fills the gap on the
        // API's next startup, for every org's Org Admin at once, since they all share this one role).
        [(int)EnumRole.OrgAdmin] = PermissionCodes.All
            .Except([PermissionCodes.SYSTEM_CONFIGURE, PermissionCodes.PLATFORM_SUPER_ADMIN])
            .ToArray(),
    };

    private async Task SeedRolePermissionsAsync()
    {
        // Build a lookup: permissionCode → permissionId
        var permLookup = await _db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.PermissionID);

        foreach (var (roleId, codes) in RolePermissionSeed)
        {
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
