using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A41 D-19 / D-20 — accounts (business partners) and contact persons for the CRM (<see cref="ICrmAccountGateway"/>).
/// Reads take the organization explicitly and ignore the tenant filter; new customers go through the A37 customer service
/// (code C-nnnnn, validation, QuickBooks publish). The walk-in customer is never offered or linked.
/// </summary>
internal sealed class CrmAccountGateway : ICrmAccountGateway
{
    private const int NameMax = 200, PhoneMax = 20, EmailMax = 150;

    private readonly SuppliersDbContext _db;
    private readonly ICustomerService _customers;
    private readonly PartnerQuickBooksPublisher? _quickBooks;

    public CrmAccountGateway(SuppliersDbContext db, ICustomerService customers, PartnerQuickBooksPublisher? quickBooks = null)
    {
        _db = db;
        _customers = customers;
        _quickBooks = quickBooks;
    }

    private IQueryable<BusinessPartner> Partners(Guid org) =>
        _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org && !p.IsDelete);

    private static CrmAccountInfo Info(BusinessPartner p) => new(
        p.UUID, p.SupplierCode, p.SupplierName, p.IsCustomer, p.IsVendor, p.IsActive, p.Phone ?? p.Mobile, p.Email, p.CustomerType, p.CreatedDate);

    public async Task<IReadOnlyDictionary<Guid, CrmAccountInfo>> GetAccountsAsync(Guid organizationId, IReadOnlyCollection<Guid> accountUuids,
        CancellationToken ct = default)
    {
        if (accountUuids.Count == 0) return new Dictionary<Guid, CrmAccountInfo>();
        var ids = accountUuids.Distinct().ToList();
        return (await Partners(organizationId).Where(p => ids.Contains(p.UUID)).ToListAsync(ct)).ToDictionary(p => p.UUID, Info);
    }

    public async Task<IReadOnlyList<CrmAccountInfo>> FindMatchesAsync(Guid organizationId, string? companyName, string? email, string? phone, int limit,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(companyName) ? null : companyName.Trim().ToLower();
        var mail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLower();
        var digits = phone is null ? string.Empty : new string(phone.Where(char.IsAsciiDigit).ToArray());
        var tail = digits.Length >= 7 ? digits[^7..] : null;
        if (name is null && mail is null && tail is null) return [];

        // §8.4 — name contains, or the same email / phone; exact name first.
        var rows = await Partners(organizationId).Where(p => !p.IsSystem
                && ((name != null && p.SupplierName.ToLower().Contains(name))
                    || (mail != null && p.Email != null && p.Email.ToLower() == mail)
                    || (tail != null && ((p.Phone != null && p.Phone.Contains(tail)) || (p.Mobile != null && p.Mobile.Contains(tail))))))
            .OrderBy(p => p.SupplierName.ToLower() == name ? 0 : 1).ThenBy(p => p.SupplierName)
            .Take(Math.Clamp(limit, 1, 50)).ToListAsync(ct);
        return rows.Select(Info).ToList();
    }

    public async Task<IReadOnlyList<CrmAccountInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default)
    {
        var q = Partners(organizationId).Where(p => p.IsActive && !p.IsSystem);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(p => p.SupplierName.ToLower().Contains(s) || p.SupplierCode.ToLower().Contains(s)
                || (p.Email != null && p.Email.ToLower().Contains(s)) || (p.Phone != null && p.Phone.Contains(s)) || (p.Mobile != null && p.Mobile.Contains(s)));
        }
        return (await q.OrderByDescending(p => p.IsCustomer).ThenBy(p => p.SupplierName).Take(Math.Clamp(limit, 1, 50)).ToListAsync(ct))
            .Select(Info).ToList();
    }

    public async Task<Guid> CreateCustomerAsync(Guid organizationId, CrmNewAccount account, int userId, CancellationToken ct = default)
    {
        if (organizationId != _db.TenantContext.OrganizationId)
            throw new BadRequestException("A customer can only be created in the caller's own organization.");
        var type = account.CustomerType is CustomerTypes.Individual ? CustomerTypes.Individual : CustomerTypes.Company;
        var uuid = await _customers.CreateAsync(new CustomerUpsert
        {
            Name = Fit(account.Name, NameMax) ?? throw new BadRequestException("The company name is required."),
            CustomerType = type,
            Phone = PhoneOrNull(account.Phone), Mobile = PhoneOrNull(account.Mobile),
            Email = account.Email is { Length: <= EmailMax } e ? e.Trim() : null,
            AddressLine1 = Fit(account.AddressLine1, 200), AddressLine2 = Fit(account.AddressLine2, 200), City = Fit(account.City, 100),
            ProvinceState = Fit(account.ProvinceState, 100), PostalCode = Fit(account.PostalCode, 20), Country = Fit(account.Country, 200),
            Notes = "Created from a CRM lead."
        }, userId, ct);

        if (!string.IsNullOrWhiteSpace(account.Website))
        {
            var p = await _db.BusinessPartners.IgnoreQueryFilters().FirstAsync(x => x.UUID == uuid && x.OrganizationId == organizationId, ct);
            p.Website = Fit(account.Website, 200);
            await _db.SaveChangesAsync(ct);
        }
        return uuid;
    }

    public async Task EnsureCustomerAsync(Guid organizationId, Guid accountUuid, int userId, CancellationToken ct = default)
    {
        var p = await _db.BusinessPartners.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.UUID == accountUuid && x.OrganizationId == organizationId && !x.IsDelete, ct)
                ?? throw new NotFoundException("Account not found.");
        if (p.IsCustomer) return;
        if (!p.IsActive) throw new BadRequestException($"{p.SupplierName} is inactive.");
        PartnerType type;
        try
        {
            type = PartnerCode.FromFlags(p.IsVendor, true, p.IsCarrier, p.IsServiceProvider);
        }
        catch (InvalidOperationException)
        {
            throw new BadRequestException($"{p.SupplierName} cannot also be a customer (its partner roles do not allow it).");
        }
        p.IsCustomer = true;
        p.PartnerType = PartnerCode.Of(type);
        p.CustomerType ??= CustomerTypes.Company;
        p.ModifiedBy = userId;
        p.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (_quickBooks is not null) await _quickBooks.PublishAsync(p.UUID);
    }

    public async Task<IReadOnlyList<CrmContactInfo>> ListContactsAsync(Guid organizationId, Guid accountUuid, CancellationToken ct = default)
    {
        var partnerId = await Partners(organizationId).Where(p => p.UUID == accountUuid).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        if (partnerId is null) return [];
        return await _db.SupplierContacts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.SupplierId == partnerId && c.IsActive)
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.ContactName)
            .Select(c => new CrmContactInfo(c.Id, accountUuid, c.ContactName, c.Title, c.Phone, c.Email, c.IsPrimary, c.IsActive))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, CrmContactInfo>> GetContactsAsync(Guid organizationId, IReadOnlyCollection<int> contactIds,
        CancellationToken ct = default)
    {
        if (contactIds.Count == 0) return new Dictionary<int, CrmContactInfo>();
        var ids = contactIds.Distinct().ToList();
        return await _db.SupplierContacts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && ids.Contains(c.Id))
            .Select(c => new CrmContactInfo(c.Id, c.Supplier.UUID, c.ContactName, c.Title, c.Phone, c.Email, c.IsPrimary, c.IsActive))
            .ToDictionaryAsync(c => c.Id, ct);
    }

    public async Task<int> CreateContactAsync(Guid organizationId, Guid accountUuid, CrmNewContact contact, CancellationToken ct = default)
    {
        var partner = await _db.BusinessPartners.IgnoreQueryFilters()
                          .FirstOrDefaultAsync(p => p.UUID == accountUuid && p.OrganizationId == organizationId && !p.IsDelete, ct)
                      ?? throw new NotFoundException("Account not found.");
        var hasContact = await _db.SupplierContacts.IgnoreQueryFilters()
            .AnyAsync(c => c.OrganizationId == organizationId && c.SupplierId == partner.Id && c.IsActive, ct);
        var row = new SupplierContact
        {
            OrganizationId = organizationId, SupplierId = partner.Id,
            ContactName = Fit(contact.Name, 200) ?? throw new BadRequestException("The contact's name is required."),
            Title = Fit(contact.Title, 100), Phone = PhoneOrNull(contact.Phone),
            Email = contact.Email is { Length: <= EmailMax } e ? e.Trim() : null,
            IsPrimary = !hasContact, IsActive = true
        };
        _db.SupplierContacts.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.Id;
    }

    private static string? Fit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length <= max ? v : v[..max];
    }

    /// <summary>The partner tables keep 20 characters of phone: spaces are dropped first; a number still longer stays on the lead only.</summary>
    private static string? PhoneOrNull(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var v = phone.Trim();
        if (v.Length > PhoneMax) v = v.Replace(" ", string.Empty);
        return v.Length <= PhoneMax ? v : null;
    }
}
