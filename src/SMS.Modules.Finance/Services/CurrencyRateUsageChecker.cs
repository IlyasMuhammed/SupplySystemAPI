using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 D-2 / D-8 — "is the rate currency used?": any non-SYSTEM rate is expressed against it, so changing the rate currency
/// is refused once one exists. Documents are answered by Demand's and Finance's document checkers (FIN), not here.
/// </summary>
internal sealed class CurrencyRateUsageChecker : ICurrencyUsageChecker
{
    private readonly FinanceDbContext _db;
    public CurrencyRateUsageChecker(FinanceDbContext db) => _db = db;

    public Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public async Task<string?> DescribeRateCurrencyUsageAsync(Guid organizationId, CancellationToken ct = default)
    {
        var count = await _db.CurrencyRates.IgnoreQueryFilters()
            .CountAsync(r => r.OrganizationId == organizationId && r.Source != CurrencyConventions.SourceSystem, ct);
        return count == 0 ? null : count == 1 ? "1 exchange rate" : $"{count} exchange rates";
    }
}
