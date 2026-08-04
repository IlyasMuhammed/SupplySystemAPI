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
