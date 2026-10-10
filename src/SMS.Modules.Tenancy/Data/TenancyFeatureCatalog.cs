using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Data;

// Single source of truth for the feature catalog and the BASIC/STANDARD/ENTERPRISE default
// matrix. Grounded in the real module/screen list (SMS.API/Program.cs's AddXModule calls and
// SMS.Shared/Authorization/PermissionCodes.cs groupings) — not invented. Consumed only by
// TenancyDataSeeder; everything downstream (org creation, backfill) reads the persisted
// PlanFeatureTemplates table this seeds, rather than recomputing this logic.
//
// A37 (docs/module-registry, D-1/D-2) — this IS the module registry: MODULE_* rows are modules, SCREEN_*/FEATURE_* rows
// with a Parent are the module's sub-features. IsAlwaysOn = an org admin cannot switch it off (D-5); IsAvailable=false
// = "Coming soon" (cannot be licensed or enabled); IsHidden = kept for old data, never listed to org admins.
internal static class TenancyFeatureCatalog
{
    internal sealed record Entry(
        string Code, string Name, string Category, string? Description, bool IsCore, int DisplayOrder,
        string? Parent = null, bool IsAlwaysOn = false, bool IsAvailable = true, string? Icon = null, bool IsHidden = false);

    internal const string CategoryModule  = "MODULE";
    internal const string CategoryScreen  = "SCREEN";
    internal const string CategoryFeature = "FEATURE";

    // Feature dependency graph — (Dependent, RequiredBy): Dependent may only be on while RequiredBy is on. The super-admin
    // licence path auto-enables RequiredBy; the org-admin path refuses ("Enable X first." / "Disable Y first."). Seeded
    // into tenant.feature_dependencies on every start (queryable), checked for cycles first (fails fast). A37 D-3.
    internal static readonly IReadOnlyList<(string Dependent, string RequiredBy)> Dependencies =
    [
        ("MODULE_MIR", "MODULE_INVENTORY"),
        // A30 §37.3 / OQ-9 — "Manufacturing Mode" is a per-tenant toggle; it makes and consumes stock.
        ("MODULE_MANUFACTURING", "MODULE_INVENTORY"),
        // A36 D-11 — service orders reserve, issue and return stock.
        ("MODULE_SERVICES", "MODULE_INVENTORY"),
        // A37 D-3
        ("MODULE_DEMAND", "MODULE_INVENTORY"),
        ("MODULE_DEMAND", "MODULE_SUPPLIERS"),
        ("MODULE_WAREHOUSE", "MODULE_INVENTORY"),
        ("MODULE_LOGISTICS", "MODULE_WAREHOUSE"),
        ("MODULE_INTEGRATION", "MODULE_FINANCE"),
    ];

    /// <summary>A37 MOD-08 — switched on by the system while any of these modules is on.</summary>
    internal static readonly IReadOnlyList<string> BomManagementDrivers = [ModuleCodes.Manufacturing, ModuleCodes.Services];

