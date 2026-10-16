using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// Approved supplier invoices, for the QuickBooks gateway, as bills. Called <b>by</b> the gateway — from
/// background jobs, with the tenant set through <c>HangfireTenantScope</c> — for "Push now" and the hourly
/// reconciliation; the invoice service's approvals and edits go through <see cref="SendApprovedAsync"/>.
/// Every batch's purchase order lines are resolved to variants in one query.
/// </summary>
internal sealed class BillQuickBooksSource : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.Bill];

    private readonly FinanceDbContext              _db;
    private readonly IPurchaseOrderLineVariants    _poLines;
    private readonly IQuickBooksGateway            _gateway;
    private readonly ILogger<BillQuickBooksSource> _log;
    private readonly IFinancialPostingGateway?     _posting;

    /// <param name="posting">A44 — what approvals and edits go through (<see cref="PublishApprovedAsync"/>); the pull-back stays direct (D-3).</param>
    public BillQuickBooksSource(
        FinanceDbContext db, IPurchaseOrderLineVariants poLines, IQuickBooksGateway gateway,
        ILogger<BillQuickBooksSource> log, IFinancialPostingGateway? posting = null)
    {
        _db      = db;
        _poLines = poLines;
        _gateway = gateway;
        _log     = log;
        _posting = posting;
    }

    /// <summary>Whether a posting layer is in the host (the publisher reverses through it).</summary>
    internal IFinancialPostingGateway? Posting => PostingSupport.IsActive(_posting) ? _posting : null;

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    /// <summary>
    /// Sends the supplier invoices asked for, if they are approved — one that is not has not been booked
    /// and does not belong in QuickBooks yet. Unknown, malformed and deleted ids are skipped.
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

        foreach (var notApproved in invoices.Where(i => !BillPayloadFactory.IsApproved(i)))
            _log.LogInformation(
                "Supplier invoice {InvoiceUuid} ({InvoiceNumber}) is {MatchStatus}, not Approved, so it is not sent to QuickBooks.",
                notApproved.UUID, notApproved.InvoiceNumber, notApproved.MatchStatus);

        await SendApprovedAsync(invoices.Where(BillPayloadFactory.IsApproved).ToList(), ct);
    }

    /// <summary>
    /// Every approved supplier invoice created, modified or approved since <paramref name="changedSince"/>.
    /// Loaded <see cref="QuickBooksSupport.BatchSize"/> at a time by id, lines and all.
    /// </summary>
    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnInvoices().AsNoTracking()
            .Where(i => !i.IsDelete && i.MatchStatus == BillPayloadFactory.ApprovedMatchStatus);

        if (changedSince is { } since)
            query = query.Where(i => i.CreatedDate >= since || i.ModifiedDate >= since || i.ApprovedAt >= since);

        var sent   = 0;
        var lastId = 0;

        while (true)
        {
            var batch = await query.Where(i => i.Id > lastId)
                .OrderBy(i => i.Id)
                .Take(QuickBooksSupport.BatchSize)
                .Include(i => i.Lines)
                .ToListAsync(ct);

            sent += await SendApprovedAsync(batch, ct);

            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }

        return sent;
    }

    /// <summary>
    /// Builds and sends each invoice (lines loaded; callers pass approved ones), asking Demand about all
    /// their purchase order lines in a single query. Returns how many reached the gateway.
    /// </summary>
    internal Task<int> SendApprovedAsync(IReadOnlyList<Invoice> invoices, CancellationToken ct = default) =>
        SendApprovedAsync(invoices, publish: false, ct);

    /// <summary>
    /// A44 (D-25) — the invoice service's approval or edit, after its commit: each bill as a posting (GL lines + the
    /// same payload) to the financial posting gateway; without a posting layer, straight to QuickBooks as before.
    /// </summary>
    internal Task<int> PublishApprovedAsync(IReadOnlyList<Invoice> invoices, CancellationToken ct = default) =>
        SendApprovedAsync(invoices, publish: PostingSupport.IsActive(_posting), ct);

    private async Task<int> SendApprovedAsync(IReadOnlyList<Invoice> invoices, bool publish, CancellationToken ct)
    {
        // A40 (TFIN, D-14) — imported opening bills are never sent: QuickBooks (the GL) already holds the opening payables,
        // and a header-only bill would land on the default expense account.
        invoices = invoices.Where(i => !string.Equals(i.SourceType, SalesInvoiceSources.Opening, StringComparison.Ordinal)).ToList();
        if (invoices.Count == 0) return 0;

        var variants = await _poLines.GetAsync(BillPayloadFactory.PoLinesOf(invoices), ct);

        var sent = 0;
        foreach (var invoice in invoices)
        {
            var externalId = invoice.UUID.ToString();
            try
            {
                var payload = BillPayloadFactory.Build(invoice, variants);
                if (publish)
                {
                    await PostingSupport.SendAsync(_posting, _gateway, PurchasePostings.Bill(invoice, payload), _log, ct);
                }
                else
                {
                    var result = await _gateway.UpsertBillAsync(payload, ct);
                    QuickBooksSupport.LogResult(_log, result, SyncKind.Bill, externalId, invoice.InvoiceNumber);
                }
                sent++;
            }
            catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
            {
                _log.LogWarning(ex,
                    "Supplier invoice {InvoiceUuid} ({InvoiceNumber}) could not be handed to the QuickBooks gateway; " +
                    "the hourly reconciliation will offer it again.",
                    invoice.UUID, invoice.InvoiceNumber);
            }
        }

        return sent;
    }

    /// <summary>
    /// The current organization's supplier invoices, limited <b>explicitly</b>: "Sync all" / "Push now" call
    /// this source inside a super admin's request, which bypasses the tenant query filter — without this,
    /// every organization's bills would be handed to the caller's QuickBooks company (security audit Q1).
    /// </summary>
    private IQueryable<Invoice> OwnInvoices()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.Invoices.Where(i => i.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.Bill)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends bills only.");
    }
}
