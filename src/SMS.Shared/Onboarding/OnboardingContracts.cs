using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMS.Shared.Onboarding;

// A50 (SMS-FSD-ADD-050) — tenant onboarding seams. SMS.Modules.Onboarding orchestrates; each module that owns
// the data implements these for its own tables. See docs/onboarding/ADDENDUM-50-ANALYSIS.md (D-3, D-5, D-25..D-28).

/// <summary>DataCategory codes of SetupDefaults (FSD §3.1 / §5). Apply order = <see cref="ApplyOrder"/> (D-10).</summary>
public static class SetupCategories
{
    public const string Currency          = "CURRENCY";
    public const string Coa               = "COA";
    public const string Tax               = "TAX";
    public const string Fiscal            = "FISCAL";
    public const string PaymentTerms      = "PAYMENT_TERMS";
    public const string Uom               = "UOM";
    public const string ProductCategories = "PRODUCT_CATEGORIES";
    public const string DocNumbering      = "DOC_NUMBERING";
    public const string ApprovalRules     = "APPROVAL_RULES";
    public const string Roles             = "ROLES";
    public const string Warehouse         = "WAREHOUSE";
    public const string DocTemplates      = "DOC_TEMPLATES";
    public const string EmailTemplates    = "EMAIL_TEMPLATES";
    /// <summary>HRM defaults (D-30, D-90): leave types, salary components, tax slabs. Applies last; needs MODULE_HRM.</summary>
    public const string Hrm               = "HRM";

    public static readonly IReadOnlyList<string> ApplyOrder =
        [Currency, Coa, Tax, Fiscal, PaymentTerms, Uom, ProductCategories, DocNumbering, ApprovalRules, Roles, Warehouse, DocTemplates, EmailTemplates, Hrm];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupResetMode { Merge, Overwrite, Full }

/// <summary>A record a seeder created (or reused) — stored by Onboarding in onboarding.SeededRecords (D-5).</summary>
public sealed record SeededRecordRef(string NaturalKey, string EntityRef, string Fingerprint, bool Reused = false);

/// <summary>What Onboarding passes a seeder for one apply/reset.</summary>
public sealed class SetupSeedContext
{
    /// <summary>The org's country (ISO alpha-2 or GLOBAL) and industry chosen in the wizard.</summary>
    public string CountryCode { get; init; } = "GLOBAL";
    public string IndustryType { get; init; } = "GLOBAL";
    /// <summary>SetupDefaults version of the payload being applied (BR-50-013).</summary>
    public int Version { get; init; }
    public int? UserId { get; init; }
    /// <summary>Wizard input that is not in the defaults (e.g. Step 6 warehouses), category-specific JSON.</summary>
    public JsonElement? WizardInput { get; init; }
    /// <summary>Records this org already has provenance for in this category (empty on first apply).</summary>
    public IReadOnlyList<SeededRecordRef> AlreadySeeded { get; init; } = [];
    /// <summary>
    /// REV50-8 — true when the payload is the org admin's edit (wizard payloadOverrides), not the Super Admin default. A seeder that
    /// writes GLOBAL (cross-tenant) rows must take those only from <see cref="DefaultPayload"/>, never from an override.
    /// </summary>
    public bool PayloadIsOverride { get; init; }
    /// <summary>The resolved Super Admin default for this category (null when none resolved). Same as the payload unless overridden.</summary>
    public JsonElement? DefaultPayload { get; init; }
}

public sealed class SetupPreview
{
    public string Category { get; init; } = string.Empty;
    /// <summary>Records the payload contains.</summary>
    public int InPayload { get; init; }
    /// <summary>Of those, already present in the org (by natural key) — they will be skipped.</summary>
    public int AlreadyPresent { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed class SetupApplyResult
{
    public string Category { get; init; } = string.Empty;
    public int Created { get; init; }
    public int Skipped { get; init; }
    public int Removed { get; init; }
    /// <summary>Every record created or reused by this call (Onboarding upserts provenance).</summary>
    public List<SeededRecordRef> Seeded { get; init; } = new();
    /// <summary>Provenance rows to drop (records removed by Overwrite/Full).</summary>
    public List<string> RemovedKeys { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed class SetupCategoryStatus
{
    public string Category { get; init; } = string.Empty;
    public int SeededCount { get; init; }
    public int CurrentCount { get; init; }
    /// <summary>Seeded records whose current values differ from the seeded fingerprint.</summary>
    public List<string> ModifiedKeys { get; init; } = new();
    /// <summary>Seeded records that no longer exist.</summary>
    public List<string> MissingKeys { get; init; } = new();
}

/// <summary>Reset refused (BR-50-008): records referenced by documents.</summary>
public sealed class SetupReferenceCheck
{
    public int ReferencedCount { get; init; }
    public List<string> ReferencedKeys { get; init; } = new();
}

/// <summary>
/// One DataCategory, implemented by the module that owns its tables (D-3). Never throws for data problems —
/// returns warnings; throws only for programming errors. Every method runs for the ambient organization.
/// </summary>
public interface ISetupCategorySeeder
{
    string Category { get; }
    /// <summary>Module that must be enabled for this category to apply (null = always). D-10.</summary>
    string? RequiresModule { get; }
    /// <summary>Validates the payload shape for this category (Super Admin save + wizard). Empty = valid.</summary>
    IReadOnlyList<string> ValidatePayload(JsonElement payload);
    Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default);
    /// <summary>Seeds what is missing (idempotent by natural key, BR-50-004). Own transaction.</summary>
    Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default);
    Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default);
    Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default);
    /// <summary>Merge = add missing; Overwrite = replace seeded (keep custom); Full = delete all + re-seed (refused if referenced).</summary>
    Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default);
}