    internal static readonly IReadOnlyList<Entry> Catalog = new List<Entry>
    {
        // MODULE_* — one per real (or explicitly-named-in-ticket stub) module boundary.
        new("MODULE_MASTER_DATA",     "Master Data",              CategoryModule, "Countries, currencies, payment terms, and other shared lookup data.", true,  10, IsAlwaysOn: true, Icon: "pi pi-database"),
        new("MODULE_SUPPLIERS",       "Supplier Management",      CategoryModule, "Supplier onboarding, scorecards, and lifecycle management.",          false, 20, Icon: "pi pi-users"),
        new("MODULE_CUSTOMERS",       "Customers",                CategoryModule, "Customer master: walk-in, individual, company and employee customers, credit limits.", false, 25, IsAlwaysOn: true, Icon: "pi pi-id-card"),
        new("MODULE_DEMAND",          "Demand & Procurement",     CategoryModule, "Requisitions, RFQs, quotations, and purchase orders.",                 false, 30, Icon: "pi pi-shopping-cart"),
        new("MODULE_PROCUREMENT",     "Procurement",              CategoryModule, "Procurement module (placeholder — functionality currently lives under Demand).", false, 40, IsAvailable: false, IsHidden: true),
        new("MODULE_INVENTORY",       "Inventory",                CategoryModule, "Stock levels, reorder points, and inventory ledgers.",                 false, 50, IsAlwaysOn: true, Icon: "pi pi-box"),
        new("MODULE_WAREHOUSE",       "Warehouse",                CategoryModule, "Goods receipt (GRN) and supplier return orders (SRO).",                false, 60, Icon: "pi pi-building"),
        new("MODULE_LOGISTICS",       "Logistics",                CategoryModule, "Delivery orders, packing, consignments and carrier tracking.",          false, 70, Icon: "pi pi-truck"),
        // D-5 — always on only where licensed: a Basic organization keeps Finance off.
        new("MODULE_FINANCE",         "Finance",                  CategoryModule, "Invoices, payments, and credit/debit notes.",                           false, 80, IsAlwaysOn: true, Icon: "pi pi-wallet"),
        new("MODULE_REPORTS",         "Reports",                  CategoryModule, "Cross-module reporting and analytics.",                                 false, 90, Icon: "pi pi-chart-bar"),
        new("MODULE_WORKFLOW_ENGINE", "Workflow Engine",          CategoryModule, "Approval workflow definitions powering PR/PO/GRN.",                     true,  100, IsAlwaysOn: true, Icon: "pi pi-sitemap"),
        new("MODULE_MIR",             "Material Issue & Projects", CategoryModule, "Projects, material issue requests, MIV, wastage, and returns.",       false, 110, Icon: "pi pi-briefcase"),
        new("MODULE_MANUFACTURING",   "Manufacturing",            CategoryModule, "Bills of materials, production orders, material issue to the floor, quality inspection and finished goods receipt.", false, 115, Icon: "pi pi-cog"),
        new("MODULE_SERVICES",        "Service Orders",           CategoryModule, "Service orders: scheduling, service BOMs, material reservation and issue, completion and the service ledger.", false, 117, Icon: "pi pi-wrench"),
        new("MODULE_NOTIFICATIONS",   "Notifications",            CategoryModule, "In-app and email/WhatsApp notification delivery.",                     false, 120, Icon: "pi pi-bell"),
        new("MODULE_INTEGRATION",     "QuickBooks Integration",   CategoryModule, "Sync customers, vendors, items, sales invoices and bills to QuickBooks Online.", false, 125, Icon: "pi pi-sync"),
        // A37 D-2 — spec modules that are not built: listed as "Coming soon".
        new("MODULE_DEMAND_PLANNING", "Demand Planning",          CategoryModule, "Demand forecasting, reorder point planning and MRP.",                   false, 130, IsAvailable: false, Icon: "pi pi-chart-line"),
        new("MODULE_POS",             "Point of Sale",            CategoryModule, "Counter sales, cash registers and receipts.",                           false, 135, IsAvailable: false, Icon: "pi pi-shopping-bag"),
        new("MODULE_PROJECTS",        "Projects",                 CategoryModule, "Project planning, budgets and time tracking.",                          false, 140, IsAvailable: false, Icon: "pi pi-folder"),
        new("MODULE_CRM",             "CRM",                      CategoryModule, "Leads, opportunities and customer interactions.",                       false, 145, IsAvailable: false, Icon: "pi pi-comments"),
        new("MODULE_MAINTENANCE",     "Maintenance",              CategoryModule, "Asset register and preventive maintenance.",                            false, 150, IsAvailable: false, Icon: "pi pi-hammer"),
        new("MODULE_ECOMMERCE",       "E-commerce",               CategoryModule, "Online storefront and web orders.",                                     false, 155, IsAvailable: false, Icon: "pi pi-globe"),

        // SCREEN_* — notable individually-permissioned screens.
        new("SCREEN_USER_MANAGEMENT",       "User Management",          CategoryScreen, "Create and manage user accounts.",                          true,  210, Parent: "MODULE_MASTER_DATA"),
        new("SCREEN_ROLE_MANAGEMENT",       "Role Management",          CategoryScreen, "Define roles and assign permissions.",                      true,  220, Parent: "MODULE_MASTER_DATA"),
        // A37 §1.3 — the spec's SUPPLIER_EVALUATION.
        new("SCREEN_SUPPLIER_SCORECARD",    "Supplier Scorecard",       CategoryScreen, "Supplier performance scoring dashboard.",                   false, 230, Parent: "MODULE_SUPPLIERS"),
        new("SCREEN_AUDIT_LOG",             "Audit Log",                CategoryScreen, "System-wide audit trail viewer.",                           false, 240, Parent: "MODULE_MASTER_DATA"),
        new("SCREEN_PO_DOCUMENT_TEMPLATE",  "PO Document Template",     CategoryScreen, "Purchase order letterhead/branding editor.",                false, 250, Parent: "MODULE_DEMAND"),
        new("SCREEN_WORKFLOW_CONFIG",       "Workflow Configuration",   CategoryScreen, "Approval workflow definition editor.",                      false, 260, Parent: "MODULE_WORKFLOW_ENGINE"),
        // A37 §1.3 — the spec's BUDGETING.
        new("SCREEN_BUDGET_MONITOR",        "Budget Monitor",          CategoryScreen, "Budget utilization monitoring dashboard.",                  false, 270, Parent: "MODULE_FINANCE"),

        // FEATURE_* — cross-cutting capabilities.
        new("FEATURE_EMAIL_NOTIFICATIONS", "Email Notifications", CategoryFeature, "Outbound email notifications via SendGrid.",           false, 310, Parent: "MODULE_NOTIFICATIONS"),
        new("FEATURE_WHATSAPP",            "WhatsApp Notifications", CategoryFeature, "Outbound WhatsApp notifications.",                   false, 320, Parent: "MODULE_NOTIFICATIONS"),
        new("FEATURE_MASTER_LEDGERS",      "Master Ledgers",       CategoryFeature, "Cross-module inventory and cost ledger views.",         false, 330, Parent: "MODULE_INVENTORY"),

        // A37 §1.3 — module sub-features. Enforced ones gate real endpoints; the rest are catalog entries (R-2), and those
        // with no screen at all are "Coming soon" (D-19).
        new("FEATURE_BOM_MANAGEMENT",      "BOM Management",       CategoryFeature, "Create, edit, approve and activate bills of materials (reading BOMs needs only Inventory). Switched on automatically with Manufacturing or Service Orders.", false, 400, Parent: "MODULE_INVENTORY"),
        new("FEATURE_STOCK_TRANSFERS",     "Stock Transfers",      CategoryFeature, "Moving stock between warehouses.",                      false, 401, Parent: "MODULE_INVENTORY"),
        new("FEATURE_STOCK_COUNTS",        "Stock Counts",         CategoryFeature, "Physical stock counts and adjustments.",                false, 402, Parent: "MODULE_INVENTORY"),
        new("FEATURE_PRODUCT_VARIANTS",    "Product Variants",     CategoryFeature, "Every product is sold and stocked through its variants.", true, 403, Parent: "MODULE_INVENTORY"),
        new("FEATURE_RFQ_MANAGEMENT",      "RFQ Management",       CategoryFeature, "Requests for quotation to suppliers and their responses.", false, 410, Parent: "MODULE_DEMAND"),
        new("FEATURE_BLANKET_ORDERS",      "Blanket Orders",       CategoryFeature, "Long-running purchase agreements with call-offs.",      false, 411, Parent: "MODULE_DEMAND", IsAvailable: false),
        new("FEATURE_PURCHASE_RETURNS",    "Purchase Returns",     CategoryFeature, "Supplier return orders (SRO).",                         false, 420, Parent: "MODULE_WAREHOUSE"),
        new("FEATURE_PICK_LISTS",          "Pick Lists",           CategoryFeature, "Picking goods for deliveries.",                         false, 430, Parent: "MODULE_LOGISTICS"),
        new("FEATURE_SHIPMENT_TRACKING",   "Shipment Tracking",    CategoryFeature, "Consignments, tracking links and carrier tracking.",    false, 431, Parent: "MODULE_LOGISTICS"),
        new("FEATURE_CARRIER_INTEGRATION", "Carrier Integration",  CategoryFeature, "Direct booking with carrier systems.",                  false, 432, Parent: "MODULE_LOGISTICS", IsAvailable: false),
        new("FEATURE_QUALITY_INSPECTION",  "Quality Inspection",   CategoryFeature, "Inspecting production output before it is received.",   false, 440, Parent: "MODULE_MANUFACTURING"),
        new("FEATURE_MRP_SCHEDULING",      "MRP Scheduling",       CategoryFeature, "Material requirements planning runs.",                  false, 441, Parent: "MODULE_MANUFACTURING", IsAvailable: false),
        new("FEATURE_PRODUCTION_COSTING",  "Production Costing",   CategoryFeature, "Standard vs. actual production cost analysis.",         false, 442, Parent: "MODULE_MANUFACTURING", IsAvailable: false),
        new("FEATURE_WORK_CENTERS",        "Work Centers",         CategoryFeature, "Machines, capacity and routings.",                      false, 443, Parent: "MODULE_MANUFACTURING", IsAvailable: false),
        new("FEATURE_FIELD_SERVICE",       "Field Service",        CategoryFeature, "Dispatching technicians to customer sites.",            false, 450, Parent: "MODULE_SERVICES", IsAvailable: false),
        new("FEATURE_SERVICE_CONTRACTS",   "Service Contracts",    CategoryFeature, "Recurring service agreements.",                         false, 451, Parent: "MODULE_SERVICES", IsAvailable: false),
        new("FEATURE_MULTI_CURRENCY",      "Multi-currency",       CategoryFeature, "Documents in foreign currencies with exchange rates.",  false, 460, Parent: "MODULE_FINANCE"),
        new("FEATURE_BANK_RECONCILIATION", "Bank Reconciliation",  CategoryFeature, "Matching bank statements to payments.",                 false, 461, Parent: "MODULE_FINANCE", IsAvailable: false),
        new("FEATURE_CREDIT_MANAGEMENT",   "Credit Management",    CategoryFeature, "Customer credit limits.",                               false, 470, Parent: "MODULE_CUSTOMERS"),
        new("FEATURE_LOYALTY_PROGRAM",     "Loyalty Program",      CategoryFeature, "Customer points and rewards.",                          false, 471, Parent: "MODULE_CUSTOMERS", IsAvailable: false),
    };

