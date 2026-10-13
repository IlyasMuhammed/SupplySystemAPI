using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Suppliers.Import.PartnerImportSupport;

namespace SMS.Modules.Suppliers.Import;

/// <summary>
/// A40 (TSUP) — CUSTOMER import target over suppliers.BusinessPartners (IsCustomer), following the A37 customer master
/// rules of <see cref="Services.CustomerService"/>: blank code → next C-00001 code; type COMPANY by default; the walk-in
/// customer (C-WALKIN / IsSystem) is never matched for update; walk-in-type customers have no credit; the credit limit is
/// written only while FEATURE_CREDIT_MANAGEMENT is on (otherwise silently ignored, as the customer API does).
/// Matching: by code when the row has one (any partner of the organization — a vendor with that code gains the customer
/// role); otherwise by email among the organization's customers (exactly one match, more = AMBIGUOUS_MATCH).
/// </summary>
internal sealed class CustomerImportTarget : IImportTarget
{
    private readonly SuppliersDbContext _db;
    private readonly IModuleGate? _gate;
    private readonly PartnerQuickBooksPublisher? _quickBooks;

    public CustomerImportTarget(SuppliersDbContext db, IModuleGate? gate = null, PartnerQuickBooksPublisher? quickBooks = null)
    {
        _db = db;
        _gate = gate;
        _quickBooks = quickBooks;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Customer, "Customers",
        "Customer master (business partners with the customer role). Blank code = next C-00001 code.",
        ImportOrder: 7, IsOpeningBalance: false, MatchFields: ["code", "email"],
        Fields:
        [
            new("code", "Customer Code", "Blank = generated (C-00001…). Existing code = update that customer.", ImportFieldTypes.Text, false, true, 1, "C-00001", MaxLength: CodeMax,
                Aliases: ["customer_code", "customer code", "cust_code", "customer_no", "customer number", "account_number", "CardCode", "KUNNR"]),
            new("name", "Customer Name", null, ImportFieldTypes.Text, true, false, 2, "Acme Trading", MaxLength: NameMax,
                Aliases: ["customer_name", "customer name", "company", "company_name", "CardName", "NAME1"]),
            new("email", "Email", "Also matches an existing customer when the code is blank.", ImportFieldTypes.Email, false, true, 3, "accounts@acme.example", MaxLength: EmailMax,
                Aliases: ["e-mail", "email_address", "E_Mail", "SMTP_ADDR"]),
            new("phone", "Phone", null, ImportFieldTypes.Phone, false, false, 4, "+92 42 1234567", MaxLength: PhoneMax,
                Aliases: ["telephone", "phone_number", "Phone1", "TELF1"]),
            new("mobile", "Mobile", null, ImportFieldTypes.Phone, false, false, 5, "+92 300 1234567", MaxLength: PhoneMax,
                Aliases: ["cell", "cellular", "mobile_number", "Cellular"]),
            new("address_line1", "Address Line 1", null, ImportFieldTypes.Text, false, false, 6, "12 Mall Road", MaxLength: AddressMax,
                Aliases: ["address", "street", "address1", "STRAS"]),
            new("address_line2", "Address Line 2", null, ImportFieldTypes.Text, false, false, 7, null, MaxLength: AddressMax,
                Aliases: ["address2"]),
            new("city", "City", null, ImportFieldTypes.Text, false, false, 8, "Lahore", MaxLength: CityMax, Aliases: ["town", "ORT01"]),
            new("state_province", "State / Province", null, ImportFieldTypes.Text, false, false, 9, "Punjab", MaxLength: StateMax,
                Aliases: ["state", "province", "region", "REGIO"]),
            new("postal_code", "Postal Code", null, ImportFieldTypes.Text, false, false, 10, "54000", MaxLength: PostalMax,
                Aliases: ["zip", "zip_code", "postcode", "PSTLZ"]),
            new("country_code", "Country", "ISO alpha-2 / alpha-3 code or English name; stored as alpha-2.", ImportFieldTypes.Lookup, false, false, 11, "PK",
                LookupList: "countries", Aliases: ["country", "LAND1"]),
            new("tax_number", "Tax Number", null, ImportFieldTypes.Text, false, false, 12, "1234567-8", MaxLength: TaxMax,
                Aliases: ["tax_id", "ntn", "vat_number", "STCD1", "LicTradNum"]),
            new("customer_type", "Customer Type", "INDIVIDUAL, COMPANY, EMPLOYEE or WALK_IN (blank = COMPANY on create).", ImportFieldTypes.Lookup, false, false, 13, CustomerTypes.Company,
                DefaultValue: CustomerTypes.Company, LookupList: "customer_types", Aliases: ["type", "customer_category"]),
            new("credit_limit", "Credit Limit", "Applied only while credit management is enabled.", ImportFieldTypes.Decimal, false, false, 14, "50000",
                MinValue: 0m, DecimalPlaces: 2, Aliases: ["credit", "CreditLine", "KLIMK"]),
            new("payment_terms_days", "Payment Terms (days)", null, ImportFieldTypes.Integer, false, false, 15, "30",
                MinValue: 0m, MaxValue: MaxTermsDays, Aliases: ["payment_terms", "terms_days", "net_days", "ZTERM"]),
            new("is_active", "Active", null, ImportFieldTypes.Boolean, false, false, 16, "true", DefaultValue: "true",
                Aliases: ["active", "enabled"])
        ]);

