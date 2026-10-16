using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-19) — customer payments as postings, and as the QuickBooks source of kind Payment.
/// <list type="bullet">
/// <item>RECEIVED → a Payment posting (record, and again after every allocation: same key, changed payload = update, D-13).</item>
/// <item>BOUNCED / REVERSED → void.</item>
/// <item>STORE_CREDIT / EXCHANGE move no money: never posted (D-19).</item>
/// <item>A POS tender of a sale paid in full at the till is inside its sales receipt (D-21): not posted on its own.</item>
/// </list>
/// The payment service calls <see cref="PublishAsync"/> after its commit (posting gateway; never throws); the QuickBooks
/// gateway calls <see cref="PushAsync"/> / <see cref="PushAllAsync"/> (pull-back, straight to QuickBooks, D-3).
/// </summary>
internal sealed class CustomerPaymentPostings : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.Payment];

    private readonly FinanceDbContext                 _db;
    private readonly IQuickBooksGateway               _gateway;
    private readonly ILogger<CustomerPaymentPostings> _log;
    private readonly IFinancialPostingGateway?        _posting;
    private readonly ILookupsService?                 _lookups;

    public CustomerPaymentPostings(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<CustomerPaymentPostings> log,
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

        var payments = await Loaded(OwnPayments().Where(p => ids.Contains(p.UUID))).ToListAsync(ct);
        await SendAsync(payments, publish: false, ct);
    }

    /// <summary>Every payment created or changed (allocated, bounced, reversed) since <paramref name="changedSince"/>, in batches.</summary>
    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnPayments();
        if (changedSince is { } since) query = query.Where(p => p.CreatedDate >= since || p.ModifiedDate >= since);

        var sent = 0;
        var lastId = 0;
        while (true)
        {
            var batch = await Loaded(query.Where(p => p.Id > lastId).OrderBy(p => p.Id).Take(QuickBooksSupport.BatchSize)).ToListAsync(ct);
            sent += await SendAsync(batch, publish: false, ct);
            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }
        return sent;
    }

    /// <summary>After a commit that recorded, allocated, bounced or reversed these payments. Never throws.</summary>
    public async Task PublishAsync(IReadOnlyCollection<Guid> paymentUuids, CancellationToken ct = default)
    {
        if (paymentUuids.Count == 0) return;
        try
        {
            var ids = paymentUuids.Distinct().ToList();
            var payments = await Loaded(_db.CustomerPayments.Where(p => ids.Contains(p.UUID))).ToListAsync(ct);
            await SendAsync(payments, publish: PostingSupport.IsActive(_posting), ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Customer payment(s) {PaymentUuids} were saved, but could not be handed to the posting gateway; " +
                "the hourly reconciliation will offer them to QuickBooks again.",
                string.Join(", ", paymentUuids));
        }
    }

    public Task PublishAsync(Guid paymentUuid, CancellationToken ct = default) => PublishAsync([paymentUuid], ct);

    /// <summary>Posts / voids each payment as it stands. Returns how many reached a gateway; a failure is logged, never thrown.</summary>
    private async Task<int> SendAsync(IReadOnlyList<CustomerPayment> payments, bool publish, CancellationToken ct)
    {
        if (payments.Count == 0) return 0;

        // POS tenders of a sale paid in full at the till belong to its sales receipt.
        var posInvoices = payments.Where(p => p.SourceUuid is not null)
            .SelectMany(p => p.Allocations.Select(a => a.SalesInvoice)).DistinctBy(i => i.Id).ToList();
        var shapes = await PosDocuments.ShapeSalesAsync(_db, posInvoices, ct);

        var sent = 0;
        foreach (var payment in payments)
        {
            if (!PaymentPostings.IsPosted(payment))
            {
                _log.LogDebug("Customer payment {PaymentNumber} is {Method}: no money moved, nothing is posted (D-19).", payment.PaymentNumber, payment.PaymentMethod);
                continue;
            }
            var receipt = payment.Allocations.Select(a => a.SalesInvoice).FirstOrDefault(i => shapes.TryGetValue(i.Id, out var s) && s.IsReceipt);
            if (receipt is not null)
            {
                // REV-15: a till tender of a sales receipt that bounces afterwards (only a bounce: a POS void reverses it and
                // voids the receipt itself) — the customer owes it again. Only on the service's own change, never on pull-back.
                if (publish && payment.Status == CustomerPaymentStatuses.Bounced)
                    sent += await ReceiptTenderBouncedAsync(payment, receipt, ct);
                else
                    _log.LogDebug("Customer payment {PaymentNumber} is a till tender of a sales receipt: posted with the receipt (D-21).", payment.PaymentNumber);
                continue;
            }

            try
            {
                if (payment.Status == CustomerPaymentStatuses.Received)
                {
                    var tx = PaymentPostings.CustomerPayment(payment, Applications(payment), BaseCode(payment));
                    if (publish) await PostingSupport.SendAsync(_posting, _gateway, tx, _log, ct);
                    else QuickBooksSupport.LogResult(_log, await _gateway.UpsertPaymentAsync((PaymentPayload)tx.Metadata!.Document!, ct),
                        SyncKind.Payment, payment.UUID.ToString(), payment.PaymentNumber);
                }
                else
                {
                    var request = PaymentPostings.VoidCustomerPayment(payment, $"Payment {payment.Status.ToLowerInvariant()}", payment.ModifiedBy, payment.ModifiedDate);
                    if (publish) await PostingSupport.VoidAsync(_posting, _gateway, request, _log, ct);
                    else QuickBooksSupport.LogResult(_log, await _gateway.VoidAsync(SyncKind.Payment, payment.UUID.ToString(), ct),
                        SyncKind.Payment, payment.UUID.ToString(), payment.PaymentNumber);
                }
                sent++;
            }
            catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
            {
                _log.LogWarning(ex,
                    "Customer payment {PaymentUuid} ({PaymentNumber}) could not be handed to the {Route}; the hourly reconciliation will offer it again.",
                    payment.UUID, payment.PaymentNumber, publish ? "posting gateway" : "QuickBooks gateway");
            }
        }
        return sent;
    }

    /// <summary>
    /// REV-15 / D-73 — a bounced cheque (or card) tender of a sale posted as a sales receipt. The GL gets a document-less
    /// Payment posting under the tender's own key (<c>pos:Payment:{uuid}</c>, never used otherwise for a receipt tender):
    /// Dr RECEIVABLE / Cr BANK-or-CASH — the money never arrived, the customer owes it again, as SCM's ledger says
    /// (external targets skip it: NO_DOCUMENT). QuickBooks cannot un-pay a receipt: once it is synced there, it is flagged for
    /// the accountant; one not yet sent is left alone (flagging would drop it).
    /// </summary>
    private async Task<int> ReceiptTenderBouncedAsync(CustomerPayment payment, SalesInvoice receipt, CancellationToken ct)
    {
        try
        {
            if (PostingSupport.IsActive(_posting))
            {
                var tx = BouncedReceiptTender(payment, receipt);
                PostingSupport.LogResult(_log, await _posting!.PostAsync(tx, ct), tx.DocumentType, tx.SourceDocumentId, tx.SourceDocumentNumber);
            }

            var status = (await _gateway.GetStatusAsync(SyncKind.SalesReceipt, [receipt.UUID.ToString()], ct)).FirstOrDefault();
            if (status is { State: SyncState.Synced or SyncState.NeedsResolution })
                QuickBooksSupport.LogResult(_log,
                    await _gateway.FlagForAccountantAsync(SyncKind.SalesReceipt, receipt.UUID.ToString(), BouncedReceiptNote(payment, receipt), ct),
                    SyncKind.SalesReceipt, receipt.UUID.ToString(), receipt.InvoiceNumber);
            return 1;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Customer payment {PaymentUuid} ({PaymentNumber}), a till tender of sales receipt {InvoiceNumber}, bounced, but the posting gateway could not be told; " +
                "book the reopened receivable by hand.", payment.UUID, payment.PaymentNumber, receipt.InvoiceNumber);
            return 0;
        }
    }

    /// <summary>The GL-only posting of a bounced receipt tender: Dr RECEIVABLE / Cr the account the tender was deposited to.</summary>
    internal static FinancialTransaction BouncedReceiptTender(CustomerPayment payment, SalesInvoice receipt)
    {
        var currency = PostingSupport.Currency(payment.CurrencyCode);
        var (rate, _) = SalesInvoicePayloadFactory.DocumentRate(currency, receipt.ExchangeRate, receipt.BaseCurrencyCode);
        var lines = new GlLines(payment.PartnerId)
            .Debit(PostingRoles.Receivable, payment.Amount, $"Bounced {payment.PaymentMethod} {payment.PaymentNumber}")
            .Credit(PostingRoles.MoneyFor(payment.PaymentMethod), payment.Amount, $"{payment.PaymentMethod} {payment.PaymentNumber} returned");

        return new FinancialTransaction
        {
            DocumentType         = FinDocType.Payment,
            SourceDocumentId     = payment.UUID,
            SourceDocumentNumber = payment.PaymentNumber,
            SourceModule         = PaymentPostings.ModuleOf(payment),
            TransactionDate      = (payment.ModifiedDate ?? payment.PaymentDate).Date,
            PartnerId            = payment.PartnerId,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Customer,
            PartnerName          = payment.PartnerName,
            CurrencyCode         = currency,
            ExchangeRate         = rate,
            Description          = $"Till tender {payment.PaymentNumber} of POS sale {receipt.SourceNumber} ({receipt.InvoiceNumber}) bounced",
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata { Reference = payment.ChequeNumber, LinkedDocumentId = receipt.UUID }
        };
    }

    internal static string BouncedReceiptNote(CustomerPayment payment, SalesInvoice receipt) =>
        $"The {payment.PaymentMethod} tender {payment.PaymentNumber} ({payment.Amount:0.00} {payment.CurrencyCode}) of POS sale {receipt.SourceNumber} " +
        $"(sales receipt {receipt.InvoiceNumber}) bounced in SCM on {DateTime.UtcNow:dd MMM yyyy}: the customer owes it again. " +
        "QuickBooks still shows the receipt as paid — record the returned payment there by hand, then mark this item resolved.";

    /// <summary>The payment's allocations, one per invoice (an allocation made later adds to the same invoice).</summary>
    internal static List<PostingApplication> Applications(CustomerPayment payment) =>
        payment.Allocations
            .GroupBy(a => a.SalesInvoice.UUID)
            .Select(g => new PostingApplication(g.Key, g.First().SalesInvoice.InvoiceNumber, g.Sum(a => a.AllocatedAmount),
                g.First().SalesInvoice.SourceType == SalesInvoiceSources.Opening))
            .ToList();

    /// <summary>The sale base the payment's rate converts into: the catalog's code for its base id, else an allocated invoice's.</summary>
    private string? BaseCode(CustomerPayment payment)
    {
        if (payment.BaseCurrencyId is { } baseId && _lookups is not null)
        {
            var code = _lookups.GetCurrencies().FirstOrDefault(c => c.Id == baseId)?.Code?.Trim();
            if (!string.IsNullOrEmpty(code)) return code;
        }
        return payment.Allocations.Select(a => a.SalesInvoice.BaseCurrencyCode).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
    }

    private static IQueryable<CustomerPayment> Loaded(IQueryable<CustomerPayment> query) =>
        query.AsNoTracking().Include(p => p.Allocations).ThenInclude(a => a.SalesInvoice);

    /// <summary>The current organization's payments, limited explicitly (security audit Q1: a super admin's "Sync all").</summary>
    private IQueryable<CustomerPayment> OwnPayments()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.CustomerPayments.Where(p => p.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.Payment)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends customer payments only.");
    }
}
