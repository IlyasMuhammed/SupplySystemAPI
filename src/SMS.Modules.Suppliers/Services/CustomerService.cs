using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Services;

/// <summary>A37 D-13 — the customer master (API-CONTRACT §5/§6) over suppliers.BusinessPartners where IsCustomer.</summary>
public interface ICustomerService
{
    Task<PaginatedResponse<CustomerListItem>> ListAsync(CustomerListFilter filter, CancellationToken ct = default);
    Task<CustomerDetail?> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<Guid> CreateAsync(CustomerUpsert request, int userId, CancellationToken ct = default);
    Task UpdateAsync(Guid uuid, CustomerUpsert request, int userId, CancellationToken ct = default);
    Task SetStatusAsync(Guid uuid, bool isActive, int userId, CancellationToken ct = default);
    Task<List<CustomerListItem>> SearchAsync(string? q, int limit, CancellationToken ct = default);
    Task<CustomerBalanceModel?> GetBalanceAsync(Guid uuid, CancellationToken ct = default);
    Task<CustomerSyncResponse> SyncAsync(DateTime? since, int limit, CancellationToken ct = default);
}

internal sealed class CustomerService : ICustomerService
{
    private const int MaxPageSize = 100, MaxSearch = 50, MaxSync = 1000, CodeAttempts = 3;

    private readonly SuppliersDbContext _db;
    private readonly WalkInCustomerSeeder _walkIn;
    private readonly ICustomerBalanceLookup? _balances;
    private readonly IModuleGate? _gate;
    private readonly PartnerQuickBooksPublisher? _quickBooks;
    private readonly PartnerCurrencyRules _currencyRules;

    /// <param name="gate">Optional: without the registry FEATURE_CREDIT_MANAGEMENT counts as enabled.</param>
    public CustomerService(SuppliersDbContext db, WalkInCustomerSeeder walkIn, ICustomerBalanceLookup? balances = null,
        IModuleGate? gate = null, PartnerQuickBooksPublisher? quickBooks = null, PartnerCurrencyRules? currencyRules = null)
    {
        _db = db;
        _walkIn = walkIn;
        _balances = balances;
        _gate = gate;
        _quickBooks = quickBooks;
        _currencyRules = currencyRules ?? new PartnerCurrencyRules();
    }

    private Guid OrgId => _db.TenantContext.OrganizationId;

    /// <summary>The caller's organization's customers — explicit org, because a super admin bypasses the tenant filter.</summary>
    private IQueryable<BusinessPartner> Customers() =>
        _db.BusinessPartners.Where(p => p.OrganizationId == OrgId && p.IsCustomer && !p.IsDelete);