    public Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportTemplateList>>(
        [
            new("countries", CountryCodes),
            new("customer_types", CustomerTypes.All.ToList())
        ]);

    // ── Reading a row ──────────────────────────────────────────────────────────

    private sealed record Parsed(
        string? Code, string? Name, string? Email, string? Phone, string? Mobile, string? Address1, string? Address2,
        string? City, string? State, string? Postal, string? Country, string? Tax, string? Type, decimal? Credit, int? Terms,
        bool? Active, List<ImportFieldError> Errors);

    private static Parsed Parse(IReadOnlyDictionary<string, string?> values)
    {
        var r = new RowReader(values);
        var code = r.Text("code", "Customer code", CodeMax);
        if (code is not null && string.Equals(code, CustomerTypes.WalkInCode, StringComparison.OrdinalIgnoreCase))
            r.Error("code", WalkInProtected, "The walk-in customer cannot be imported or updated.");
        var type = r.Raw("customer_type")?.ToUpperInvariant();
        if (type is not null && !CustomerTypes.All.Contains(type))
            r.Error("customer_type", LookupNotFound, $"Customer type must be one of {string.Join(", ", CustomerTypes.All)}.");
        return new Parsed(
            code, r.Text("name", "Customer name", NameMax), r.Email("email", "Email"),
            r.Text("phone", "Phone", PhoneMax), r.Text("mobile", "Mobile", PhoneMax),
            r.Text("address_line1", "Address line 1", AddressMax), r.Text("address_line2", "Address line 2", AddressMax),
            r.Text("city", "City", CityMax), r.Text("state_province", "State / province", StateMax),
            r.Text("postal_code", "Postal code", PostalMax), r.Country("country_code", "Country"), r.Text("tax_number", "Tax number", TaxMax),
            type is not null && CustomerTypes.All.Contains(type) ? type : null,
            r.Decimal("credit_limit", "Credit limit", 0m), r.Int("payment_terms_days", "Payment terms", 0, MaxTermsDays),
            r.Bool("is_active", "Active"), r.Errors);
    }

    /// <summary>CUST-04 — a walk-in-type customer (requested or existing type) cannot have a credit limit.</summary>
    private static void CheckCredit(Parsed p, string? existingType)
    {
        var type = p.Type ?? existingType ?? CustomerTypes.Company;
        if (type == CustomerTypes.WalkIn && p.Credit is > 0m)
            p.Errors.Add(new ImportFieldError("credit_limit", CreditNotAllowed, CustomerTypes.MsgWalkInCredit,
                p.Credit.Value.ToString(CultureInfo.InvariantCulture)));
    }

