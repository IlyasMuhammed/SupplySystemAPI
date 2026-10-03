using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// SMS.Shared's <see cref="ITaxCodeLookup"/> over Finance's tax codes — how Demand (and anything else that
/// cannot reference Finance) picks and checks a code. Read-only; limited to the current organization
/// explicitly as well as through the tenant filter (a super admin bypasses the filter).
/// </summary>
internal sealed class TaxCodeLookup : ITaxCodeLookup
{
    private readonly FinanceDbContext _db;

    public TaxCodeLookup(FinanceDbContext db) => _db = db;

    private IQueryable<TaxCode> OwnCodes()
    {
        var org = _db.TenantContext.OrganizationId;
        return _db.TaxCodes.AsNoTracking().Where(t => t.OrganizationId == org);
    }

    public async Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        if (uuid == Guid.Empty) return null;

        var code = await OwnCodes().FirstOrDefaultAsync(t => t.Uuid == uuid, ct);
        return code is null ? null : ToInfo(code);
    }

    public async Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default)
    {
        var onSide = Side(side);

        var codes = await OwnCodes()
            .Where(t => t.IsActive && (t.Usage == onSide || t.Usage == TaxCodeUsages.Both))
            .ToListAsync(ct);

        return codes
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Code, StringComparer.Ordinal)
            .Select(ToInfo)
            .ToList();
    }

    public async Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default)
    {
        var onSide = Side(side);

        // TaxCodeService keeps it to one per side (its saves of an organization's codes run one at a time); the
        // ordering only keeps the answer stable should two ever exist anyway, e.g. rows written outside it.
        var code = await OwnCodes()
            .Where(t => t.IsActive && t.IsDefault && (t.Usage == onSide || t.Usage == TaxCodeUsages.Both))
            .OrderBy(t => t.Code)
            .FirstOrDefaultAsync(ct);

        return code is null ? null : ToInfo(code);
    }

    private static string Side(string side)
    {
        var normalized = side?.Trim().ToUpperInvariant();
        return normalized is TaxCodeUsage.Sales or TaxCodeUsage.Purchase
            ? normalized
            : throw new ArgumentException($"A tax code side is {TaxCodeUsage.Sales} or {TaxCodeUsage.Purchase}, not '{side}'.", nameof(side));
    }

    private static TaxCodeInfo ToInfo(TaxCode t) =>
        new(t.Uuid, t.Code, t.Name, t.RatePercent, t.Usage, t.IsDefault, t.IsActive);
}
