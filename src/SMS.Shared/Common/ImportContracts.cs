namespace SMS.Shared.Common;

// A40 — Data Import & Upload Framework (docs/data-import/ADDENDUM-40-ANALYSIS.md, API-CONTRACT.md).
// The Import module (SMS.Modules.Import) owns sessions, parsing, mapping, generic field validation, external references,
// templates and execution. Each entity it can import is an IImportTarget implemented INSIDE the module that owns that
// data, so every row goes through that module's own rules. Organization is always explicit. Owner: COORD.

/// <summary>Import entity type codes (D-2). Order = the migration wizard sequence.</summary>
public static class ImportEntityTypes
{
    public const string Unit = "UNIT", ProductCategory = "PRODUCT_CATEGORY", Product = "PRODUCT", Warehouse = "WAREHOUSE",
        WarehouseLocation = "WAREHOUSE_LOCATION", TaxCode = "TAX_CODE", Customer = "CUSTOMER", Supplier = "SUPPLIER",
        SupplierContact = "SUPPLIER_CONTACT", PricingRule = "PRICING_RULE", OpeningStock = "OPENING_STOCK",
        CustomerOpeningBalance = "CUSTOMER_OPENING_BALANCE", SupplierOpeningBalance = "SUPPLIER_OPENING_BALANCE",
        /// <summary>A49 E-14.</summary>
        Employee = "HRM_EMPLOYEE";
}

/// <summary>Field kinds (spec field_type): TEXT INTEGER DECIMAL DATE BOOLEAN LOOKUP EMAIL PHONE CURRENCY.</summary>
public static class ImportFieldTypes
{
    public const string Text = "TEXT", Integer = "INTEGER", Decimal = "DECIMAL", Date = "DATE", Boolean = "BOOLEAN",
        Lookup = "LOOKUP", Email = "EMAIL", Phone = "PHONE", Currency = "CURRENCY";
}

/// <summary>One column a target accepts. The Import module runs the generic checks from these (required, type, length,
/// range, precision, regex, email, phone); LOOKUP values are resolved by the target itself.</summary>
public sealed record ImportFieldDefinition(
    string FieldName, string DisplayName, string? Description, string FieldType, bool IsRequired, bool IsKey,
    int SortOrder, string? SampleValue, int? MaxLength = null, decimal? MinValue = null, decimal? MaxValue = null,
    int? DecimalPlaces = null, string? ValidationRegex = null, string? DefaultValue = null,
    /// <summary>For LOOKUP: a key of the target's template lists (dropdown source), e.g. "categories".</summary>
    string? LookupList = null, IReadOnlyList<string>? Aliases = null,
    /// <summary>A49: refuse dates that could be read two ways (05/01/2024): only year-first, a named month or an Excel date.</summary>
    bool UnambiguousDateOnly = false);

/// <summary>What a target is. MatchFields = natural key used for duplicate detection (spec match_fields).</summary>
public sealed record ImportProfileDefinition(
    string EntityType, string Name, string? Description, int ImportOrder, bool IsOpeningBalance,
    IReadOnlyList<string> MatchFields, IReadOnlyList<ImportFieldDefinition> Fields,
    /// <summary>Opening balances force duplicate strategy ERROR (IMP-OB-06).</summary>
    bool AllowUpdate = true,
    /// <summary>A49: listed only while this module is on for the organization, and not part of the migration workbook. Null = always.</summary>
    string? RequiresModule = null,
    /// <summary>A49: every one of these, on top of the import rights, to start, validate or run an import of this type.</summary>
    IReadOnlyList<string>? RequiredPermissions = null,
    /// <summary>
    /// A49: fields holding S3 data. A profile with any: its rows are encrypted at rest, these fields and every unmapped column are masked in
    /// each read and report, never echoed in an error, and every read needs <see cref="RequiredPermissions"/> too.
    /// </summary>
    IReadOnlyList<string>? SensitiveFields = null,
    /// <summary>A49: replaces the generic External ID help when re-importing does something else in this profile.</summary>
    string? ExternalIdHelp = null);

/// <summary>A row after generic parsing: values keyed by FieldName, already trimmed, empty → null, booleans "true"/"false",
/// dates ISO yyyy-MM-dd, decimals invariant culture.</summary>
public sealed record ImportRowInput(int RowNumber, IReadOnlyDictionary<string, string?> Values, Guid? ExistingEntityUuid);

public sealed record ImportFieldError(string Field, string Code, string Message, string? Value);

/// <summary>Target validation result per row: errors (codes per spec: LOOKUP_NOT_FOUND, DUPLICATE_IN_DB, MISSING_DEPENDENCY,
/// INVALID_DATE, OUT_OF_RANGE, or target-specific UPPER_SNAKE codes) and the existing record matched by MatchFields.</summary>
public sealed record ImportRowCheck(int RowNumber, Guid? ExistingEntityUuid, IReadOnlyList<ImportFieldError> Errors);

/// <summary>ApplyAsync action per row: CREATE or UPDATE (UPDATE only when ExistingEntityUuid is set).</summary>
public sealed record ImportApplyRow(int RowNumber, IReadOnlyDictionary<string, string?> Values, string Action, Guid? ExistingEntityUuid);

/// <summary>Outcome: IMPORTED (created) / UPDATED, the entity uuid; a row error fails the whole batch (the target throws).</summary>
public sealed record ImportRowOutcome(int RowNumber, string Status, Guid EntityUuid);

/// <summary>A dropdown list for templates and the lookup suggestions: key → active codes (≤ 1000).</summary>
public sealed record ImportTemplateList(string Key, IReadOnlyList<string> Values);

/// <summary>
/// A40 D-3 — one importable entity, implemented by its owning module and registered in DI (IEnumerable). Rules:
/// ValidateAsync is read-only and checks the batch as a whole (in-batch hierarchy such as parent categories earlier in the
/// file counts as existing); ApplyAsync writes a batch in ONE transaction of the owning module (all-or-nothing, IMP-EXC-02),
/// sets created/modified by <paramref name="userId"/>, and on UPDATE leaves fields whose value is null unchanged (IMP-EXC-04).
/// </summary>
public interface IImportTarget
{
    ImportProfileDefinition Describe();
    Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default);
    Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default);
}

/// <summary>A40 D-7 — the organization's opening balance date (Finance owns it). Null = not set (opening imports refused:
/// "Set the opening balance date in Finance settings first.").</summary>
public interface IOpeningBalanceDateProvider
{
    Task<DateOnly?> GetAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// A40 QA-R1 — the latest date of the organization's posted opening stock (inventory-ledger OPENING_STOCK entries), or null
/// when none. Implemented in Inventory; Finance asks it before the opening balance date is cleared or moved earlier.
/// </summary>
public interface IOpeningStockDateSource
{
    Task<DateOnly?> GetLatestAsync(Guid organizationId, CancellationToken ct = default);
}