// ── Sample data (D-27) ─────────────────────────────────────────────────────────────────────────────────────

public sealed record DemoRecordRef(string EntityType, string EntityRef);

public sealed class SampleDataPlan
{
    public Dictionary<string, int> Counts { get; init; } = new();
}

/// <summary>A module that creates and removes its own demo records (Suppliers, Inventory, Demand…). Order = <see cref="Order"/>.</summary>
public interface ISampleDataContributor
{
    /// <summary>Create in ascending order, delete in descending (customers 10, products 20, orders 30).</summary>
    int Order { get; }
    string? RequiresModule { get; }
    Task<SampleDataPlan> PreviewAsync(CancellationToken ct = default);
    /// <summary>Creates the demo records; may read refs created by lower-order contributors.</summary>
    Task<IReadOnlyList<DemoRecordRef>> CreateAsync(IReadOnlyList<DemoRecordRef> createdSoFar, CancellationToken ct = default);
    /// <summary>Deletes the given refs it owns; returns the refs kept because real data uses them.</summary>
    Task<IReadOnlyList<DemoRecordRef>> DeleteAsync(IReadOnlyList<DemoRecordRef> refs, CancellationToken ct = default);
}

// ── Onboarding checklist signals (D-28) ────────────────────────────────────────────────────────────────────

public static class OnboardingStepCodes
{
    public const string CompleteSetup = "COMPLETE_SETUP";
    public const string AddCustomer   = "ADD_CUSTOMER";
    public const string AddProduct    = "ADD_PRODUCT";
    public const string AddSupplier   = "ADD_SUPPLIER";
    public const string CreateSo      = "CREATE_SO";
    public const string CreatePo      = "CREATE_PO";
    public const string ConnectQb     = "CONNECT_QB";
    public const string InviteTeam    = "INVITE_TEAM";
    public const string ImportData    = "IMPORT_DATA";
    /// <summary>A50 D-30 / D-94 — HRM steps, shown only while MODULE_HRM is on.</summary>
    public const string HrmSetup       = "HRM_SETUP";
    public const string HrmAddEmployee = "HRM_ADD_EMPLOYEE";
    public const string HrmVerifyTax   = "HRM_VERIFY_TAX";
}

