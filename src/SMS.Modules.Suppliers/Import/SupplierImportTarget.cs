using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Suppliers.Import.PartnerImportSupport;

namespace SMS.Modules.Suppliers.Import;

/// <summary>
/// A40 (TSUP) — SUPPLIER import target over suppliers.BusinessPartners (IsVendor). D-4: a created supplier is approved on
/// import — this system's approved state is Status ACTIVE with ApprovedBy / OnboardingDate / StatusChangedBy/At set to
/// the importing user — and the approval is written to the audit log as "Imported" (there is no partner status-history
/// table). Matching is by code (mandatory here; any partner of the organization — a customer with that code gains the
/// vendor role). payment_terms_days is stored in PaymentTermsDays (PreferredPaymentTerms, a Lookups payment-term id, is
/// left alone); currency_code must be an active currency of the organization and becomes the default purchase currency.
/// </summary>
internal sealed class SupplierImportTarget : IImportTarget
{
    public const string ImportedNote = "Imported";

    private readonly SuppliersDbContext _db;
    private readonly IOrgCurrencyLookup? _currencies;
    private readonly PartnerQuickBooksPublisher? _quickBooks;
    private readonly ISupplierEventPublisher? _events;
    private readonly IAuditService? _audit;
    private readonly ILogger _log;