    // ── Reads ──────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<CustomerListItem>> ListAsync(CustomerListFilter filter, CancellationToken ct = default)
    {
        await _walkIn.EnsureAsync(OrgId, ct);
        var query = Customers().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(p => p.SupplierName.ToLower().Contains(term) || p.SupplierCode.ToLower().Contains(term)
                || (p.Phone != null && p.Phone.Contains(term)) || (p.Mobile != null && p.Mobile.Contains(term))
                || (p.Email != null && p.Email.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var type = filter.Type.Trim().ToUpperInvariant();
            query = query.Where(p => p.CustomerType == type);
        }
        var status = filter.Status?.Trim().ToUpperInvariant();
        if (status == "ACTIVE") query = query.Where(p => p.IsActive);
        else if (status == "INACTIVE") query = query.Where(p => !p.IsActive);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var desc = filter.SortOrder?.Trim().ToLowerInvariant() is "desc" or "-1" or "descend" or "descending";
        var field = filter.SortField?.Trim().ToLowerInvariant();

        List<BusinessPartner> rows;
        IReadOnlyDictionary<Guid, CustomerBalanceInfo> balances;
        if (field == "balance")
        {
            // Balances live in Finance: rank every match in memory, then load the page.
            var ids = await query.Select(p => new { p.UUID, p.SupplierName }).ToListAsync(ct);
            balances = await BalancesAsync(ids.Select(i => i.UUID).ToList(), ct);
            decimal Bal(Guid id) => balances.TryGetValue(id, out var b) ? b.Balance : 0m;
            var ordered = desc ? ids.OrderByDescending(i => Bal(i.UUID)) : ids.OrderBy(i => Bal(i.UUID));
            var pageIds = ordered.ThenBy(i => i.SupplierName).Skip((page - 1) * pageSize).Take(pageSize).Select(i => i.UUID).ToList();
            var loaded = await query.Where(p => pageIds.Contains(p.UUID)).ToListAsync(ct);
            rows = pageIds.Select(id => loaded.First(p => p.UUID == id)).ToList();
        }
        else
        {
            rows = await Sort(query, field, desc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            balances = await BalancesAsync(rows.Select(r => r.UUID).ToList(), ct);
        }

        var baseCode = await BaseCodeAsync(ct);
        return new PaginatedResponse<CustomerListItem>
        {
            Data = rows.Select(p => Fill(new CustomerListItem(), p, balances, baseCode)).ToList(),
            TotalRecords = total, Page = page, PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    private static IQueryable<BusinessPartner> Sort(IQueryable<BusinessPartner> q, string? field, bool desc) => field switch
    {
        "code"         => desc ? q.OrderByDescending(p => p.SupplierCode) : q.OrderBy(p => p.SupplierCode),
        "customertype" => (desc ? q.OrderByDescending(p => p.CustomerType) : q.OrderBy(p => p.CustomerType)).ThenBy(p => p.SupplierName),
        "creditlimit"  => (desc ? q.OrderByDescending(p => p.CreditLimit ?? 0m) : q.OrderBy(p => p.CreditLimit ?? 0m)).ThenBy(p => p.SupplierName),
        "status"       => (desc ? q.OrderByDescending(p => p.IsActive) : q.OrderBy(p => p.IsActive)).ThenBy(p => p.SupplierName),
        _              => (desc ? q.OrderByDescending(p => p.SupplierName) : q.OrderBy(p => p.SupplierName)).ThenBy(p => p.SupplierCode)
    };

    public async Task<CustomerDetail?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var p = await Customers().AsNoTracking().FirstOrDefaultAsync(x => x.UUID == uuid, ct);
        if (p is null) return null;
        var balances = await BalancesAsync([p.UUID], ct);
        var d = Fill(new CustomerDetail(), p, balances, await BaseCodeAsync(ct));
        d.TaxId = p.TaxId; d.AddressLine1 = p.AddressLine1; d.AddressLine2 = p.AddressLine2; d.City = p.City;
        d.ProvinceState = p.ProvinceState; d.PostalCode = p.PostalCode; d.Country = p.Country;
        d.PaymentTermsDays = p.PaymentTermsDays; d.DefaultSaleCurrencyId = p.DefaultSaleCurrency; d.Notes = p.Notes;
        d.IsVendor = p.IsVendor; d.CreatedAt = p.CreatedDate; d.ModifiedAt = p.ModifiedAt;
        return d;
    }

    /// <summary>Active customers only: exact code/phone/mobile first, then name/code starting with q, then the rest; by name.</summary>
    public async Task<List<CustomerListItem>> SearchAsync(string? q, int limit, CancellationToken ct = default)
    {
        await _walkIn.EnsureAsync(OrgId, ct);
        limit = Math.Clamp(limit, 1, MaxSearch);
        var query = Customers().AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query
                .Where(p => p.SupplierName.ToLower().Contains(t) || p.SupplierCode.ToLower().Contains(t)
                    || (p.Phone != null && p.Phone.Contains(t)) || (p.Mobile != null && p.Mobile.Contains(t))
                    || (p.Email != null && p.Email.ToLower().Contains(t)))
                .OrderBy(p => p.SupplierCode.ToLower() == t || p.Phone == t || p.Mobile == t ? 0
                    : p.SupplierName.ToLower().StartsWith(t) || p.SupplierCode.ToLower().StartsWith(t) ? 1 : 2)
                .ThenBy(p => p.SupplierName);
        }
        else query = query.OrderByDescending(p => p.IsSystem).ThenBy(p => p.SupplierName);

        var rows = await query.Take(limit).ToListAsync(ct);
        var balances = await BalancesAsync(rows.Select(r => r.UUID).ToList(), ct);
        var baseCode = await BaseCodeAsync(ct);
        return rows.Select(p => Fill(new CustomerListItem(), p, balances, baseCode)).ToList();
    }

    public async Task<CustomerBalanceModel?> GetBalanceAsync(Guid uuid, CancellationToken ct = default)
    {
        if (!await Customers().AnyAsync(p => p.UUID == uuid, ct)) return null;
        var balances = await BalancesAsync([uuid], ct);
        return balances.TryGetValue(uuid, out var b)
            ? new CustomerBalanceModel(b.Balance, b.CurrencyCode ?? await BaseCodeAsync(ct), b.Overdue)
            : new CustomerBalanceModel(0m, await BaseCodeAsync(ct), 0m);
    }

    /// <summary>
    /// D-16 — customers changed strictly after <paramref name="since"/>, oldest change first. Deactivated and deleted
    /// customers are included (isActive false) so an offline copy drops them. A page never ends inside a run of equal
    /// ModifiedAt values (it is extended to the end of the run), so "since = last item's modifiedAt" never skips a row.
    /// </summary>
    public async Task<CustomerSyncResponse> SyncAsync(DateTime? since, int limit, CancellationToken ct = default)
    {
        await _walkIn.EnsureAsync(OrgId, ct);
        var serverTime = DateTime.UtcNow;
        limit = Math.Clamp(limit, 1, MaxSync);
        var query = _db.BusinessPartners.AsNoTracking().Where(p => p.OrganizationId == OrgId && p.IsCustomer);
        if (since is { } s)
        {
            var utc = s.Kind == DateTimeKind.Local ? s.ToUniversalTime() : DateTime.SpecifyKind(s, DateTimeKind.Utc);
            query = query.Where(p => p.ModifiedAt > utc);
        }

        var rows = await query.OrderBy(p => p.ModifiedAt).ThenBy(p => p.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            rows.AddRange(await query.Where(p => p.ModifiedAt == last.ModifiedAt && p.Id > last.Id).OrderBy(p => p.Id).ToListAsync(ct));
        }

        var balances = await BalancesAsync(rows.Select(r => r.UUID).ToList(), ct);
        var baseCode = await BaseCodeAsync(ct);
        return new CustomerSyncResponse
        {
            ServerTime = serverTime, HasMore = hasMore,
            Customers = rows.Select(p =>
            {
                var item = Fill(new CustomerSyncItem(), p, balances, baseCode);
                item.ModifiedAt = DateTime.SpecifyKind(p.ModifiedAt, DateTimeKind.Utc);
                if (p.IsDelete) item.IsActive = false;
                return item;
            }).ToList()
        };
    }

    // ── Writes ─────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CustomerUpsert request, int userId, CancellationToken ct = default)
    {
        var type = ValidateCommon(request) ?? CustomerTypes.Company;
        await _currencyRules.ValidateAsync(request.DefaultSaleCurrencyId, false, null, false, ct: ct);
        var credit = await ResolveCreditAsync(type, request.CreditLimit, current: null, ct);

        for (var attempt = 1; ; attempt++)
        {
            var p = new BusinessPartner
            {
                UUID = Guid.NewGuid(), OrganizationId = OrgId, SupplierCode = await NextCodeAsync(ct),
                PartnerType = PartnerCode.Of(PartnerType.Customer), IsVendor = false, IsCustomer = true,
                Status = "ACTIVE", IsActive = true, CreatedBy = userId, CreatedDate = DateTime.UtcNow,
                CreditLimit = credit
            };
            Apply(p, request, type);
            _db.BusinessPartners.Add(p);
            try
            {
                await _db.SaveChangesAsync(ct);
                await PublishAsync(p.UUID);
                return p.UUID;
            }
            catch (DbUpdateException) when (attempt < CodeAttempts)
            {
                // Two creates drew the same next code; the unique (OrganizationId, code) index refused one. Draw again.
                _db.Entry(p).State = EntityState.Detached;
            }
        }
    }

    public async Task UpdateAsync(Guid uuid, CustomerUpsert request, int userId, CancellationToken ct = default)
    {
        var p = await Customers().FirstOrDefaultAsync(x => x.UUID == uuid, ct) ?? throw new NotFoundException("Customer", uuid);
        var type = ValidateCommon(request) ?? p.CustomerType ?? CustomerTypes.Company;
        if (p.IsSystem && type != CustomerTypes.WalkIn) throw new BadRequestException(CustomerTypes.MsgWalkInRetype);
        await _currencyRules.ValidateAsync(request.DefaultSaleCurrencyId, false, null, false, p.DefaultSaleCurrency, ct: ct);

        p.CreditLimit = await ResolveCreditAsync(type, request.CreditLimit, p.CreditLimit, ct);
        Apply(p, request, type);
        p.ModifiedBy = userId;
        p.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await PublishAsync(p.UUID);
    }

    public async Task SetStatusAsync(Guid uuid, bool isActive, int userId, CancellationToken ct = default)
    {
        var p = await Customers().FirstOrDefaultAsync(x => x.UUID == uuid, ct) ?? throw new NotFoundException("Customer", uuid);
        if (p.IsSystem && !isActive) throw new BadRequestException(CustomerTypes.MsgWalkInDeactivate);
        if (p.IsActive == isActive) return;

        p.IsActive = isActive;
        // A vendor's Status belongs to the supplier onboarding workflow; a pure customer's mirrors IsActive.
        if (!p.IsVendor) p.Status = isActive ? "ACTIVE" : "INACTIVE";
        p.StatusChangedBy = userId;
        p.StatusChangedAt = DateTime.UtcNow;
        p.ModifiedBy = userId;
        p.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await PublishAsync(p.UUID);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <returns>The requested customer type (upper-cased), or null when none was sent.</returns>
    private static string? ValidateCommon(CustomerUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new BadRequestException("Name is required.");
        MaxLength("Name", r.Name, 200); MaxLength("Phone", r.Phone, 20); MaxLength("Mobile", r.Mobile, 20);
        MaxLength("Email", r.Email, 150); MaxLength("Tax ID", r.TaxId, 30); MaxLength("Address line 1", r.AddressLine1, 200);
        MaxLength("Address line 2", r.AddressLine2, 200); MaxLength("City", r.City, 100);
        MaxLength("Province/state", r.ProvinceState, 100); MaxLength("Postal code", r.PostalCode, 20);
        MaxLength("Country", r.Country, 200); MaxLength("Notes", r.Notes, 500);
        if (!string.IsNullOrWhiteSpace(r.Email) && !r.Email.Contains('@')) throw new BadRequestException("Email is not a valid address.");
        if (r.PaymentTermsDays is < 0 or > 3650) throw new BadRequestException("Payment terms must be between 0 and 3650 days.");
        if (r.CreditLimit is < 0m) throw new BadRequestException("Credit limit cannot be negative.");
        if (string.IsNullOrWhiteSpace(r.CustomerType)) return null;
        var type = r.CustomerType.Trim().ToUpperInvariant();
        return CustomerTypes.All.Contains(type) ? type
            : throw new BadRequestException($"Customer type must be one of {string.Join(", ", CustomerTypes.All)}.");
    }

    private static void MaxLength(string field, string? value, int max)
    {
        if (value is not null && value.Trim().Length > max) throw new BadRequestException($"{field} must be at most {max} characters.");
    }

    /// <summary>
    /// CUST-04 — a walk-in customer's limit is 0 (a request for more is refused, an existing one is cleared). Otherwise the
    /// limit is written only when FEATURE_CREDIT_MANAGEMENT is on; without it the request's value is ignored (contract §5).
    /// </summary>
    private async Task<decimal?> ResolveCreditAsync(string type, decimal? requested, decimal? current, CancellationToken ct)
    {
        if (type == CustomerTypes.WalkIn)
            return requested is > 0m ? throw new BadRequestException(CustomerTypes.MsgWalkInCredit) : 0m;
        return await IsCreditManagementEnabledAsync(ct) ? requested : current;
    }

    internal async Task<bool> IsCreditManagementEnabledAsync(CancellationToken ct) =>
        _gate is null || await _gate.IsEnabledAsync(OrgId, ModuleCodes.CreditManagement, ct);

    private static void Apply(BusinessPartner p, CustomerUpsert r, string type)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        p.SupplierName = r.Name!.Trim();
        p.CustomerType = type;
        p.Phone = Clean(r.Phone); p.Mobile = Clean(r.Mobile); p.Email = Clean(r.Email); p.TaxId = Clean(r.TaxId);
        p.AddressLine1 = Clean(r.AddressLine1); p.AddressLine2 = Clean(r.AddressLine2); p.City = Clean(r.City);
        p.ProvinceState = Clean(r.ProvinceState); p.PostalCode = Clean(r.PostalCode); p.Country = Clean(r.Country);
        p.Notes = Clean(r.Notes);
        p.PaymentTermsDays = r.PaymentTermsDays ?? 0;
        p.DefaultSaleCurrency = r.DefaultSaleCurrencyId;
    }

    /// <summary>CUST-02 — C-00001, C-00002… per organization: one past the highest C-nnnnn code it has ever used (deleted rows included).</summary>
    private async Task<string> NextCodeAsync(CancellationToken ct)
    {
        var codes = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == OrgId && p.SupplierCode.StartsWith(CustomerTypes.CodePrefix))
            .Select(p => p.SupplierCode).ToListAsync(ct);
        var max = codes.Select(c => int.TryParse(c.AsSpan(CustomerTypes.CodePrefix.Length), System.Globalization.NumberStyles.None, null, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{CustomerTypes.CodePrefix}{max + 1:D5}";
    }

    private async Task<IReadOnlyDictionary<Guid, CustomerBalanceInfo>> BalancesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        _balances is null || ids.Count == 0 ? new Dictionary<Guid, CustomerBalanceInfo>() : await _balances.GetAsync(OrgId, ids, ct);

    private async Task<string?> BaseCodeAsync(CancellationToken ct) =>
        _balances is null ? null : await _balances.GetBaseCurrencyCodeAsync(OrgId, ct);

    private static T Fill<T>(T item, BusinessPartner p, IReadOnlyDictionary<Guid, CustomerBalanceInfo> balances, string? baseCode)
        where T : CustomerListItem
    {
        item.Uuid = p.UUID; item.Code = p.SupplierCode; item.Name = p.SupplierName;
        item.CustomerType = p.CustomerType ?? CustomerTypes.Company;
        item.Phone = p.Phone; item.Mobile = p.Mobile; item.Email = p.Email;
        item.CreditLimit = p.CreditLimit ?? 0m;
        item.Balance = balances.TryGetValue(p.UUID, out var b) ? b.Balance : 0m;
        item.CurrencyCode = b?.CurrencyCode ?? baseCode;
        item.IsActive = p.IsActive; item.IsSystem = p.IsSystem;
        return item;
    }

    private Task PublishAsync(Guid uuid) => _quickBooks is null ? Task.CompletedTask : _quickBooks.PublishAsync(uuid);
}
