using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// Sales invoices, for the QuickBooks gateway. Called <b>by</b> the gateway — from background jobs, with the
/// tenant set through <c>HangfireTenantScope</c> — for "Push now" and the hourly reconciliation. The sales
/// invoice service's own changes go through <see cref="PublishAsync"/> here, so both paths build an invoice
/// the same way.
/// <para>
/// A44: the service's changes go to the financial posting gateway (<see cref="PublishAsync"/>: the GL and QuickBooks
/// through one call, D-25); the gateway's pull-back (<see cref="PushAsync"/>, <see cref="PushAllAsync"/>) still calls
/// the QuickBooks gateway directly (D-3). POS documents that are not invoices in QuickBooks — a sale paid in full at the
/// till (a sales receipt, D-21) and POS credit notes (credit memo / refund receipt, D-23) — are served by
/// <see cref="PosDocumentPostings"/>, not here; a voided POS sale is voided (D-22).
/// </para>
/// </summary>
internal sealed class SalesInvoiceQuickBooksSource : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.SalesInvoice];

    private readonly FinanceDbContext                      _db;
    private readonly IQuickBooksGateway                    _gateway;
    private readonly ILogger<SalesInvoiceQuickBooksSource> _log;
    private readonly IFinancialPostingGateway?             _posting;

    /// <param name="posting">A44 — the posting gateway the service's changes go through; without one (or the Null one) they go straight to QuickBooks as before.</param>
    public SalesInvoiceQuickBooksSource(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<SalesInvoiceQuickBooksSource> log,
        IFinancialPostingGateway? posting = null)
    {
        _db      = db;
        _gateway = gateway;
        _log     = log;
        _posting = posting;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    /// <summary>
    /// Sends the invoices asked for as they stand now: an issued (or paid, or overdue) one is upserted, a
    /// cancelled one voided, a draft skipped. Unknown, malformed and deleted ids are skipped.
    /// </summary>
    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;

        var invoices = await OwnInvoices().AsNoTracking()
            .Include(i => i.Lines)
            .Where(i => ids.Contains(i.UUID) && !i.IsDelete)
            .ToListAsync(ct);

        var actions = await ActionsAsync(invoices, ct);
        foreach (var invoice in invoices)
            await SendAsync(invoice, actions[invoice.Id], ct);
    }

    /// <summary>
    /// Every invoice that has left DRAFT, created or modified since <paramref name="changedSince"/> —
    /// cancelled ones included, which are voided. Loaded <see cref="QuickBooksSupport.BatchSize"/> at a
    /// time by id, lines and all.
    /// </summary>
    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnInvoices().AsNoTracking()
            .Where(i => !i.IsDelete && i.Status != SalesInvoiceStatuses.Draft);

        if (changedSince is { } since)
            query = query.Where(i => i.CreatedDate >= since || i.ModifiedDate >= since);

        var sent   = 0;
        var lastId = 0;

        while (true)
        {
            var batch = await query.Where(i => i.Id > lastId)
                .OrderBy(i => i.Id)
                .Take(QuickBooksSupport.BatchSize)
                .Include(i => i.Lines)
                .ToListAsync(ct);

            var actions = await ActionsAsync(batch, ct);
            foreach (var invoice in batch)
                if (await SendAsync(invoice, actions[invoice.Id], ct))
                    sent++;

            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }

        return sent;
    }

    /// <summary>
    /// What the pull-back does for each invoice: <see cref="SalesInvoiceQuickBooksRules.ForCurrentState"/>, except for
    /// POS documents (D-73) — a POS credit note is not an invoice (skipped: it is a credit memo / refund receipt), a
    /// voided POS sale is voided, and a POS sale paid in full at the till is a sales receipt (skipped) — unless the
    /// QuickBooks gateway already holds it as an invoice (a sale from before A44), which stays one.
    /// </summary>
    private async Task<Dictionary<int, QuickBooksPushAction>> ActionsAsync(IReadOnlyList<SalesInvoice> invoices, CancellationToken ct)
    {
        var actions = invoices.ToDictionary(i => i.Id, i => SalesInvoiceQuickBooksRules.ForCurrentState(i.Status));

        foreach (var credit in invoices.Where(i => PosDocuments.IsPos(i) && i.Status == SalesInvoiceStatuses.CreditNote))
            actions[credit.Id] = QuickBooksPushAction.None;

        var shapes = await PosDocuments.ShapeSalesAsync(_db, invoices, ct);
        if (shapes.Count == 0) return actions;

        var receipts = invoices.Where(i => shapes.TryGetValue(i.Id, out var s) && s.IsReceipt).ToList();
        // REV-20: only a map that really carries a pre-A44 invoice counts — not an empty placeholder.
        var legacy = await PosDocuments.LegacyInvoicesAsync(_gateway, receipts, ct);

        foreach (var invoice in invoices)
        {
            if (!shapes.TryGetValue(invoice.Id, out var shape)) continue;
            if (shape.IsVoided) actions[invoice.Id] = QuickBooksPushAction.Void;
            else if (shape.IsReceipt && !legacy.Contains(invoice.UUID.ToString())) actions[invoice.Id] = QuickBooksPushAction.None;
        }

        return actions;
    }

    /// <summary>
    /// Does <paramref name="action"/> for one invoice (lines loaded) straight at the QuickBooks gateway — the pull-back.
    /// False when nothing was sent — no action, or the gateway failed, which is logged, never thrown.
    /// </summary>
    internal async Task<bool> SendAsync(SalesInvoice invoice, QuickBooksPushAction action, CancellationToken ct = default)
    {
        if (action == QuickBooksPushAction.None) return false;

        // A40 (TFIN, D-14) — an imported opening balance is never sent: QuickBooks (the GL) already holds the organization's
        // opening receivables, and its non-stock line names no item a QuickBooks invoice line could carry.
        if (invoice.SourceType == SalesInvoiceSources.Opening) return false;

        var externalId = invoice.UUID.ToString();

        try
        {
            var result = action == QuickBooksPushAction.Void
                ? await _gateway.VoidSalesInvoiceAsync(externalId, ct)
                : await _gateway.UpsertSalesInvoiceAsync(SalesInvoicePayloadFactory.Build(invoice), ct);

            QuickBooksSupport.LogResult(_log, result, SyncKind.SalesInvoice, externalId, invoice.InvoiceNumber);
            return true;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Sales invoice {InvoiceUuid} ({InvoiceNumber}) could not be handed to the QuickBooks gateway ({Action}); " +
                "the hourly reconciliation will offer it again.",
                invoice.UUID, invoice.InvoiceNumber, action);
            return false;
        }
    }

    /// <summary>
    /// A44 (D-25) — the service's change, after its commit: a posting of the issued invoice (GL lines + the same payload)
    /// or a void of it, through the posting gateway; without a posting layer, <see cref="SendAsync"/>. Never throws.
    /// </summary>
    internal async Task<bool> PublishAsync(SalesInvoice invoice, QuickBooksPushAction action, CancellationToken ct = default)
    {
        if (!PostingSupport.IsActive(_posting)) return await SendAsync(invoice, action, ct);

        if (action == QuickBooksPushAction.None) return false;
        if (invoice.SourceType == SalesInvoiceSources.Opening) return false;

        try
        {
            if (action == QuickBooksPushAction.Void)
                await PostingSupport.VoidAsync(_posting, _gateway,
                    SalesPostings.Void(invoice, FinDocType.SalesInvoice, invoice.CancellationReason ?? "Cancelled", invoice.CancelledBy, invoice.CancelledAt),
                    _log, ct);
            else
                await PostingSupport.SendAsync(_posting, _gateway, SalesPostings.Invoice(invoice), _log, ct);
            return true;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Sales invoice {InvoiceUuid} ({InvoiceNumber}) could not be handed to the posting gateway ({Action}); " +
                "the hourly reconciliation will offer it to QuickBooks again.",
                invoice.UUID, invoice.InvoiceNumber, action);
            return false;
        }
    }

    /// <summary>
    /// The current organization's invoices, limited <b>explicitly</b>: "Sync all" / "Push now" call this
    /// source inside a super admin's request, which bypasses the tenant query filter — without this, every
    /// organization's invoices would be handed to the caller's QuickBooks company (security audit Q1).
    /// </summary>
    private IQueryable<SalesInvoice> OwnInvoices()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.SalesInvoices.Where(i => i.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.SalesInvoice)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends sales invoices only.");
    }
}
