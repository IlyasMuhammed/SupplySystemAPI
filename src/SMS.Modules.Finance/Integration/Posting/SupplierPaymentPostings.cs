using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-20) — supplier payments as postings, and as the QuickBooks source of kind BillPayment: POSTED → a BillPayment
/// posting (STANDARD lines link their bills; an advance links none); BOUNCED (or CANCELLED after it was posted) → void;
/// DRAFT / APPROVED → nothing yet; a PURCHASE_RETURN_SETTLEMENT is never posted (the vendor credit settles it). The
/// legacy single-invoice <c>Payment</c> is not sent (R-2).
/// </summary>
internal sealed class SupplierPaymentPostings : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.BillPayment];

    private readonly FinanceDbContext                 _db;
    private readonly IQuickBooksGateway               _gateway;
    private readonly ILogger<SupplierPaymentPostings> _log;
    private readonly IFinancialPostingGateway?        _posting;
    private readonly ILookupsService?                 _lookups;

    public SupplierPaymentPostings(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<SupplierPaymentPostings> log,
        IFinancialPostingGateway? posting = null, ILookupsService? lookups = null)
    {
        _db      = db;
        _gateway = gateway;
        _log     = log;
        _posting = posting;
        _lookups = lookups;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);
        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;

        var payments = await OwnPayments().AsNoTracking().Include(p => p.Lines).Where(p => ids.Contains(p.UUID)).ToListAsync(ct);
        await SendAsync(payments, publish: false, ct);
    }

    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnPayments().AsNoTracking().Where(p => p.PostedAt != null);
        if (changedSince is { } since)
            query = query.Where(p => p.CreatedDate >= since || p.ModifiedDate >= since || p.PostedAt >= since || p.BouncedAt >= since);

        var sent = 0;
        var lastId = 0;
        while (true)
        {
            var batch = await query.Where(p => p.Id > lastId).OrderBy(p => p.Id).Take(QuickBooksSupport.BatchSize)
                .Include(p => p.Lines).ToListAsync(ct);
            sent += await SendAsync(batch, publish: false, ct);
            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }
        return sent;
    }

    /// <summary>After a commit that posted, bounced or cancelled the payment. Never throws.</summary>
    public async Task PublishAsync(Guid paymentUuid, CancellationToken ct = default)
    {
        try
        {
            var payments = await _db.SupplierPayments.AsNoTracking().Include(p => p.Lines)
                .Where(p => p.UUID == paymentUuid).ToListAsync(ct);
            await SendAsync(payments, publish: PostingSupport.IsActive(_posting), ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Supplier payment {PaymentUuid} was saved, but could not be handed to the posting gateway; " +
                "the hourly reconciliation will offer it to QuickBooks again.", paymentUuid);
        }
    }

    private async Task<int> SendAsync(IReadOnlyList<SupplierPayment> payments, bool publish, CancellationToken ct)
    {
        payments = payments.Where(p => p.PostedAt is not null || p.Status == "POSTED").ToList();
        if (payments.Count == 0) return 0;

        // Which bills are imported opening balances (never in QuickBooks, not linked there).
        var billUuids = payments.SelectMany(p => p.Lines.Select(l => l.InvoiceUuid)).Distinct().ToList();
        var opening = billUuids.Count == 0 ? [] : (await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => billUuids.Contains(i.UUID) && i.SourceType == SalesInvoiceSources.Opening)
                .Select(i => i.UUID).ToListAsync(ct)).ToHashSet();

        var sent = 0;
        foreach (var payment in payments)
        {
            if (!PaymentPostings.IsPosted(payment))
            {
                _log.LogDebug("Supplier payment {PaymentNumber} settles a purchase return: its vendor credit is what is posted (D-20).", payment.PaymentNumber);
                continue;
            }

            try
            {
                if (payment.Status == "POSTED")
                {
                    var applications = payment.Lines
                        .GroupBy(l => l.InvoiceUuid)
                        .Select(g => new PostingApplication(g.Key, g.First().InvoiceNumber, g.Sum(l => l.AllocatedAmount), opening.Contains(g.Key)))
                        .ToList();
                    var tx = PaymentPostings.SupplierPayment(payment, applications, BaseCode(payment));
                    if (publish) await PostingSupport.SendAsync(_posting, _gateway, tx, _log, ct);
                    else QuickBooksSupport.LogResult(_log, await _gateway.UpsertBillPaymentAsync((BillPaymentPayload)tx.Metadata!.Document!, ct),
                        SyncKind.BillPayment, payment.UUID.ToString(), payment.PaymentNumber);
                }
                else
                {
                    var request = PaymentPostings.VoidSupplierPayment(payment, $"Payment {payment.Status.ToLowerInvariant()}", payment.ModifiedBy, payment.BouncedAt ?? payment.ModifiedDate);
                    if (publish) await PostingSupport.VoidAsync(_posting, _gateway, request, _log, ct);
                    else QuickBooksSupport.LogResult(_log, await _gateway.VoidAsync(SyncKind.BillPayment, payment.UUID.ToString(), ct),
                        SyncKind.BillPayment, payment.UUID.ToString(), payment.PaymentNumber);
                }
                sent++;
            }
            catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
            {
                _log.LogWarning(ex,
                    "Supplier payment {PaymentUuid} ({PaymentNumber}) could not be handed to the {Route}; the hourly reconciliation will offer it again.",
                    payment.UUID, payment.PaymentNumber, publish ? "posting gateway" : "QuickBooks gateway");
            }
        }
        return sent;
    }

    private string? BaseCode(SupplierPayment payment) =>
        payment.BaseCurrencyId is { } baseId && _lookups is not null
            ? _lookups.GetCurrencies().FirstOrDefault(c => c.Id == baseId)?.Code?.Trim()
            : null;

    /// <summary>The current organization's payments, limited explicitly (security audit Q1).</summary>
    private IQueryable<SupplierPayment> OwnPayments()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.SupplierPayments.Where(p => p.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.BillPayment)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends supplier payments only.");
    }
}