    // ── Existing partners ──────────────────────────────────────────────────────

    private sealed record Snapshot(Guid Uuid, string Code, string? Email, bool IsVendor, bool IsCustomer, bool IsCarrier,
        bool IsServiceProvider, bool IsSystem, bool IsDelete, string? CustomerType);

    private sealed class Known
    {
        public Dictionary<string, Snapshot> ByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<Snapshot>> CustomersByEmail { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, Snapshot> ByUuid { get; } = [];
    }

    private async Task<Known> LoadAsync(Guid org, IEnumerable<IReadOnlyDictionary<string, string?>> rows, IEnumerable<Guid> uuids, CancellationToken ct)
    {
        var list = rows.ToList();
        var codes = list.Select(v => Get(v, "code")).OfType<string>().Select(Norm).Distinct().ToList();
        var emails = list.Select(v => Get(v, "email")?.ToLowerInvariant()).OfType<string>().Distinct().ToList();
        var ids = uuids.Distinct().ToList();

        var found = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && (codes.Contains(p.SupplierCode.ToUpper()) || ids.Contains(p.UUID)
                || (p.IsCustomer && !p.IsDelete && p.Email != null && emails.Contains(p.Email.ToLower()))))
            .Select(p => new Snapshot(p.UUID, p.SupplierCode, p.Email, p.IsVendor, p.IsCustomer, p.IsCarrier, p.IsServiceProvider,
                p.IsSystem, p.IsDelete, p.CustomerType))
            .ToListAsync(ct);

        var known = new Known();
        foreach (var s in found)
        {
            known.ByCode.TryAdd(s.Code.Trim(), s);
            known.ByUuid[s.Uuid] = s;
            if (s.IsCustomer && !s.IsDelete && s.Email is { } e)
            {
                if (!known.CustomersByEmail.TryGetValue(e.Trim(), out var l)) known.CustomersByEmail[e.Trim()] = l = [];
                l.Add(s);
            }
        }
        return known;
    }

    /// <summary>Errors that make a partner unusable as this row's customer (walk-in, deleted code, impossible role mix).</summary>
    private static void CheckTarget(Snapshot s, List<ImportFieldError> errors, string field, string? value)
    {
        if (s.IsSystem)
        {
            if (errors.All(e => e.Code != WalkInProtected))
                errors.Add(new ImportFieldError(field, WalkInProtected, "The walk-in customer cannot be imported or updated.", value));
        }
        else if (s.IsDelete)
            errors.Add(new ImportFieldError(field, CodeInUse, $"Code {s.Code} belongs to a deleted partner and cannot be reused.", value));
        else if (!s.IsCustomer && TypeCodeOf(s.IsVendor, true, s.IsCarrier, s.IsServiceProvider) is null)
            errors.Add(new ImportFieldError(field, PartnerRoleConflict, $"Partner {s.Code} cannot also be a customer.", value));
    }

    private static Snapshot? Match(ImportRowInput row, Parsed p, Known known, List<ImportFieldError> errors)
    {
        if (row.ExistingEntityUuid is { } given && known.ByUuid.TryGetValue(given, out var byRef))
        {
            CheckTarget(byRef, errors, "code", p.Code);
            return byRef;
        }
        if (p.Code is not null)
        {
            if (!known.ByCode.TryGetValue(p.Code, out var byCode)) return null;
            CheckTarget(byCode, errors, "code", p.Code);
            return byCode;
        }
        if (p.Email is not null && known.CustomersByEmail.TryGetValue(p.Email, out var byEmail))
        {
            if (byEmail.Count > 1)
            {
                errors.Add(new ImportFieldError("email", AmbiguousMatch,
                    $"{byEmail.Count} customers have this email ({string.Join(", ", byEmail.Select(s => s.Code))}); add the customer code.", p.Email));
                return null;
            }
            CheckTarget(byEmail[0], errors, "email", p.Email);
            return byEmail[0];
        }
        return null;
    }

