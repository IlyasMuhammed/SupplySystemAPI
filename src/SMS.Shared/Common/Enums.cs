namespace SMS.Shared.Common;

public enum EnumRole
{
    SystemAdmin        = 1,
    ProcurementManager = 2,
    PurchaseOfficer    = 3,
    InventoryManager   = 4,
    WarehouseOperator  = 5,
    FinanceOfficer     = 6,
    Requester          = 7,
    Auditor            = 8,
    FinanceManager     = 9,
    // Per-organization admin, seeded as a tenant's initial user (MT-002). Full owner/operator of
    // their own tenant — holds every permission in the catalog except SYSTEM_CONFIGURE/
    // PLATFORM_SUPER_ADMIN (see AuthDataSeeder's RolePermissionSeed), the two codes that reach
    // genuinely cross-tenant surfaces. USER_MANAGE is still global in Auth today (no per-org data
    // scoping yet) — a known, documented gap.
    OrgAdmin           = 10,
    // A29-P3-01 — owns sale order administration & configuration (FSD §3.1): the Deputy Director /
    // Director of Supply, not IT/Super Admin. SystemAdmin and OrgAdmin keep SALE_ORDER_CONFIG_READ
    // through their blanket grants but are deliberately denied SALE_ORDER_CONFIG_WRITE, which only
    // this role carries (see AuthDataSeeder.RolePermissionSeed).
    SupplyDeptAdmin    = 11,
}

public enum EnumStatus
{
    Active = 1,
    Inactive = 2,
    Deleted = 3,
    Pending = 4
}

public enum EnumNotificationType
{
    Email = 1,
    Sms = 2,
    Push = 3
}
