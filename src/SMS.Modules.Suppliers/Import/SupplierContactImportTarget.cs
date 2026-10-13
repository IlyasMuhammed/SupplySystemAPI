using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Suppliers.Import.PartnerImportSupport;

namespace SMS.Modules.Suppliers.Import;

/// <summary>
/// A40 (TSUP) — SUPPLIER_CONTACT import target over suppliers.SupplierContacts. The supplier (supplier_code) must already
/// exist as a vendor of the organization — import suppliers first (LOOKUP_NOT_FOUND otherwise). Matching: supplier +
/// email (case-insensitive, active contacts). A row with is_primary true makes that contact the supplier's only primary.
/// SupplierContacts has an int key and no UUID or audit columns, so the entity reference is a stable synthetic Guid that
/// encodes the contact id (<see cref="ToUuid"/> / <see cref="TryFromUuid"/>) — no schema change.
/// </summary>
internal sealed class SupplierContactImportTarget : IImportTarget
{
    private readonly SuppliersDbContext _db;

    public SupplierContactImportTarget(SuppliersDbContext db) => _db = db;

    // ── Synthetic entity reference ─────────────────────────────────────────────

    private static readonly byte[] Marker = "SUPCONT1"u8.ToArray();

    internal static Guid ToUuid(int contactId) => new(contactId, 0x5C0A, 0x0001, Marker);

    internal static bool TryFromUuid(Guid uuid, out int contactId)
    {
        var bytes = uuid.ToByteArray();
        contactId = BitConverter.ToInt32(bytes, 0);
        return BitConverter.ToInt16(bytes, 4) == 0x5C0A && BitConverter.ToInt16(bytes, 6) == 0x0001
            && bytes.AsSpan(8).SequenceEqual(Marker) && contactId > 0;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.SupplierContact, "Supplier Contacts",
        "Contact persons of suppliers already in the system (import suppliers first).",
        ImportOrder: 9, IsOpeningBalance: false, MatchFields: ["supplier_code", "email"],
        Fields:
        [
            new("supplier_code", "Supplier Code", "Code of an existing supplier.", ImportFieldTypes.Lookup, true, true, 1, "SUP-001",
                MaxLength: CodeMax, LookupList: "suppliers", Aliases: ["supplier", "vendor_code", "vendor", "CardCode", "LIFNR"]),
            new("name", "Contact Name", null, ImportFieldTypes.Text, true, false, 2, "Sara Khan", MaxLength: NameMax,
                Aliases: ["contact_name", "contact name", "contact", "person", "Name"]),
            new("title", "Title", null, ImportFieldTypes.Text, false, false, 3, "Sales Manager", MaxLength: TitleMax,
                Aliases: ["job_title", "designation", "position", "Position"]),
            new("email", "Email", "With the supplier code, matches an existing contact.", ImportFieldTypes.Email, false, true, 4, "sara@globalparts.example",
                MaxLength: EmailMax, Aliases: ["e-mail", "email_address", "E_Mail"]),
            new("phone", "Phone", null, ImportFieldTypes.Phone, false, false, 5, "+92 300 7654321", MaxLength: PhoneMax,
                Aliases: ["telephone", "phone_number", "mobile", "Tel1"]),
            new("is_primary", "Primary Contact", null, ImportFieldTypes.Boolean, false, false, 6, "false", DefaultValue: "false",
                Aliases: ["primary", "main_contact"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
    [
        new("suppliers", await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.IsVendor && !p.IsDelete && p.IsActive)
            .OrderBy(p => p.SupplierCode).Select(p => p.SupplierCode).Take(1000).ToListAsync(ct))
    ];

    // ── Reading a row ──────────────────────────────────────────────────────────

    private sealed record Parsed(string? SupplierCode, string? Name, string? Title, string? Email, string? Phone, bool? IsPrimary,
        List<ImportFieldError> Errors);

    private static Parsed Parse(IReadOnlyDictionary<string, string?> values)
    {
        var r = new RowReader(values);
        return new Parsed(r.Text("supplier_code", "Supplier code", CodeMax, required: true), r.Text("name", "Contact name", NameMax),
            r.Text("title", "Title", TitleMax), r.Email("email", "Email"), r.Text("phone", "Phone", PhoneMax), r.Bool("is_primary", "Primary contact"),
            r.Errors);
    }

    private sealed record Supplier(int Id, string Code);

    private async Task<Dictionary<string, Supplier>> SuppliersAsync(Guid org, IEnumerable<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct)
    {
        var codes = rows.Select(v => Get(v, "supplier_code")).OfType<string>().Select(Norm).Distinct().ToList();
        var found = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.IsVendor && !p.IsDelete && codes.Contains(p.SupplierCode.ToUpper()))
            .Select(p => new Supplier(p.Id, p.SupplierCode)).ToListAsync(ct);
        var map = new Dictionary<string, Supplier>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in found) map.TryAdd(s.Code.Trim(), s);
        return map;
    }

    private static Supplier? ResolveSupplier(Parsed p, IReadOnlyDictionary<string, Supplier> suppliers)
    {
        if (p.SupplierCode is null) return null;
        if (suppliers.TryGetValue(p.SupplierCode, out var s)) return s;
        p.Errors.Add(new ImportFieldError("supplier_code", LookupNotFound,
            $"Supplier {p.SupplierCode} does not exist. Import suppliers before their contacts.", p.SupplierCode));
        return null;
    }

    // ── IImportTarget ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        var suppliers = await SuppliersAsync(organizationId, rows.Select(r => r.Values), ct);
        var supplierIds = suppliers.Values.Select(s => s.Id).ToList();
        var contacts = await _db.SupplierContacts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.IsActive && supplierIds.Contains(c.SupplierId))
            .Select(c => new { c.Id, c.SupplierId, c.Email }).ToListAsync(ct);

