using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 D-11 (FIN) — the part of the document backfill that needs rates, so it runs after CUR's startup backfill has the
/// organization's currencies and rates in place (<see cref="ICurrencyRatesReadyParticipant"/>). Fills locked FOREIGN Finance
/// documents that still have no rate, at the rate of their own lock date, when one exists; leaves them null otherwise (no
/// guessing — logged). Same-currency documents were already filled by the migration.
/// <list type="bullet">
/// <item>Sales invoices past DRAFT — invoice date, sale base.</item>
/// <item>Supplier invoices Approved/Reversed — approval date (else invoice date), purchase base.</item>
/// <item>Customer payments — payment date, sale base. Their historical allocations get no realized difference (none was
/// booked at the time); ExchangeDifference stays null = "not computed".</item>
/// <item>Supplier payments POSTED/BOUNCED — payment date, purchase base (same rule for differences).</item>
/// </list>
/// Idempotent (only rows with no rate are touched), explicit organization (query filters off).
/// </summary>
internal sealed class FinanceDocumentRatesBackfill : ICurrencyRatesReadyParticipant
{
    private readonly FinanceDbContext _db;
    private readonly ICurrencyService _currency;
    private readonly ILookupsService? _lookups;
    private readonly ILogger<FinanceDocumentRatesBackfill>? _log;

    public FinanceDocumentRatesBackfill(
        FinanceDbContext db, ICurrencyService currency, ILookupsService? lookups = null, ILogger<FinanceDocumentRatesBackfill>? log = null)
    {
        _db       = db;
        _currency = currency;
        _lookups  = lookups;
        _log      = log;
    }

    private Guid? IdOf(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : _lookups?.GetCurrencies().FirstOrDefault(c => string.Equals(c.Code?.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    private async Task<DocumentRateLock?> TryLockAsync(Guid org, Guid? currencyId, DateTime date, TransactionDomain domain, string what, CancellationToken ct)
    {
        if (currencyId is not { } id) { _log?.LogWarning("A35 backfill: {What} has a currency the catalog does not know; left without a rate.", what); return null; }
        try { return await _currency.LockRateAsync(org, id, DateOnly.FromDateTime(date), domain, ct); }
        catch (CurrencyRateNotFoundException ex)
        {
            _log?.LogWarning("A35 backfill: {What} left without a rate: {Reason}", what, ex.Message);
            return null;
        }
    }

    public async Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default)
    {
        var invoices = await _db.SalesInvoices.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == organizationId && i.Status != SalesInvoiceStatuses.Draft && i.ExchangeRate == null)
            .ToListAsync(ct);
        foreach (var i in invoices)
        {
            var lk = await TryLockAsync(organizationId, i.CurrencyId ?? IdOf(i.CurrencyCode), i.InvoiceDate, TransactionDomain.Sale, $"sales invoice {i.InvoiceNumber}", ct);
            if (lk is null) continue;
            (i.CurrencyId, i.ExchangeRate, i.BaseCurrencyId, i.BaseCurrencyCode, i.BaseGrandTotal, i.ExchangeRateLockedAt) =
                (lk.CurrencyId, lk.Rate, lk.BaseCurrencyId, lk.BaseCurrencyCode, lk.ToBase(i.GrandTotal), i.InvoiceDate);
        }

        var bills = await _db.Invoices.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == organizationId && (i.MatchStatus == InvoiceMatchStatus.Approved || i.MatchStatus == InvoiceMatchStatus.Reversed)
                     && i.ExchangeRate == null)
            .ToListAsync(ct);
        foreach (var i in bills)
        {
            var when = i.ApprovedAt ?? i.InvoiceDate;
            var lk = await TryLockAsync(organizationId, i.CurrencyId ?? IdOf(i.Currency), when, TransactionDomain.Purchase, $"supplier invoice {i.InvoiceNumber}", ct);
            if (lk is null) continue;
            (i.CurrencyId, i.ExchangeRate, i.BaseCurrencyId, i.BaseCurrencyCode, i.BaseTotalAmount, i.ExchangeRateLockedAt) =
                (lk.CurrencyId, lk.Rate, lk.BaseCurrencyId, lk.BaseCurrencyCode, lk.ToBase(i.TotalAmount), when);
        }

        var receipts = await _db.CustomerPayments.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == organizationId && p.ExchangeRate == null)
            .ToListAsync(ct);
        foreach (var p in receipts)
        {
            var lk = await TryLockAsync(organizationId, p.CurrencyId ?? IdOf(p.CurrencyCode), p.PaymentDate, TransactionDomain.Sale, $"customer payment {p.PaymentNumber}", ct);
            if (lk is null) continue;
            (p.CurrencyId, p.ExchangeRate, p.BaseCurrencyId, p.AmountBase) = (lk.CurrencyId, lk.Rate, lk.BaseCurrencyId, lk.ToBase(p.Amount));
        }

        var payments = await _db.SupplierPayments.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == organizationId && (p.Status == "POSTED" || p.Status == "BOUNCED") && p.ExchangeRate == null)
            .ToListAsync(ct);
        foreach (var p in payments)
        {
            var lk = await TryLockAsync(organizationId, p.CurrencyId ?? IdOf(p.CurrencyCode), p.PaymentDate, TransactionDomain.Purchase, $"supplier payment {p.PaymentNumber}", ct);
            if (lk is null) continue;
            (p.CurrencyId, p.ExchangeRate, p.BaseCurrencyId, p.AmountBase) = (lk.CurrencyId, lk.Rate, lk.BaseCurrencyId, lk.ToBase(p.TotalAmount));
        }

        await _db.SaveChangesAsync(ct);
    }
}
