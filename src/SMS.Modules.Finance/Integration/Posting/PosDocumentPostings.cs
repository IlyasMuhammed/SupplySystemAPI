using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-21..D-23) — POS documents as postings, and the QuickBooks source of kinds SalesReceipt, CreditMemo and
/// RefundReceipt. CounterSaleService calls <see cref="PublishSaleAsync"/>, <see cref="PublishReturnAsync"/> and
/// <see cref="PublishVoidAsync"/> after its single commit (tenders and refunds are committed with the sale / credit
/// note, so the shape is known when the hook runs).
/// <para>
/// <b>The type never changes (REV-8).</b> What a POS document becomes is decided by <see cref="PosDocuments"/> from rows
/// that do not change after that commit: a sale's POS tender allocations (kept even when a cheque bounces; removed only
/// by a void, which is handled first) and a credit note's refunds (append-only, written with it). A sale already held or
/// sent to QuickBooks as an invoice before A44 stays an invoice (REV-11): the SalesInvoice source keeps serving it and
/// this one skips it.
/// </para>
/// </summary>
internal sealed class PosDocumentPostings : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.SalesReceipt, SyncKind.CreditMemo, SyncKind.RefundReceipt];

    private readonly FinanceDbContext              _db;
    private readonly IQuickBooksGateway            _gateway;
    private readonly ILogger<PosDocumentPostings>  _log;
    private readonly IFinancialPostingGateway?     _posting;
    private readonly SalesInvoiceQuickBooksSource? _invoices;
    private readonly CustomerPaymentPostings?      _payments;

    /// <param name="invoices">How a POS sale that is not a receipt goes (as an invoice, as before).</param>
    /// <param name="payments">How that invoice's tenders go (as payments, D-21).</param>
    public PosDocumentPostings(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<PosDocumentPostings> log,
        IFinancialPostingGateway? posting = null, SalesInvoiceQuickBooksSource? invoices = null, CustomerPaymentPostings? payments = null)
    {
        _db       = db;
        _gateway  = gateway;
        _log      = log;
        _posting  = posting;
        _invoices = invoices;
        _payments = payments;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    // ══ After CounterSaleService's commit ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A POS sale: a sales receipt when paid in full at the till in cash-like tenders, else the invoice (as before)
    /// and its tenders as payments. Never throws.
    /// </summary>
    public async Task PublishSaleAsync(Guid invoiceUuid, CancellationToken ct = default)
    {
        try
        {
            var invoice = await _db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct);
            if (invoice is null) return;

            var shape = (await PosDocuments.ShapeSalesAsync(_db, [invoice], ct)).GetValueOrDefault(invoice.Id);
            if (shape is { IsReceipt: true })
            {
                await PostingSupport.SendAsync(_posting, _gateway, SalesPostings.SalesReceipt(invoice, shape.Tenders), _log, ct);
                return;
            }

            if (_invoices is not null) await _invoices.PublishAsync(invoice, QuickBooksPushAction.Upsert, ct);
            if (_payments is not null && shape is not null) await _payments.PublishAsync(shape.Tenders.Select(t => t.Uuid).ToList(), ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "POS sale invoice {InvoiceUuid} was posted, but could not be handed to the posting gateway; the hourly reconciliation will offer it to QuickBooks again.",
                invoiceUuid);
        }
    }

    /// <summary>A POS return: its credit note as a refund receipt or a credit memo (D-23). Never throws.</summary>
    public async Task PublishReturnAsync(Guid creditNoteUuid, CancellationToken ct = default)
    {
        try
        {
            var creditNote = await _db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == creditNoteUuid && i.Status == SalesInvoiceStatuses.CreditNote, ct);
            if (creditNote is null) return;

            var tx = (await CreditPostingsAsync([creditNote], ct)).SingleOrDefault();
            if (tx is not null) await PostingSupport.SendAsync(_posting, _gateway, tx, _log, ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "POS credit note {CreditNoteUuid} was posted, but could not be handed to the posting gateway; the hourly reconciliation will offer it to QuickBooks again.",
                creditNoteUuid);
        }
    }

    /// <summary>
    /// A POS void (D-22): the sales receipt, or the invoice and each of its tender payments, voided — by the type they
    /// were posted under (REV-9). <paramref name="wasReceipt"/> is decided by the caller with
    /// <see cref="PosDocuments.SaleIsReceipt"/> from the tenders as they were <b>before</b> the void reversed them.
    /// The void's own credit note is not posted: the voids are its accounting. Never throws.
    /// </summary>
    public async Task PublishVoidAsync(
        Guid invoiceUuid, bool wasReceipt, IReadOnlyCollection<PosTender> tenders, string reason, int userId, DateTime when,
        CancellationToken ct = default)
    {
        try
        {
            var invoice = await _db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.UUID == invoiceUuid, ct);
            if (invoice is null) return;

            if (wasReceipt)
            {
                await PostingSupport.VoidAsync(_posting, _gateway, SalesPostings.Void(invoice, FinDocType.SalesReceipt, reason, userId, when), _log, ct);
                return;
            }

            await PostingSupport.VoidAsync(_posting, _gateway, SalesPostings.Void(invoice, FinDocType.SalesInvoice, reason, userId, when), _log, ct);
            foreach (var tender in tenders.Where(t => PosDocuments.IsCashLike(t.Method)))
                await PostingSupport.VoidAsync(_posting, _gateway, new VoidRequest
                {
                    SourceDocumentId = tender.Uuid, SourceModule = PostingSourceModules.Pos, DocumentType = FinDocType.Payment,
                    SourceDocumentNumber = tender.Number, Reason = reason, VoidedBy = userId, VoidDate = when
                }, _log, ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "POS sale {InvoiceUuid} was voided, but the void could not be handed to the posting gateway; the hourly reconciliation will void it in QuickBooks.",
                invoiceUuid);
        }
    }

    // ══ The QuickBooks gateway's pull-back (direct, D-3) ════════════════════════════════════════════════════════

    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);
        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;

        var documents = await OwnPos().AsNoTracking().Include(i => i.Lines)
            .Where(i => ids.Contains(i.UUID) && !i.IsDelete).ToListAsync(ct);
        await PullAsync(kind, documents, ct);
    }

    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnPos().AsNoTracking().Where(i => !i.IsDelete && i.Status != SalesInvoiceStatuses.Draft);
        query = kind == SyncKind.SalesReceipt
            ? query.Where(i => i.Status != SalesInvoiceStatuses.CreditNote)
            : query.Where(i => i.Status == SalesInvoiceStatuses.CreditNote);
        if (changedSince is { } since) query = query.Where(i => i.CreatedDate >= since || i.ModifiedDate >= since);

        var sent = 0;
        var lastId = 0;
        while (true)
        {
            var batch = await query.Where(i => i.Id > lastId).OrderBy(i => i.Id).Take(QuickBooksSupport.BatchSize)
                .Include(i => i.Lines).ToListAsync(ct);
            sent += await PullAsync(kind, batch, ct);
            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }
        return sent;
    }

    private async Task<int> PullAsync(SyncKind kind, IReadOnlyList<SalesInvoice> documents, CancellationToken ct)
    {
        var sent = 0;

        if (kind == SyncKind.SalesReceipt)
        {
            var sales  = documents.Where(i => i.Status != SalesInvoiceStatuses.CreditNote).ToList();
            var shapes = await PosDocuments.ShapeSalesAsync(_db, sales, ct);
            var legacy = await LegacyInvoicesAsync(sales.Where(s => shapes.TryGetValue(s.Id, out var x) && x.IsReceipt).ToList(), ct);

            foreach (var sale in sales)
            {
                if (!shapes.TryGetValue(sale.Id, out var shape)) continue;
                if (shape.IsVoided)
                    sent += await DirectAsync(sale, () => _gateway.VoidAsync(SyncKind.SalesReceipt, sale.UUID.ToString(), ct), kind, ct);
                else if (shape.IsReceipt && !legacy.Contains(sale.UUID.ToString()))
                {
                    var payload = (SalesReceiptPayload)SalesPostings.SalesReceipt(sale, shape.Tenders).Metadata!.Document!;
                    sent += await DirectAsync(sale, () => _gateway.UpsertSalesReceiptAsync(payload, ct), kind, ct);
                }
            }
            return sent;
        }

        var credits = documents.Where(i => i.Status == SalesInvoiceStatuses.CreditNote).ToList();
        foreach (var tx in await CreditPostingsAsync(credits, ct))
        {
            var document = credits.First(c => c.UUID == tx.SourceDocumentId);
            if (PostingSupport.KindOf(tx.DocumentType) != kind) continue;
            sent += await DirectAsync(document, () => PostingSupport.UpsertDirectAsync(_gateway, tx.Metadata!.Document!, ct), kind, ct);
        }
        return sent;
    }

    /// <summary>
    /// The postings of these POS credit notes (void credit notes left out: a void is posted as voids). Whether each
    /// original sale went as an invoice decides whether the credit can be applied to it.
    /// </summary>
    private async Task<List<FinancialTransaction>> CreditPostingsAsync(IReadOnlyList<SalesInvoice> creditNotes, CancellationToken ct)
    {
        var returns = creditNotes.Where(c => PosDocuments.IsPos(c) && !PosDocuments.IsVoidCreditNote(c)).ToList();
        if (returns.Count == 0) return [];

        var originalUuids = returns.Where(c => c.OriginalInvoiceUuid is not null).Select(c => c.OriginalInvoiceUuid!.Value).Distinct().ToList();
        var orgs = returns.Select(c => c.OrganizationId).Distinct().ToList();
        var originals = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => orgs.Contains(i.OrganizationId) && originalUuids.Contains(i.UUID)).ToListAsync(ct);
        var shapes  = await PosDocuments.ShapeSalesAsync(_db, originals, ct);
        var legacy  = await LegacyInvoicesAsync(originals.Where(o => shapes.TryGetValue(o.Id, out var s) && s.IsReceipt).ToList(), ct);
        var refunds = await PosDocuments.RefundsAsync(_db, returns, ct);

        return returns.Select(c =>
        {
            var original = originals.FirstOrDefault(o => o.UUID == c.OriginalInvoiceUuid);
            var originalIsInvoice = original is not null
                && !(shapes.TryGetValue(original.Id, out var s) && s.IsReceipt && !legacy.Contains(original.UUID.ToString()));
            return SalesPostings.CreditNote(c, original, refunds.GetValueOrDefault(c.UUID) ?? [], originalIsInvoice);
        }).ToList();
    }

    /// <summary>POS sales the QuickBooks gateway already knows as invoices (held or sent before A44): they stay invoices (REV-11).</summary>
    private Task<HashSet<string>> LegacyInvoicesAsync(IReadOnlyList<SalesInvoice> sales, CancellationToken ct) =>
        PosDocuments.LegacyInvoicesAsync(_gateway, sales, ct);   // REV-20: empty placeholders do not count

    private async Task<int> DirectAsync(SalesInvoice document, Func<Task<GatewayResult>> call, SyncKind kind, CancellationToken ct)
    {
        try
        {
            QuickBooksSupport.LogResult(_log, await call(), kind, document.UUID.ToString(), document.InvoiceNumber);
            return 1;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex, "POS document {Uuid} ({Number}) could not be handed to the QuickBooks gateway as a {Kind}; the hourly reconciliation will offer it again.",
                document.UUID, document.InvoiceNumber, kind);
            return 0;
        }
    }

    /// <summary>The current organization's POS documents, limited explicitly (security audit Q1).</summary>
    private IQueryable<SalesInvoice> OwnPos()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.SalesInvoices.Where(i => i.OrganizationId == organizationId && i.SourceType == SalesInvoiceSources.Pos);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (!SupportedKinds.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends POS sales receipts, credit memos and refund receipts only.");
    }
}