        var result = new List<ImportRowCheck>(rows.Count);
        foreach (var row in rows)
        {
            var p = Parse(row.Values);
            var supplier = ResolveSupplier(p, suppliers);
            Guid? match = null;
            if (supplier is not null && row.ExistingEntityUuid is { } given && TryFromUuid(given, out var refId)
                && contacts.Any(c => c.Id == refId && c.SupplierId == supplier.Id))
                match = given;
            else if (supplier is not null && p.Email is not null)
            {
                var hits = contacts.Where(c => c.SupplierId == supplier.Id && c.Email != null
                    && string.Equals(c.Email.Trim(), p.Email, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 1)
                    p.Errors.Add(new ImportFieldError("email", AmbiguousMatch, $"{hits.Count} contacts of supplier {supplier.Code} have this email.", p.Email));
                else if (hits.Count == 1) match = ToUuid(hits[0].Id);
            }
            if (match is null && p.Name is null && p.Errors.All(e => e.Field != "name"))
                p.Errors.Add(new ImportFieldError("name", Required, "Contact name is required.", null));
            result.Add(new ImportRowCheck(row.RowNumber, match, p.Errors));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        // SupplierContacts has no created/modified-by columns: userId is not stored for contacts.
        var suppliers = await SuppliersAsync(organizationId, rows.Select(r => r.Values), ct);
        var supplierIds = suppliers.Values.Select(s => s.Id).ToList();
        var contacts = await _db.SupplierContacts.IgnoreQueryFilters()
            .Where(c => c.OrganizationId == organizationId && c.IsActive && supplierIds.Contains(c.SupplierId))
            .ToListAsync(ct);

        var outcomes = new List<(int Row, string Status, SupplierContact Contact)>(rows.Count);
        try
        {
            foreach (var row in rows)
            {
                var p = Parse(row.Values);
                var supplier = ResolveSupplier(p, suppliers);
                SupplierContact contact;
                if (row.Action == ActionUpdate)
                {
                    ThrowIfAny(row.RowNumber, p.Errors);
                    if (row.ExistingEntityUuid is not { } id || !TryFromUuid(id, out var contactId))
                        throw new BadRequestException($"Row {row.RowNumber}: an update needs the existing contact.");
                    contact = contacts.FirstOrDefault(c => c.Id == contactId && c.SupplierId == supplier!.Id)
                        ?? throw new BadRequestException($"Row {row.RowNumber}: the contact to update no longer exists for supplier {p.SupplierCode}.");
                    if (p.Name is not null) contact.ContactName = p.Name;
                    if (p.Title is not null) contact.Title = p.Title;
                    if (p.Email is not null) contact.Email = p.Email;
                    if (p.Phone is not null) contact.Phone = p.Phone;
                    if (p.IsPrimary is { } primary) contact.IsPrimary = primary;
                    outcomes.Add((row.RowNumber, StatusUpdated, contact));
                }
                else
                {
                    if (p.Name is null && p.Errors.All(e => e.Field != "name"))
                        p.Errors.Add(new ImportFieldError("name", Required, "Contact name is required.", null));
                    ThrowIfAny(row.RowNumber, p.Errors);
                    contact = new SupplierContact
                    {
                        SupplierId = supplier!.Id, OrganizationId = organizationId, ContactName = p.Name!, Title = p.Title,
                        Email = p.Email, Phone = p.Phone, IsPrimary = p.IsPrimary ?? false, IsActive = true
                    };
                    _db.SupplierContacts.Add(contact);
                    contacts.Add(contact);
                    outcomes.Add((row.RowNumber, StatusImported, contact));
                }
                if (p.IsPrimary == true)
                    foreach (var other in contacts.Where(c => c.SupplierId == contact.SupplierId && !ReferenceEquals(c, contact)))
                        other.IsPrimary = false;
            }
            await _db.SaveChangesAsync(ct);   // one SaveChanges = one transaction: the whole batch or nothing
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
        return outcomes.Select(o => new ImportRowOutcome(o.Row, o.Status, ToUuid(o.Contact.Id))).ToList();
    }
}
