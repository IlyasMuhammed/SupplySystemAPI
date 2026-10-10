using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A37 D-13 — <see cref="ICustomerBalanceLookup"/> over Finance's sales invoices (read-only, the same direct read
/// <see cref="SupplierScoringService"/> makes of Finance's invoices). The customer ledger's running balance is not used:
/// it mixes currencies (A29). Each open invoice's <c>BalanceDue</c> is valued at the rate it locked at issue
/// (<c>ExchangeRate</c>, 1 when none) — so the figure is in the sale base the invoice was issued against.
/// Unapplied advances and credit notes not yet applied to an invoice are not netted.
/// </summary>
internal sealed class SalesInvoiceCustomerBalanceLookup : ICustomerBalanceLookup
{
    private static readonly string[] Open =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Overdue];

    private readonly FinanceDbContext _finance;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly IPartnerCurrencyCodes? _codes;
    private readonly TimeProvider _clock;

    public SalesInvoiceCustomerBalanceLookup(FinanceDbContext finance, IOrganizationCurrencyService? orgCurrency = null,
        IPartnerCurrencyCodes? codes = null, TimeProvider? clock = null)
    {
        _finance     = finance;
        _orgCurrency = orgCurrency;
        _codes       = codes;
        _clock       = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyDictionary<Guid, CustomerBalanceInfo>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> partnerIds, CancellationToken ct = default)
    {
        if (partnerIds.Count == 0) return new Dictionary<Guid, CustomerBalanceInfo>();

        var today = _clock.GetUtcNow().UtcDateTime.Date;
        var rows = await _finance.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && !i.IsDelete && partnerIds.Contains(i.PartnerId)
                     && Open.Contains(i.Status) && i.BalanceDue != 0m)
            .Select(i => new { i.PartnerId, i.BalanceDue, i.ExchangeRate, i.DueDate, i.BaseCurrencyCode })
            .ToListAsync(ct);

        var baseCode = await GetBaseCurrencyCodeAsync(organizationId, ct);
        return rows.GroupBy(r => r.PartnerId).ToDictionary(g => g.Key, g =>
        {
            decimal Base(decimal amount, decimal? rate) => Math.Round(amount * (rate ?? 1m), 2, MidpointRounding.AwayFromZero);
            return new CustomerBalanceInfo(
                g.Sum(r => Base(r.BalanceDue, r.ExchangeRate)),
                g.Where(r => r.DueDate.Date < today).Sum(r => Base(r.BalanceDue, r.ExchangeRate)),
                baseCode ?? g.Select(r => r.BaseCurrencyCode).FirstOrDefault(c => c != null));
        });
    }

    public async Task<string?> GetBaseCurrencyCodeAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_orgCurrency is null || _codes is null) return null;
        var id = await _orgCurrency.GetBaseCurrencyIdAsync(organizationId, TransactionDomain.Sale, ct);
        return id is { } currencyId && _codes.Load().TryGetValue(currencyId, out var code) ? code : null;
    }
}
