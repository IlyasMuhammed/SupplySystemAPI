using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A42 D-12 (FIN) — the A40 sub-ledger opening totals the GL setup wizard shows beside the default A/R, A/P and Inventory
/// accounts (information only, never blocks). Explicit organization, whoever the caller is; read-only.
/// <list type="bullet">
/// <item>Customer / supplier totals are the <b>original</b> amounts of the OPENING documents (D-40): what was owed at the
/// conversion date, which is what a GL opening balance as at that date must equal. Payments made since then reduce the
/// outstanding balance but not the opening balance.</item>
/// <item>Amounts are the A35 rate-locked base amounts (<c>BaseGrandTotal</c> / <c>BaseTotalAmount</c>); a document with no
/// stored base amount falls back to amount × its rate (1 when none).</item>
/// <item>Supplier bills are in the purchase base; when that differs from the sale base the total is converted at the
/// opening balance date (D-42). Without a rate it is left unconverted (residual, info only).</item>
/// <item>Opening stock value = Σ TotalCost of the Finance product ledger's OPENING_STOCK entries (D-43).</item>
/// </list>
/// </summary>
internal sealed class OpeningSubledgerTotalsService : IOpeningSubledgerTotals
{
    private readonly FinanceDbContext     _db;
    private readonly ICurrencyService?    _currency;
    private readonly IOrgCurrencyLookup?  _orgCurrencies;
    private readonly TimeProvider         _clock;

    public OpeningSubledgerTotalsService(
        FinanceDbContext db, ICurrencyService? currency = null, IOrgCurrencyLookup? orgCurrencies = null, TimeProvider? clock = null)
    {
        _db            = db;
        _currency      = currency;
        _orgCurrencies = orgCurrencies;
        _clock         = clock ?? TimeProvider.System;
    }

    public async Task<OpeningSubledgerTotals> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        var customer = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.SourceType == SalesInvoiceSources.Opening && !i.IsDelete
                     && i.Status != SalesInvoiceStatuses.Cancelled)
            .Select(i => new { Amount = i.BaseGrandTotal ?? i.GrandTotal * (i.ExchangeRate ?? 1m), i.BaseCurrencyCode })
            .ToListAsync(ct);

        var bills = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.SourceType == SalesInvoiceSources.Opening && !i.IsDelete
                     && i.MatchStatus != InvoiceMatchStatus.Reversed)
            .Select(i => new { Amount = i.BaseTotalAmount ?? i.TotalAmount * (i.ExchangeRate ?? 1m), i.BaseCurrencyId, i.BaseCurrencyCode })
            .ToListAsync(ct);

        var stock = await _db.ProductLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.EntryType == ProductLedgerEntryTypes.OpeningStock)
            .SumAsync(e => (decimal?)e.TotalCost, ct) ?? 0m;

        Guid? saleBase = _currency is null ? null : await _currency.GetBaseCurrencyIdAsync(organizationId, TransactionDomain.Sale, ct);
        var code = saleBase is { } id && _orgCurrencies is not null
            ? (await _orgCurrencies.GetAsync(organizationId, id, ct))?.Code
            : null;
        code ??= customer.Select(c => c.BaseCurrencyCode).FirstOrDefault(c => !string.IsNullOrEmpty(c))
              ?? bills.Select(b => b.BaseCurrencyCode).FirstOrDefault(c => !string.IsNullOrEmpty(c));

        var supplier = 0m;
        foreach (var group in bills.GroupBy(b => b.BaseCurrencyId))
        {
            var sum = group.Sum(b => b.Amount);
            if (group.Key is { } from && saleBase is { } to && from != to && sum != 0m)
                sum = await ToSaleBaseAsync(organizationId, sum, from, to, ct);
            supplier += sum;
        }

        return new OpeningSubledgerTotals(Round(customer.Sum(c => c.Amount)), Round(supplier), Round(stock), code);
    }

    private async Task<decimal> ToSaleBaseAsync(Guid org, decimal amount, Guid from, Guid to, CancellationToken ct)
    {
        var date = await _db.OrgSettings.IgnoreQueryFilters().AsNoTracking()
                       .Where(s => s.OrganizationId == org).Select(s => s.OpeningBalanceDate).FirstOrDefaultAsync(ct)
                   ?? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        try
        {
            return (await _currency!.ConvertAsync(org, amount, from, to, date, ct)).ConvertedAmount;
        }
        catch (CurrencyRateNotFoundException)
        {
            return amount; // Info only: no rate on file — shown unconverted rather than failing the wizard step.
        }
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
