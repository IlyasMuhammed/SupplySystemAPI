using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A35 D-11 / REV-01 — locks the rate of LEGACY locked documents the migration could not: confirmed sale orders, sent
/// quotations and approved+ purchase orders whose ExchangeRate is still null (in practice: a currency other than the
/// domain base — the migration already set rate 1 on the rest). It needs Finance's rates, which exist only after Finance
/// migrates AND CUR's startup bootstrap has seeded them; so it is an <see cref="ICurrencyRatesReadyParticipant"/>: Finance's
/// CurrencyBootstrapper calls it per organization, after that bootstrap, under its per-organization app lock (API start and
/// new-organization provisioning).
/// <para>
/// Idempotent and conservative: only this organization's rows whose rate is still null; the rate at the document's lock
/// date (SO: OrderDate, SQ: SentAt, PO: ModifiedDate ?? CreatedDate) through Finance's <see cref="ICurrencyService"/>
/// (explicit-org overloads — no request, no tenant filter); no rate on file → left null and logged (no guessing). A later
/// start retries them (a historical rate added since then is "the rate at the lock date" too).
/// </para>
/// </summary>
internal sealed class DemandCurrencyBackfill(
    DemandDbContext db, ICurrencyService? currency = null, ILogger<DemandCurrencyBackfill>? log = null) : ICurrencyRatesReadyParticipant
{
    public async Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (currency is null) return;
        await RunAsync(db, currency, organizationId, (ILogger?)log ?? NullLogger.Instance, ct);
    }

    /// <summary>The backfill for one organization (internal for the tests). Returns (locked, left without a rate).</summary>
    internal static async Task<(int Locked, int Missing)> RunAsync(
        DemandDbContext db, ICurrencyService currency, Guid org, ILogger log, CancellationToken ct = default)
    {
        int locked = 0, missing = 0;
        var poStatuses = DemandCurrencyUsageChecker.LockedPoStatuses;

        var orders = await db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
            .Where(o => o.OrganizationId == org && o.ExchangeRate == null && o.Status != "DRAFT" && o.Status != "CANCELLED")
            .ToListAsync(ct);
        foreach (var o in orders)
            if (await TryLockAsync(currency, org, o.CurrencyId, o.OrderDate, TransactionDomain.Sale, log, $"sale order {o.SoNumber}", ct) is { } rate)
            { DemandCurrency.Apply(o, rate, o.OrderDate); locked++; }
            else missing++;

        var quotations = await db.SaleQuotations.IgnoreQueryFilters().Include(q => q.Lines)
            .Where(q => q.OrganizationId == org && q.ExchangeRate == null && q.SentAt != null)
            .ToListAsync(ct);
        foreach (var q in quotations)
            if (await TryLockAsync(currency, org, q.CurrencyId, q.SentAt!.Value, TransactionDomain.Sale, log, $"quotation {q.QuotationNumber}", ct) is { } rate)
            { DemandCurrency.Apply(q, rate, q.SentAt!.Value); locked++; }
            else missing++;

        var pos = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .Where(p => p.OrganizationId == org && p.ExchangeRate == null && !p.IsDelete && poStatuses.Contains(p.Status))
            .ToListAsync(ct);
        foreach (var p in pos)
        {
            var when = p.ModifiedDate ?? p.CreatedDate;
            var currencyId = p.CurrencyId ?? await currency.GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase, ct);
            if (await TryLockAsync(currency, org, currencyId, when, TransactionDomain.Purchase, log, $"purchase order {p.PoNumber}", ct) is { } rate)
            { DemandCurrency.Apply(p, rate, when); locked++; }
            else missing++;
        }

        if (locked > 0) await db.SaveChangesAsync(ct);
        if (missing > 0)
            log.LogWarning("A35 currency backfill, organization {Org}: {Missing} locked document(s) still have no exchange rate (no rate on file at their lock date).",
                org, missing);
        return (locked, missing);
    }

    private static async Task<DocumentRateLock?> TryLockAsync(
        ICurrencyService currency, Guid org, Guid currencyId, DateTime when, TransactionDomain domain, ILogger log, string what, CancellationToken ct)
    {
        try
        {
            return await currency.LockRateAsync(org, currencyId, DateOnly.FromDateTime(when), domain, ct);
        }
        catch (CurrencyRateNotFoundException ex)
        {
            log.LogInformation("A35 currency backfill: {Document} left without a rate — {Reason}", what, ex.Message);
            return null;
        }
    }
}
