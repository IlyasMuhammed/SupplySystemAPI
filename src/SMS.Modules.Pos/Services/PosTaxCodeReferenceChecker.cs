using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A50 REV50-1 (SEED-FIN) — POS keeps a Finance tax code next to its text snapshot: a store's default (DefaultTaxCode) and every
/// order (TaxCode). Answers Finance's <see cref="ITaxCodeReferenceChecker"/> so a tax code that POS holds is neither renamed
/// (TaxCodeService) nor removed by an onboarding TAX reset (TaxSetupSeeder). Explicit organization.
/// </summary>
internal sealed class PosTaxCodeReferenceChecker : ITaxCodeReferenceChecker
{
    private readonly PosDbContext _db;

    public PosTaxCodeReferenceChecker(PosDbContext db) => _db = db;

    public async Task<string?> FindCodeReferenceAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        var where = new List<string>();
        if (await _db.Stores.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == organizationId && s.DefaultTaxCode == c, ct))
            where.Add("the POS store settings");
        if (await _db.Orders.IgnoreQueryFilters().AnyAsync(o => o.OrganizationId == organizationId && o.TaxCode == c, ct))
            where.Add("POS orders");
        return where.Count == 0 ? null : string.Join(" and ", where);
    }
}