    internal static readonly IReadOnlyDictionary<string, Entry> ByCode =
        Catalog.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

    internal const string PlanBasic      = "BASIC";
    internal const string PlanStandard   = "STANDARD";
    internal const string PlanEnterprise = "ENTERPRISE";

    internal static readonly IReadOnlyList<string> AllPlans = new[] { PlanBasic, PlanStandard, PlanEnterprise };

    // Enterprise is always literally "every current catalog entry" — computed, not listed — so
    // the matrix self-corrects the moment a new FeatureDefinition is added to the catalog above.
    private static readonly HashSet<string> BasicExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODULE_FINANCE", "MODULE_MIR", "MODULE_MANUFACTURING", "MODULE_SERVICES", "MODULE_LOGISTICS", "MODULE_NOTIFICATIONS", "MODULE_PROCUREMENT",
        "MODULE_INTEGRATION",
        "SCREEN_SUPPLIER_SCORECARD", "SCREEN_AUDIT_LOG", "SCREEN_PO_DOCUMENT_TEMPLATE",
        "SCREEN_WORKFLOW_CONFIG", "SCREEN_BUDGET_MONITOR", "FEATURE_WHATSAPP", "FEATURE_MASTER_LEDGERS"
    };

    private static readonly HashSet<string> StandardExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODULE_PROCUREMENT", "SCREEN_PO_DOCUMENT_TEMPLATE", "SCREEN_WORKFLOW_CONFIG", "FEATURE_WHATSAPP"
    };

    // Returns the set of FeatureCodes that should default to enabled for the given plan. A37: never an unavailable entry;
    // a sub-feature only with its parent module (§1.3); BOM_MANAGEMENT with Manufacturing or Service Orders (MOD-08).
    internal static HashSet<string> GetDefaultEnabledCodes(string plan)
    {
        var excluded = plan.ToUpperInvariant() switch
        {
            PlanBasic    => BasicExclusions,
            PlanStandard => StandardExclusions,
            PlanEnterprise => new HashSet<string>(StringComparer.OrdinalIgnoreCase), // literally everything
            _            => BasicExclusions // unknown plan — fail safe to the most restrictive tier
        };
        var modules = Catalog.Where(e => e.Parent is null && e.IsAvailable && !excluded.Contains(e.Code))
            .Select(e => e.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var features = Catalog.Where(e => e.Parent is not null && e.IsAvailable && !excluded.Contains(e.Code)
                && modules.Contains(e.Parent)
                && (!e.Code.Equals(ModuleCodes.BomManagement, StringComparison.OrdinalIgnoreCase) || BomManagementDrivers.Any(modules.Contains)))
            .Select(e => e.Code);
        modules.UnionWith(features);
        return modules;
    }

    /// <summary>A37 D-3 / M037.3 — fails fast at startup on a dependency cycle, an unknown code, a parent that is not a
    /// module, or a core entry that is not available.</summary>
    internal static void Validate()
    {
        foreach (var e in Catalog)
        {
            if (e.IsCore && !e.IsAvailable)
                throw new InvalidOperationException($"Feature catalog: core entry {e.Code} must be available.");
            if (e.Parent is not null && (!ByCode.TryGetValue(e.Parent, out var p) || p.Category != CategoryModule || p.Parent is not null))
                throw new InvalidOperationException($"Feature catalog: {e.Code} has parent {e.Parent}, which is not a module.");
        }
        foreach (var (dependent, requiredBy) in Dependencies)
            if (!ByCode.ContainsKey(dependent) || !ByCode.ContainsKey(requiredBy))
                throw new InvalidOperationException($"Feature catalog: dependency {dependent} -> {requiredBy} names an unknown code.");
        EnsureAcyclic(Dependencies);
    }

    internal static void EnsureAcyclic(IEnumerable<(string Dependent, string RequiredBy)> pairs)
    {
        var edges = pairs.GroupBy(p => p.Dependent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.RequiredBy).ToList(), StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = visiting, 2 = done

        void Visit(string node, List<string> path)
        {
            if (state.TryGetValue(node, out var s))
            {
                if (s == 1) throw new InvalidOperationException(
                    $"Feature dependency cycle: {string.Join(" -> ", path.SkipWhile(c => !c.Equals(node, StringComparison.OrdinalIgnoreCase)).Append(node))}.");
                return;
            }
            state[node] = 1;
            path.Add(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? []) Visit(next, path);
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        foreach (var node in edges.Keys.ToList()) Visit(node, []);
    }
}
