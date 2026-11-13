using SMS.Shared.Common;
using S = SMS.Shared.Onboarding.OnboardingStepCodes;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>One step of the onboarding checklist (FSD §3.3, D-28). Links are full Angular routes.</summary>
internal sealed record ChecklistStepDefinition(
    string StepCode, string Title, string Description, string Link, string? RequiresModule, int SortOrder);

/// <summary>
/// D-72: every organization's checklist holds all of these steps; which ones are shown is decided on read from the modules
/// enabled now, so a module switched on later brings its step back (with any tick it already earned).
/// </summary>
internal static class ChecklistCatalog
{
    public static readonly IReadOnlyList<ChecklistStepDefinition> Steps =
    [
        new(S.CompleteSetup, "Complete the setup wizard",       "Country, chart of accounts, taxes and defaults.",      "/portal/pages/onboarding/setup",               null,                    10),
        new(S.AddCustomer,   "Add your first customer",         "Customers you sell to.",                               "/portal/pages/customers",                      ModuleCodes.Customers,   20),
        new(S.AddProduct,    "Add your first product",          "The items you buy, stock and sell.",                   "/portal/pages/inventory/products/new",         ModuleCodes.Inventory,   30),
        new(S.AddSupplier,   "Add your first supplier",         "The vendors you buy from.",                            "/portal/pages/suppliers/supplier-create",      ModuleCodes.Suppliers,   40),
        new(S.CreateSo,      "Create your first sale order",    "Take an order from a customer.",                       "/portal/pages/sales/orders",                   ModuleCodes.Demand,      50),
        new(S.CreatePo,      "Create your first purchase order","Order stock from a supplier.",                         "/portal/pages/demand/purchase-orders/create",  ModuleCodes.Demand,      60),
        new(S.ConnectQb,     "Connect QuickBooks (optional)",   "Send invoices, bills and payments to QuickBooks Online.","/portal/pages/integrations/quickbooks",      ModuleCodes.Integration, 70),
        new(S.InviteTeam,    "Invite your team members",        "Add the people who will work in the system.",          "/portal/pages/users",                          null,                    80),
        new(S.ImportData,    "Import your existing data",       "Bring customers, products and opening balances from a spreadsheet.", "/portal/pages/settings/import", ModuleCodes.DataImport, 90),
        new(S.HrmSetup,       "Set up HR",                       "Choose the payroll cost center so payroll can post.",    "/portal/pages/hrm/setup",                      ModuleCodes.Hrm,         100),
        new(S.HrmAddEmployee, "Add your first employee",         "The people you employ and pay.",                         "/portal/pages/hrm/employees/new",              ModuleCodes.Hrm,         110),
        new(S.HrmVerifyTax,   "Have your tax slabs checked",     "Seeded tax slabs are unverified; a payroll reviewer must confirm them before payroll can be finalized.", "/portal/pages/hrm/salary/tax-tables", ModuleCodes.Hrm, 120),
    ];

    public static ChecklistStepDefinition? Find(string? code) =>
        Steps.FirstOrDefault(s => string.Equals(s.StepCode, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}