    // ── IImportTarget ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        var known = await LoadAsync(organizationId, rows.Select(r => r.Values), rows.Select(r => r.ExistingEntityUuid).OfType<Guid>(), ct);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportRowCheck>(rows.Count);
        foreach (var row in rows)
        {
            var p = Parse(row.Values);
            var match = Match(row, p, known, p.Errors);
            CheckCredit(p, match?.CustomerType);
            if (match is null && p.Name is null && !p.Errors.Any(e => e.Field == "name"))
                p.Errors.Add(new ImportFieldError("name", Required, "Customer name is required.", null));
            if (p.Code is not null && !seenCodes.Add(p.Code))
                p.Errors.Add(new ImportFieldError("code", DuplicateInFile, $"Code {p.Code} appears more than once in the file.", p.Code));
            result.Add(new ImportRowCheck(row.RowNumber, match?.Uuid, p.Errors));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        var updates = rows.Where(r => r.Action == ActionUpdate).ToList();
        if (updates.FirstOrDefault(r => r.ExistingEntityUuid is null) is { } bad)
            throw new BadRequestException($"Row {bad.RowNumber}: an update needs the existing customer.");

        var known = await LoadAsync(organizationId, rows.Select(r => r.Values), updates.Select(r => r.ExistingEntityUuid!.Value), ct);
        var updateIds = updates.Select(r => r.ExistingEntityUuid!.Value).Distinct().ToList();
        var tracked = await _db.BusinessPartners.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == organizationId && updateIds.Contains(p.UUID) && !p.IsDelete)
            .ToDictionaryAsync(p => p.UUID, ct);
        var creditOn = _gate is null || await _gate.IsEnabledAsync(organizationId, ModuleCodes.CreditManagement, ct);

