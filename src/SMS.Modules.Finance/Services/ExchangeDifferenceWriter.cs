using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>Document / payment kinds written on <see cref="ExchangeDifference"/> rows (API-CONTRACT §7.1).</summary>
internal static class ExchangeDifferenceRefs
{
    public const string SalesInvoice    = "SALES_INVOICE";
    public const string SupplierInvoice = "SUPPLIER_INVOICE";
    public const string CustomerPayment = "CUSTOMER_PAYMENT";
    public const string SupplierPayment = "SUPPLIER_PAYMENT";
}

/// <summary>One allocation of a payment to an invoice, as the realized difference needs it.</summary>
/// <param name="AmountCurrency">The allocated amount, in the (shared) document currency.</param>
/// <param name="InvoiceRate">The rate the invoice was booked (locked) at; null = never locked → no difference can be worked out.</param>
/// <param name="InvoiceBaseCurrencyId">The base the invoice was booked in; a different base than the payment's → no difference.</param>
internal sealed record RealizedAllocation(
    string Side,
    string DocumentType, int DocumentId, Guid DocumentUuid, string? DocumentNo,
    string PaymentType, int? PaymentId, Guid PaymentUuid, string? PaymentNo, int? AllocationId,
    Guid? PartnerId,
    decimal AmountCurrency, decimal? InvoiceRate, Guid? InvoiceBaseCurrencyId);

/// <summary>
/// A35 C7 / D-15 — writes the exchange-difference register (<c>finance.exchange_differences</c>; there is no GL, the row is
/// the spec's journal entry). Rows are only <b>tracked</b>: the caller's own save commits them with the payment, so a
/// difference is booked exactly when the payment is. Customer and supplier ledgers are not touched (they stay in the
/// transaction currency).
/// <para>
/// <b>Reverse, don't edit.</b> A bounced (or otherwise undone) payment gets one opposite row per realized row still standing
/// (every amount negated, the same references), so the register nets to zero for it and keeps the history.
/// </para>
/// </summary>
internal sealed class ExchangeDifferenceWriter
{
    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;
    private readonly TimeProvider                  _clock;

    public ExchangeDifferenceWriter(FinanceDbContext db, IOrganizationCurrencyService? settings = null, TimeProvider? clock = null)
    {
        _db       = db;
        _settings = settings;
        _clock    = clock ?? TimeProvider.System;
    }

    /// <summary>The organization's gain/loss account codes (realized or unrealized); nulls when none are set.</summary>
    public async Task<(string? Gain, string? Loss)> AccountsAsync(Guid organizationId, bool realized, CancellationToken ct = default)
    {
        if (_settings is null) return (null, null);
        // A mock of the interface answers the default-implemented member with null; treat that as "no codes set".
        var s = await (_settings.GetSettingsAsync(organizationId, ct) ?? Task.FromResult<OrgCurrencySettingsSnapshot>(null!));
        if (s is null) return (null, null);
        return realized
            ? (s.ExchangeGainAccountCode, s.ExchangeLossAccountCode)
            : (s.UnrealizedGainAccountCode, s.UnrealizedLossAccountCode);
    }

