namespace SMS.Modules.Tenancy.Data;

// Single source of truth for the feature catalog and the BASIC/STANDARD/ENTERPRISE default
// matrix. Grounded in the real module/screen list (SMS.API/Program.cs's AddXModule calls and
// SMS.Shared/Authorization/PermissionCodes.cs groupings) — not invented. Consumed only by
// TenancyDataSeeder; everything downstream (org creation, backfill) reads the persisted
// PlanFeatureTemplates table this seeds, rather than recomputing this logic.
internal static class TenancyFeatureCatalog
{
    internal sealed record Entry(string Code, string Name, string Category, string? Description, bool IsCore, int DisplayOrder);

    internal const string CategoryModule  = "MODULE";
    internal const string CategoryScreen  = "SCREEN";
    internal const string CategoryFeature = "FEATURE";

    // Feature dependency graph — enabling Dependent auto-enables RequiredBy if not already on;
    // disabling RequiredBy is rejected while Dependent is (or would remain) enabled. A static list,
    // not a DB table — matches how the rest of this catalog is code-defined; still just the one
    // pair the ticket specifies, written as a list so a second pair later needs no algorithm change.
    internal static readonly IReadOnlyList<(string Dependent, string RequiredBy)> Dependencies =
    [
        ("MODULE_MIR", "MODULE_INVENTORY")
    ];

    internal static readonly IReadOnlyList<Entry> Catalog = new List<Entry>
    {
        // MODULE_* — one per real (or explicitly-named-in-ticket stub) module boundary.
        new("MODULE_MASTER_DATA",     "Master Data",              CategoryModule, "Countries, currencies, payment terms, and other shared lookup data.", true,  10),
        new("MODULE_SUPPLIERS",       "Supplier Management",      CategoryModule, "Supplier onboarding, scorecards, and lifecycle management.",          false, 20),
        new("MODULE_DEMAND",          "Demand & Procurement",     CategoryModule, "Requisitions, RFQs, quotations, and purchase orders.",                 false, 30),
        new("MODULE_PROCUREMENT",     "Procurement",              CategoryModule, "Procurement module (placeholder — functionality currently lives under Demand).", false, 40),
        new("MODULE_INVENTORY",       "Inventory",                CategoryModule, "Stock levels, reorder points, and inventory ledgers.",                 false, 50),
        new("MODULE_WAREHOUSE",       "Warehouse",                CategoryModule, "Goods receipt (GRN) and supplier return orders (SRO).",                false, 60),
        new("MODULE_LOGISTICS",       "Logistics",                CategoryModule, "Carrier and delivery tracking.",                                        false, 70),
        new("MODULE_FINANCE",         "Finance",                  CategoryModule, "Invoices, payments, and credit/debit notes.",                           false, 80),
        new("MODULE_REPORTS",         "Reports",                  CategoryModule, "Cross-module reporting and analytics.",                                 false, 90),
        new("MODULE_WORKFLOW_ENGINE", "Workflow Engine",          CategoryModule, "Approval workflow definitions powering PR/PO/GRN.",                     true,  100),
        new("MODULE_MIR",             "Material Issue & Projects", CategoryModule, "Projects, material issue requests, MIV, wastage, and returns.",       false, 110),
        new("MODULE_NOTIFICATIONS",   "Notifications",            CategoryModule, "In-app and email/WhatsApp notification delivery.",                     false, 120),

        // SCREEN_* — notable individually-permissioned screens.
        new("SCREEN_USER_MANAGEMENT",       "User Management",          CategoryScreen, "Create and manage user accounts.",                          true,  210),
        new("SCREEN_ROLE_MANAGEMENT",       "Role Management",          CategoryScreen, "Define roles and assign permissions.",                      true,  220),
        new("SCREEN_SUPPLIER_SCORECARD",    "Supplier Scorecard",       CategoryScreen, "Supplier performance scoring dashboard.",                   false, 230),
        new("SCREEN_AUDIT_LOG",             "Audit Log",                CategoryScreen, "System-wide audit trail viewer.",                           false, 240),
        new("SCREEN_PO_DOCUMENT_TEMPLATE",  "PO Document Template",     CategoryScreen, "Purchase order letterhead/branding editor.",                false, 250),
        new("SCREEN_WORKFLOW_CONFIG",       "Workflow Configuration",   CategoryScreen, "Approval workflow definition editor.",                      false, 260),
        new("SCREEN_BUDGET_MONITOR",        "Budget Monitor",          CategoryScreen, "Budget utilization monitoring dashboard.",                  false, 270),

        // FEATURE_* — cross-cutting capabilities.
        new("FEATURE_EMAIL_NOTIFICATIONS", "Email Notifications", CategoryFeature, "Outbound email notifications via SendGrid.",           false, 310),
        new("FEATURE_WHATSAPP",            "WhatsApp Notifications", CategoryFeature, "Outbound WhatsApp notifications.",                   false, 320),
        new("FEATURE_MASTER_LEDGERS",      "Master Ledgers",       CategoryFeature, "Cross-module inventory and cost ledger views.",         false, 330),
    };

    internal const string PlanBasic      = "BASIC";
    internal const string PlanStandard   = "STANDARD";
    internal const string PlanEnterprise = "ENTERPRISE";

    internal static readonly IReadOnlyList<string> AllPlans = new[] { PlanBasic, PlanStandard, PlanEnterprise };

    // Enterprise is always literally "every current catalog entry" — computed, not listed — so
    // the matrix self-corrects the moment a new FeatureDefinition is added to the catalog above.
    private static readonly HashSet<string> BasicExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODULE_FINANCE", "MODULE_MIR", "MODULE_LOGISTICS", "MODULE_NOTIFICATIONS", "MODULE_PROCUREMENT",
        "SCREEN_SUPPLIER_SCORECARD", "SCREEN_AUDIT_LOG", "SCREEN_PO_DOCUMENT_TEMPLATE",
        "SCREEN_WORKFLOW_CONFIG", "SCREEN_BUDGET_MONITOR", "FEATURE_WHATSAPP", "FEATURE_MASTER_LEDGERS"
    };

    private static readonly HashSet<string> StandardExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODULE_PROCUREMENT", "SCREEN_PO_DOCUMENT_TEMPLATE", "SCREEN_WORKFLOW_CONFIG", "FEATURE_WHATSAPP"
    };

    // Returns the set of FeatureCodes that should default to enabled for the given plan.
    internal static HashSet<string> GetDefaultEnabledCodes(string plan)
    {
        var all = Catalog.Select(e => e.Code);
        var excluded = plan.ToUpperInvariant() switch
        {
            PlanBasic    => BasicExclusions,
            PlanStandard => StandardExclusions,
            PlanEnterprise => new HashSet<string>(StringComparer.OrdinalIgnoreCase), // literally everything
            _            => BasicExclusions // unknown plan — fail safe to the most restrictive tier
        };
        return all.Where(c => !excluded.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