        // CUST-02 — one past the highest C-nnnnn code ever used (deleted rows and this file's explicit codes included).
        var used = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.SupplierCode.StartsWith(CustomerTypes.CodePrefix))
            .Select(p => p.SupplierCode).ToListAsync(ct);
        var nextNumber = used.Concat(rows.Select(r => Get(r.Values, "code")).OfType<string>()).Select(CodeNumber).DefaultIfEmpty(0).Max() + 1;

        var now = DateTime.UtcNow;
        var batchCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<(int Row, string Status, BusinessPartner Partner)>(rows.Count);
        try
        {
            foreach (var row in rows)
            {
                var p = Parse(row.Values);
                if (row.Action == ActionUpdate)
                {
                    var id = row.ExistingEntityUuid!.Value;
                    if (!tracked.TryGetValue(id, out var partner) || !known.ByUuid.TryGetValue(id, out var snap))
                        throw new BadRequestException($"Row {row.RowNumber}: the customer to update no longer exists.");
                    CheckTarget(snap, p.Errors, "code", p.Code);
                    CheckCredit(p, partner.CustomerType);
                    ThrowIfAny(row.RowNumber, p.Errors);
                    ApplyUpdate(partner, p, creditOn, userId, now);
                    outcomes.Add((row.RowNumber, StatusUpdated, partner));
                }
                else
                {
                    CheckCredit(p, null);
                    if (p.Name is null) p.Errors.Add(new ImportFieldError("name", Required, "Customer name is required.", null));
                    if (p.Code is not null && (known.ByCode.ContainsKey(p.Code) || !batchCodes.Add(p.Code)))
                        p.Errors.Add(new ImportFieldError("code", CodeInUse, $"Code {p.Code} is already used by another partner.", p.Code));
                    ThrowIfAny(row.RowNumber, p.Errors);
                    var code = p.Code;
                    while (code is null || known.ByCode.ContainsKey(code) || (p.Code is null && !batchCodes.Add(code)))
                        code = $"{CustomerTypes.CodePrefix}{nextNumber++:D5}";
                    var partner = Create(organizationId, code, p, creditOn, userId, now);
                    _db.BusinessPartners.Add(partner);
                    outcomes.Add((row.RowNumber, StatusImported, partner));
                }
            }
            await _db.SaveChangesAsync(ct);   // one SaveChanges = one transaction: the whole batch or nothing
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }

        if (_quickBooks is not null)
            foreach (var o in outcomes) await _quickBooks.PublishAsync(o.Partner.UUID);   // best-effort, never throws
        return outcomes.Select(o => new ImportRowOutcome(o.Row, o.Status, o.Partner.UUID)).ToList();
    }

    private static int CodeNumber(string code) =>
        code.StartsWith(CustomerTypes.CodePrefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(code.AsSpan(CustomerTypes.CodePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static BusinessPartner Create(Guid org, string code, Parsed p, bool creditOn, int userId, DateTime now)
    {
        var type = p.Type ?? CustomerTypes.Company;
        var active = p.Active ?? true;
        return new BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = org, SupplierCode = code, SupplierName = p.Name!,
            PartnerType = PartnerCode.Of(PartnerType.Customer), IsVendor = false, IsCustomer = true, CustomerType = type,
            Email = p.Email, Phone = p.Phone, Mobile = p.Mobile, AddressLine1 = p.Address1, AddressLine2 = p.Address2,
            City = p.City, ProvinceState = p.State, PostalCode = p.Postal, Country = p.Country, TaxId = p.Tax,
            PaymentTermsDays = p.Terms ?? 0,
            CreditLimit = type == CustomerTypes.WalkIn ? 0m : creditOn ? p.Credit : null,
            Status = active ? "ACTIVE" : "INACTIVE", IsActive = active,
            CreatedBy = userId, CreatedDate = now
        };
    }

    /// <summary>IMP-EXC-04 — a null (blank) value leaves the stored one unchanged; the code itself is never changed.</summary>
    private static void ApplyUpdate(BusinessPartner x, Parsed p, bool creditOn, int userId, DateTime now)
    {
        if (!x.IsCustomer)
        {
            x.IsCustomer = true;
            x.PartnerType = TypeCodeOf(x.IsVendor, true, x.IsCarrier, x.IsServiceProvider)!;
        }
        if (p.Name is not null) x.SupplierName = p.Name;
        if (p.Email is not null) x.Email = p.Email;
        if (p.Phone is not null) x.Phone = p.Phone;
        if (p.Mobile is not null) x.Mobile = p.Mobile;
        if (p.Address1 is not null) x.AddressLine1 = p.Address1;
        if (p.Address2 is not null) x.AddressLine2 = p.Address2;
        if (p.City is not null) x.City = p.City;
        if (p.State is not null) x.ProvinceState = p.State;
        if (p.Postal is not null) x.PostalCode = p.Postal;
        if (p.Country is not null) x.Country = p.Country;
        if (p.Tax is not null) x.TaxId = p.Tax;
        if (p.Terms is { } terms) x.PaymentTermsDays = terms;
        x.CustomerType = p.Type ?? x.CustomerType ?? CustomerTypes.Company;
        if (x.CustomerType == CustomerTypes.WalkIn) x.CreditLimit = 0m;
        else if (creditOn && p.Credit is { } credit) x.CreditLimit = credit;
        if (p.Active is { } active && active != x.IsActive)
        {
            x.IsActive = active;
            // As CustomerService.SetStatusAsync: a vendor's Status belongs to the onboarding workflow.
            if (!x.IsVendor) x.Status = active ? "ACTIVE" : "INACTIVE";
            x.StatusChangedBy = userId;
            x.StatusChangedAt = now;
        }
        x.ModifiedBy = userId;
        x.ModifiedDate = now;
    }
}