    /// <summary>
    /// The realized difference on one allocation (BR-C7-01..03): the allocated amount at the payment's locked rate against
    /// the invoice's booked rate, both rounded at the base's decimals. Null — and nothing tracked — when there is nothing to
    /// book: same currency as the base (BR-C7-05), an invoice that was never locked or was booked in another base, or no
    /// difference at all. The returned row is tracked, unsaved.
    /// </summary>
    public ExchangeDifference? TrackRealized(
        Guid organizationId, RealizedAllocation a, DocumentRateLock payment, (string? Gain, string? Loss) accounts, int? userId)
    {
        if (payment.SameCurrency) return null;
        if (a.InvoiceRate is not { } bookedRate || a.InvoiceBaseCurrencyId != payment.BaseCurrencyId) return null;

        var amounts = ExchangeDifferenceMath.Compute(a.AmountCurrency, bookedRate, payment.Rate, payment.BaseDecimalPlaces, a.Side);
        if (amounts.Difference == 0m) return null;

        var row = new ExchangeDifference
        {
            Uuid              = Guid.NewGuid(),
            OrganizationId    = organizationId,
            Kind              = ExchangeDifferenceKinds.Realized,
            Side              = a.Side,
            DocumentType      = a.DocumentType,
            DocumentId        = a.DocumentId,
            DocumentUuid      = a.DocumentUuid,
            DocumentNo        = a.DocumentNo,
            PaymentType       = a.PaymentType,
            PaymentId         = a.PaymentId,
            PaymentUuid       = a.PaymentUuid,
            PaymentNo         = a.PaymentNo,
            AllocationId      = a.AllocationId,
            PartnerId         = a.PartnerId,
            CurrencyId        = payment.CurrencyId,
            CurrencyCode      = payment.CurrencyCode,
            AmountCurrency    = a.AmountCurrency,
            BookedRate        = bookedRate,
            SettlementRate    = payment.Rate,
            BaseCurrencyId    = payment.BaseCurrencyId,
            BaseCurrencyCode  = payment.BaseCurrencyCode,
            BookedAmountBase  = amounts.BookedBase,
            SettledAmountBase = amounts.SettledBase,
            DifferenceBase    = amounts.Difference,
            AccountCode       = amounts.IsGain ? accounts.Gain : accounts.Loss,
            PostedAt          = _clock.GetUtcNow().UtcDateTime,
            CreatedBy         = userId,
            CreatedDate       = _clock.GetUtcNow().UtcDateTime
        };
        _db.ExchangeDifferences.Add(row);
        return row;
    }

    /// <summary>True when the payment already has realized rows (posted twice / a retry must not book them again).</summary>
    public Task<bool> HasRealizedAsync(Guid organizationId, string paymentType, Guid paymentUuid, CancellationToken ct = default) =>
        _db.ExchangeDifferences.IgnoreQueryFilters().AnyAsync(d =>
            d.OrganizationId == organizationId && d.Kind == ExchangeDifferenceKinds.Realized
         && d.PaymentType == paymentType && d.PaymentUuid == paymentUuid, ct);

    /// <summary>
    /// Undoes a payment's realized differences: one opposite row per row, only while they do not already net to zero (a
    /// second bounce, or a retry, adds nothing). Tracked, unsaved. Returns how many were added.
    /// </summary>
    public async Task<int> TrackReversalAsync(Guid organizationId, string paymentType, Guid paymentUuid, int? userId, CancellationToken ct = default)
    {
        var rows = await _db.ExchangeDifferences.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Kind == ExchangeDifferenceKinds.Realized
                     && d.PaymentType == paymentType && d.PaymentUuid == paymentUuid)
            .ToListAsync(ct);

        // Allocated amounts are always positive, so a negative row is a reversal already written.
        if (rows.Count == 0 || rows.Any(r => r.AmountCurrency < 0m)) return 0;

        var now = _clock.GetUtcNow().UtcDateTime;
        var originals = rows;
        foreach (var r in originals)
            _db.ExchangeDifferences.Add(new ExchangeDifference
            {
                Uuid = Guid.NewGuid(), OrganizationId = organizationId, Kind = r.Kind, Side = r.Side,
                DocumentType = r.DocumentType, DocumentId = r.DocumentId, DocumentUuid = r.DocumentUuid, DocumentNo = r.DocumentNo,
                PaymentType = r.PaymentType, PaymentId = r.PaymentId, PaymentUuid = r.PaymentUuid, PaymentNo = r.PaymentNo,
                AllocationId = r.AllocationId, PartnerId = r.PartnerId,
                CurrencyId = r.CurrencyId, CurrencyCode = r.CurrencyCode, AmountCurrency = -r.AmountCurrency,
                BookedRate = r.BookedRate, SettlementRate = r.SettlementRate,
                BaseCurrencyId = r.BaseCurrencyId, BaseCurrencyCode = r.BaseCurrencyCode,
                BookedAmountBase = -r.BookedAmountBase, SettledAmountBase = -r.SettledAmountBase, DifferenceBase = -r.DifferenceBase,
                AccountCode = r.AccountCode, PostedAt = now, CreatedBy = userId, CreatedDate = now
            });

        return originals.Count;
    }
}
