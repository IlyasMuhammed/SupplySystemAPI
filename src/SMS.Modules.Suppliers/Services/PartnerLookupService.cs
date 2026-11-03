using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A48 D-3 — business partners for Finance's sub-ledgers (<see cref="IPartnerLookupService"/>). Scoped, so its cache lives for
/// one request: a report that names the same customer on many rows asks once, and <see cref="GetManyAsync"/> asks for all the
/// uncached uuids in one query per chunk. The organization is explicit (the tenant filter is bypassed for super admins and
/// absent in jobs); a deleted partner or another organization's is absent, never an exception.
/// </summary>
internal sealed class PartnerLookupService : IPartnerLookupService
{
    private const int Chunk = 1000;

    private readonly SuppliersDbContext  _db;
    private readonly IEncryptionService? _encryption;
    private readonly Dictionary<(Guid Org, Guid Uuid), PartnerInfo?> _cache = [];

    public PartnerLookupService(SuppliersDbContext db, IEncryptionService? encryption = null)
    {
        _db         = db;
        _encryption = encryption;
    }

    /// <summary>How many database round trips this instance made (tests: one per batch).</summary>
    internal int QueryCount { get; private set; }

    public async Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default)
    {
        var filter = RoleFilter(role);
        if (partnerUuid == Guid.Empty) return null;
        var found = await GetManyAsync(organizationId, [partnerUuid], null, ct);
        return found.TryGetValue(partnerUuid, out var p) && filter(p) ? p : null;
    }

    public async Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(
        Guid organizationId, IEnumerable<Guid> partnerUuids, string? role = null, CancellationToken ct = default)
    {
        var filter = RoleFilter(role);
        var wanted = partnerUuids.Where(u => u != Guid.Empty).Distinct().ToList();
        var missing = wanted.Where(u => !_cache.ContainsKey((organizationId, u))).ToList();

        foreach (var chunk in missing.Chunk(Chunk))
        {
            var ids = chunk.ToList();
            QueryCount++;
            var rows = await Partners(organizationId).Where(p => ids.Contains(p.UUID)).Select(Projection).ToListAsync(ct);
            var byUuid = rows.ToDictionary(r => r.Uuid);
            foreach (var id in ids) _cache[(organizationId, id)] = byUuid.TryGetValue(id, out var r) ? ToInfo(r) : null;
        }

        var result = new Dictionary<Guid, PartnerInfo>();
        foreach (var id in wanted)
            if (_cache.TryGetValue((organizationId, id), out var info) && info is not null && filter(info)) result[id] = info;
        return result;
    }

    public async Task<IReadOnlyList<PartnerInfo>> SearchAsync(
        Guid organizationId, string search, string? role = null, int limit = 200, CancellationToken ct = default)
    {
        RoleFilter(role);
        var text = search?.Trim() ?? string.Empty;
        var q = Partners(organizationId);
        if (role == PartnerRoles.Customer) q = q.Where(p => p.IsCustomer);
        else if (role == PartnerRoles.Vendor) q = q.Where(p => p.IsVendor);
        if (text.Length > 0) q = q.Where(p => p.SupplierName.Contains(text) || p.SupplierCode.Contains(text));

        QueryCount++;
        var rows = await q.OrderBy(p => p.SupplierName).Take(Math.Clamp(limit, 1, 5000)).Select(Projection).ToListAsync(ct);
        var infos = rows.Select(ToInfo).ToList();
        foreach (var i in infos) _cache[(organizationId, i.Uuid)] = i;
        return infos;
    }

    public async Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default)
    {
        if (partnerUuid == Guid.Empty) return null;
        QueryCount++;
        var bank = await Partners(organizationId).Where(p => p.UUID == partnerUuid && p.BankDetail != null)
            .Select(p => new { p.BankDetail!.BankName, p.BankDetail.BankAccountNo, p.BankDetail.BankIban, p.BankDetail.BankSwift })
            .FirstOrDefaultAsync(ct);
        if (bank is null) return null;
        return new PartnerBankInfo(partnerUuid, bank.BankName, Decrypt(bank.BankAccountNo), Decrypt(bank.BankIban), Decrypt(bank.BankSwift));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private IQueryable<BusinessPartner> Partners(Guid org) =>
        _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org && !p.IsDelete);

    /// <summary>The role predicate; anything but null / Customer / Vendor is a programming error (ArgumentException).</summary>
    private static Func<PartnerInfo, bool> RoleFilter(string? role) => role switch
    {
        null                  => _ => true,
        PartnerRoles.Customer => p => p.IsCustomer,
        PartnerRoles.Vendor   => p => p.IsVendor,
        _ => throw new ArgumentException($"Unknown partner role '{role}'. Allowed: Customer, Vendor.", nameof(role)),
    };

    private sealed record Row(
        Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive,
        string? Email, string? Phone, string? Mobile, string? Line1, string? Line2, string? City, string? Province, string? Postal,
        string? Country, string? ContactName, string? ContactEmail, decimal? CreditLimit, int PaymentTermsDays, string? CustomerType);

    private static readonly System.Linq.Expressions.Expression<Func<BusinessPartner, Row>> Projection = p => new Row(
        p.UUID, p.SupplierCode, p.SupplierName, p.IsCustomer, p.IsVendor, p.IsActive,
        p.Email, p.Phone, p.Mobile, p.AddressLine1, p.AddressLine2, p.City, p.ProvinceState, p.PostalCode, p.Country,
        p.PrimaryContactName, p.PrimaryContactEmail, p.CreditLimit, p.PaymentTermsDays, p.CustomerType);

    private static PartnerInfo ToInfo(Row r)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var address = string.Join(", ", new[] { r.Line1, r.Line2, r.City, r.Province, r.Postal, r.Country }.Select(Clean).Where(v => v is not null));
        return new PartnerInfo(
            r.Uuid, r.Code, r.Name, r.IsCustomer, r.IsVendor, r.IsActive,
            Clean(r.Email) ?? Clean(r.ContactEmail), Clean(r.Phone) ?? Clean(r.Mobile), address.Length == 0 ? null : address,
            Clean(r.ContactName), Clean(r.ContactEmail), r.CreditLimit, r.PaymentTermsDays, r.CustomerType);
    }

    private string? Decrypt(string? value)
    {
        if (string.IsNullOrEmpty(value) || _encryption is null) return null;
        try { return _encryption.Decrypt(value); }
        catch (Exception) { return null; } // a value that does not decrypt (wrong key, legacy plain text) is left out
    }
}