/// <summary>Answers whether the ambient org has done a step (non-demo records only). Implemented by the owning module.</summary>
public interface IOnboardingSignal
{
    IReadOnlyCollection<string> StepCodes { get; }
    Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default);
}

/// <summary>Lets a module tick a step at once (after commit). Implemented by Onboarding; Null default.</summary>
public interface IOnboardingProgress
{
    Task MarkCompletedAsync(string stepCode, CancellationToken ct = default);
}

public sealed class NullOnboardingProgress : IOnboardingProgress
{
    public Task MarkCompletedAsync(string stepCode, CancellationToken ct = default) => Task.CompletedTask;
}

// ── Branding & templates for existing renderers/senders (D-25, D-26) ─────────────────────────────────────────

public static class DocumentTemplateTypes
{
    public const string SalesInvoice      = "SalesInvoice";
    public const string Quotation         = "Quotation";
    public const string PurchaseOrder     = "PurchaseOrder";
    public const string DeliveryNote      = "DeliveryNote";
    public const string PaymentReceipt    = "PaymentReceipt";
    public const string CustomerStatement = "CustomerStatement";

    public static readonly IReadOnlyList<string> All = [SalesInvoice, Quotation, PurchaseOrder, DeliveryNote, PaymentReceipt, CustomerStatement];
}

/// <summary>What a QuestPDF renderer needs from the org's default template of a type. Null fields = renderer default.</summary>
public sealed class DocumentBranding
{
    public string  TemplateType  { get; init; } = string.Empty;
    public byte[]? LogoBytes     { get; init; }
    /// <summary>"#RRGGBB".</summary>
    public string? PrimaryColor  { get; init; }
    public string? HeaderHtml    { get; init; }
    /// <summary>Non-empty = render this body (placeholders substituted) instead of the built-in layout.</summary>
    public string? BodyHtml      { get; init; }
    public string? FooterHtml    { get; init; }
    public bool    ShowBankDetails { get; init; }
    public string? BankDetailsText { get; init; }
    public string? TermsAndConditions { get; init; }
    public string? CompanyName   { get; init; }
    public string? CompanyAddress { get; init; }
    public string? CompanyTaxId  { get; init; }
    public string? CompanyPhone  { get; init; }
    public string? CompanyEmail  { get; init; }
}

public interface IDocumentBrandingProvider
{
    /// <summary>The ambient org's default template for the type, or null (renderer keeps its built-in look).</summary>
    Task<DocumentBranding?> GetAsync(string templateType, CancellationToken ct = default);
}

public sealed class NullDocumentBrandingProvider : IDocumentBrandingProvider
{
    public Task<DocumentBranding?> GetAsync(string templateType, CancellationToken ct = default) => Task.FromResult<DocumentBranding?>(null);
}

public static class EmailTemplateTypes
{
    public const string InvoiceSent      = "InvoiceSent";
    public const string QuotationSent    = "QuotationSent";
    public const string PoToSupplier     = "POToSupplier";
    public const string PaymentReminder  = "PaymentReminder";
    public const string PaymentReceived  = "PaymentReceived";
    public const string WelcomeEmail     = "WelcomeEmail";
    /// <summary>Seeded; sent by HRM (D-93).</summary>
    public const string PayslipSent      = "PayslipSent";

    public static readonly IReadOnlyList<string> All = [InvoiceSent, QuotationSent, PoToSupplier, PaymentReminder, PaymentReceived, WelcomeEmail, PayslipSent];
}

public sealed record RenderedEmail(string Subject, string BodyHtml);

public interface IEmailTemplateRenderer
{
    /// <summary>Renders the ambient org's default template of the type; null = no template (caller uses its built-in text).</summary>
    Task<RenderedEmail?> RenderAsync(string templateType, IReadOnlyDictionary<string, string?> placeholders, CancellationToken ct = default);
}

public sealed class NullEmailTemplateRenderer : IEmailTemplateRenderer
{
    public Task<RenderedEmail?> RenderAsync(string templateType, IReadOnlyDictionary<string, string?> placeholders, CancellationToken ct = default) =>
        Task.FromResult<RenderedEmail?>(null);
}