    public SupplierImportTarget(SuppliersDbContext db, IOrgCurrencyLookup? currencies = null, PartnerQuickBooksPublisher? quickBooks = null,
        ISupplierEventPublisher? events = null, IAuditService? audit = null, ILogger<SupplierImportTarget>? log = null)
    {
        _db = db;
        _currencies = currencies;
        _quickBooks = quickBooks;
        _events = events;
        _audit = audit;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Supplier, "Suppliers",
        "Supplier master (business partners with the vendor role). Imported suppliers are created approved (active).",
        ImportOrder: 8, IsOpeningBalance: false, MatchFields: ["code", "email"],
        Fields:
        [
            new("code", "Supplier Code", "Existing code = update that supplier.", ImportFieldTypes.Text, true, true, 1, "SUP-001", MaxLength: CodeMax,
                Aliases: ["supplier_code", "supplier code", "vendor_code", "vendor code", "vendor_no", "vendor number", "CardCode", "LIFNR"]),
            new("name", "Supplier Name", null, ImportFieldTypes.Text, true, false, 2, "Global Parts Ltd", MaxLength: NameMax,
                Aliases: ["supplier_name", "supplier name", "vendor_name", "vendor name", "company", "CardName", "NAME1"]),
            new("email", "Email", null, ImportFieldTypes.Email, false, true, 3, "sales@globalparts.example", MaxLength: EmailMax,
                Aliases: ["e-mail", "email_address", "E_Mail", "SMTP_ADDR"]),
            new("phone", "Phone", null, ImportFieldTypes.Phone, false, false, 4, "+92 42 7654321", MaxLength: PhoneMax,
                Aliases: ["telephone", "phone_number", "Phone1", "TELF1"]),
            new("address_line1", "Address", null, ImportFieldTypes.Text, false, false, 5, "Plot 7, Industrial Estate", MaxLength: AddressMax,
                Aliases: ["address", "street", "address1", "STRAS"]),
            new("city", "City", null, ImportFieldTypes.Text, false, false, 6, "Karachi", MaxLength: CityMax, Aliases: ["town", "ORT01"]),
            new("country_code", "Country", "ISO alpha-2 / alpha-3 code or English name; stored as alpha-2.", ImportFieldTypes.Lookup, false, false, 7, "PK",
                LookupList: "countries", Aliases: ["country", "LAND1"]),
            new("tax_number", "Tax Number", null, ImportFieldTypes.Text, false, false, 8, "7654321-0", MaxLength: TaxMax,
                Aliases: ["tax_id", "ntn", "vat_number", "STCD1", "LicTradNum"]),
            new("payment_terms_days", "Payment Terms (days)", null, ImportFieldTypes.Integer, false, false, 9, "30",
                MinValue: 0m, MaxValue: MaxTermsDays, Aliases: ["payment_terms", "terms_days", "net_days", "ZTERM"]),
            new("currency_code", "Currency", "Default purchase currency: an active currency of the organization.", ImportFieldTypes.Lookup, false, false, 10, "PKR",
                LookupList: "currencies", Aliases: ["currency", "purchase_currency", "WAERS", "Currency"]),
            new("is_active", "Active", null, ImportFieldTypes.Boolean, false, false, 11, "true", DefaultValue: "true",
                Aliases: ["active", "enabled"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
    [
        new("countries", CountryCodes),
        new("currencies", (await CurrenciesAsync(organizationId, ct)).Keys.OrderBy(c => c, StringComparer.Ordinal).Take(1000).ToList())
    ];

    private async Task<Dictionary<string, Guid>> CurrenciesAsync(Guid org, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (_currencies is null) return map;
        foreach (var c in await _currencies.ListAsync(org, activeOnly: true, ct))
            if (!string.IsNullOrWhiteSpace(c.Code)) map.TryAdd(c.Code.Trim().ToUpperInvariant(), c.CurrencyId);
        return map;
    }

    // ── Reading a row ──────────────────────────────────────────────────────────

    private sealed record Parsed(string? Code, string? Name, string? Email, string? Phone, string? Address1, string? City,
        string? Country, string? Tax, int? Terms, Guid? CurrencyId, bool? Active, List<ImportFieldError> Errors);

    private static Parsed Parse(IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, Guid> currencies)
    {
        var r = new RowReader(values);
        var code = r.Text("code", "Supplier code", CodeMax, required: true);
        if (code is not null && string.Equals(code, CustomerTypes.WalkInCode, StringComparison.OrdinalIgnoreCase))
            r.Error("code", WalkInProtected, "The walk-in customer cannot be imported or updated.");
        Guid? currencyId = null;
        if (r.Raw("currency_code") is { } cur)
        {
            if (currencies.TryGetValue(cur, out var id)) currencyId = id;
            else r.Error("currency_code", LookupNotFound, $"Currency {cur.ToUpperInvariant()} is not an active currency of this organization.");
        }
        return new Parsed(code, r.Text("name", "Supplier name", NameMax), r.Email("email", "Email"), r.Text("phone", "Phone", PhoneMax),
            r.Text("address_line1", "Address", AddressMax), r.Text("city", "City", CityMax), r.Country("country_code", "Country"),
            r.Text("tax_number", "Tax number", TaxMax), r.Int("payment_terms_days", "Payment terms", 0, MaxTermsDays),
            currencyId, r.Bool("is_active", "Active"), r.Errors);
    }

    // ── Existing partners ──────────────────────────────────────────────────────

    private sealed record Snapshot(Guid Uuid, string Code, bool IsVendor, bool IsCustomer, bool IsCarrier, bool IsServiceProvider,
        bool IsSystem, bool IsDelete, string Status, bool IsActive);

    private async Task<(Dictionary<string, Snapshot> ByCode, Dictionary<Guid, Snapshot> ByUuid)> LoadAsync(
        Guid org, IEnumerable<IReadOnlyDictionary<string, string?>> rows, IEnumerable<Guid> uuids, CancellationToken ct)
    {
        var codes = rows.Select(v => Get(v, "code")).OfType<string>().Select(Norm).Distinct().ToList();
        var ids = uuids.Distinct().ToList();
        var found = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && (codes.Contains(p.SupplierCode.ToUpper()) || ids.Contains(p.UUID)))
            .Select(p => new Snapshot(p.UUID, p.SupplierCode, p.IsVendor, p.IsCustomer, p.IsCarrier, p.IsServiceProvider,
                p.IsSystem, p.IsDelete, p.Status, p.IsActive))
            .ToListAsync(ct);
        var byCode = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in found) byCode.TryAdd(s.Code.Trim(), s);
        return (byCode, found.ToDictionary(s => s.Uuid));
    }

    private static void CheckTarget(Snapshot s, Parsed p)
    {
        if (s.IsSystem)
        {
            if (p.Errors.All(e => e.Code != WalkInProtected))
                p.Errors.Add(new ImportFieldError("code", WalkInProtected, "The walk-in customer cannot be imported or updated.", p.Code));
        }
        else if (s.IsDelete)
            p.Errors.Add(new ImportFieldError("code", CodeInUse, $"Code {s.Code} belongs to a deleted partner and cannot be reused.", p.Code));
        else if (!s.IsVendor && TypeCodeOf(true, s.IsCustomer, s.IsCarrier, s.IsServiceProvider) is null)
            p.Errors.Add(new ImportFieldError("code", PartnerRoleConflict, $"Partner {s.Code} cannot also be a supplier.", p.Code));
        else if (p.Active is { } active && active != s.IsActive && NextStatus(s.Status, active) is null)
            p.Errors.Add(new ImportFieldError("is_active", StatusChangeNotAllowed,
                $"A {s.Status} supplier cannot be {(active ? "activated" : "deactivated")} by import; use the supplier screen.",
                active ? "true" : "false"));
    }

    /// <summary>The supplier status machine's activation steps only: INACTIVE → ACTIVE and ACTIVE → INACTIVE.</summary>
    private static string? NextStatus(string current, bool active) => (current, active) switch
    {
        ("INACTIVE", true) => "ACTIVE",
        ("ACTIVE", false) => "INACTIVE",
        _ => null
    };

    // ── IImportTarget ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        var currencies = await CurrenciesAsync(organizationId, ct);
        var (byCode, byUuid) = await LoadAsync(organizationId, rows.Select(r => r.Values), rows.Select(r => r.ExistingEntityUuid).OfType<Guid>(), ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportRowCheck>(rows.Count);
        foreach (var row in rows)
        {
            var p = Parse(row.Values, currencies);
            Snapshot? match = null;
            if (row.ExistingEntityUuid is { } given && byUuid.TryGetValue(given, out var byRef)) match = byRef;
            else if (p.Code is not null && byCode.TryGetValue(p.Code, out var hit)) match = hit;
            if (match is not null) CheckTarget(match, p);
            else if (p.Name is null && p.Errors.All(e => e.Field != "name"))
                p.Errors.Add(new ImportFieldError("name", Required, "Supplier name is required.", null));
            if (p.Code is not null && !seen.Add(p.Code))
                p.Errors.Add(new ImportFieldError("code", DuplicateInFile, $"Code {p.Code} appears more than once in the file.", p.Code));
            result.Add(new ImportRowCheck(row.RowNumber, match?.Uuid, p.Errors));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        var updates = rows.Where(r => r.Action == ActionUpdate).ToList();
        if (updates.FirstOrDefault(r => r.ExistingEntityUuid is null) is { } bad)
            throw new BadRequestException($"Row {bad.RowNumber}: an update needs the existing supplier.");

        var currencies = await CurrenciesAsync(organizationId, ct);
        var (byCode, byUuid) = await LoadAsync(organizationId, rows.Select(r => r.Values), updates.Select(r => r.ExistingEntityUuid!.Value), ct);
        var updateIds = updates.Select(r => r.ExistingEntityUuid!.Value).Distinct().ToList();
        var tracked = await _db.BusinessPartners.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == organizationId && updateIds.Contains(p.UUID) && !p.IsDelete)
            .ToDictionaryAsync(p => p.UUID, ct);

        var now = DateTime.UtcNow;
        var batchCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<(int Row, string Status, BusinessPartner Partner, bool Approved)>(rows.Count);
        try
        {
            foreach (var row in rows)
            {
                var p = Parse(row.Values, currencies);
                if (row.Action == ActionUpdate)
                {
                    var id = row.ExistingEntityUuid!.Value;
                    if (!tracked.TryGetValue(id, out var partner) || !byUuid.TryGetValue(id, out var snap))
                        throw new BadRequestException($"Row {row.RowNumber}: the supplier to update no longer exists.");
                    CheckTarget(snap, p);
                    ThrowIfAny(row.RowNumber, p.Errors);
                    var approved = ApplyUpdate(partner, p, userId, now);
                    outcomes.Add((row.RowNumber, StatusUpdated, partner, approved));
                }
                else
                {
                    if (p.Name is null && p.Errors.All(e => e.Field != "name"))
                        p.Errors.Add(new ImportFieldError("name", Required, "Supplier name is required.", null));
                    if (p.Code is not null && (byCode.ContainsKey(p.Code) || !batchCodes.Add(p.Code)))
                        p.Errors.Add(new ImportFieldError("code", CodeInUse, $"Code {p.Code} is already used by another partner.", p.Code));
                    ThrowIfAny(row.RowNumber, p.Errors);
                    var partner = Create(organizationId, p, userId, now);
                    _db.BusinessPartners.Add(partner);
                    outcomes.Add((row.RowNumber, StatusImported, partner, true));
                }
            }
            await _db.SaveChangesAsync(ct);   // one SaveChanges = one transaction: the whole batch or nothing
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }

        foreach (var o in outcomes) await AfterSaveAsync(o.Partner, o.Approved, userId, ct);
        return outcomes.Select(o => new ImportRowOutcome(o.Row, o.Status, o.Partner.UUID)).ToList();
    }

    private static BusinessPartner Create(Guid org, Parsed p, int userId, DateTime now)
    {
        var active = p.Active ?? true;
        return new BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = org, SupplierCode = p.Code!, SupplierName = p.Name!,
            PartnerType = PartnerCode.Of(PartnerType.Vendor), IsVendor = true, IsCustomer = false,
            Email = p.Email, Phone = p.Phone, AddressLine1 = p.Address1, City = p.City, Country = p.Country, TaxId = p.Tax,
            PaymentTermsDays = p.Terms ?? 0, PreferredCurrency = p.CurrencyId,
            // D-4 — approved on import.
            Status = active ? "ACTIVE" : "INACTIVE", IsActive = active,
            ApprovedBy = userId, OnboardingDate = now, StatusChangedBy = userId, StatusChangedAt = now,
            CreatedBy = userId, CreatedDate = now
        };
    }

    /// <returns>True when this update made the partner an approved vendor (it had no vendor role before).</returns>
    private static bool ApplyUpdate(BusinessPartner x, Parsed p, int userId, DateTime now)
    {
        var becameVendor = false;
        if (!x.IsVendor)
        {
            x.IsVendor = true;
            x.PartnerType = TypeCodeOf(true, x.IsCustomer, x.IsCarrier, x.IsServiceProvider)!;
            if (x.Status == "ACTIVE" && x.ApprovedBy is null)
            {
                x.ApprovedBy = userId;
                x.OnboardingDate ??= now;
                becameVendor = true;
            }
        }
        if (p.Name is not null) x.SupplierName = p.Name;
        if (p.Email is not null) x.Email = p.Email;
        if (p.Phone is not null) x.Phone = p.Phone;
        if (p.Address1 is not null) x.AddressLine1 = p.Address1;
        if (p.City is not null) x.City = p.City;
        if (p.Country is not null) x.Country = p.Country;
        if (p.Tax is not null) x.TaxId = p.Tax;
        if (p.Terms is { } terms) x.PaymentTermsDays = terms;
        if (p.CurrencyId is { } currency) x.PreferredCurrency = currency;
        if (p.Active is { } active && active != x.IsActive)
        {
            x.Status = NextStatus(x.Status, active)!;
            x.IsActive = active;
            x.StatusChangedBy = userId;
            x.StatusChangedAt = now;
        }
        x.ModifiedBy = userId;
        x.ModifiedDate = now;
        return becameVendor;
    }

    /// <summary>The post-save behaviour of a normal save/approval, best-effort (a failure never undoes the import).</summary>
    private async Task AfterSaveAsync(BusinessPartner partner, bool approved, int userId, CancellationToken ct)
    {
        if (approved)
        {
            try
            {
                if (_audit is not null)
                    await _audit.LogAsync(userId, null, "SUPPLIERS", "STATUS_CHANGE", "SUPPLIER", partner.UUID,
                        fieldChanged: "Status", newValue: partner.Status, notes: ImportedNote);
                if (_events is not null) await _events.PublishSupplierApprovedAsync(partner.UUID, partner.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Imported supplier {PartnerUuid} was saved, but its approval could not be recorded/published.", partner.UUID);
            }
        }
        if (_quickBooks is not null) await _quickBooks.PublishAsync(partner.UUID);   // never throws
    }
}
