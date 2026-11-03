using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;

namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>
/// A48 AR D-AR-13 (QA-3, contract §3 "reset to 0 when the open balance is 0 … on profile read") — the level a read shows:
/// the stored level, or 0 when the customer has no open invoice left (the same test the preview's persisted auto-reset uses,
/// BR-DN-006). Reads never write: the stored level is reset (and audited) at the next dunning preview.
/// </summary>
internal static class EffectiveDunningLevel
{
    /// <summary>Of <paramref name="customers"/>, those with at least one open sales invoice (ISSUED / PARTIALLY_PAID / OVERDUE, BalanceDue &gt; 0).</summary>
    public static async Task<HashSet<Guid>> OwingAsync(FinanceDbContext db, Guid org, IReadOnlyCollection<Guid> customers, CancellationToken ct)
    {
        if (customers.Count == 0) return [];
        var owing = await db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsDelete && customers.Contains(i.PartnerId)
                     && CreditExposureReader.OpenInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0m)
            .Select(i => i.PartnerId).Distinct().ToListAsync(ct);
        return owing.ToHashSet();
    }

    public static int Of(int storedLevel, bool owing) => owing ? storedLevel : 0;
}
