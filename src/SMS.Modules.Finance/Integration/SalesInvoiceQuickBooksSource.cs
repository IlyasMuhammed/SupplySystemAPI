using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// Sales invoices, for the QuickBooks gateway. Called <b>by</b> the gateway — from background jobs, with the
/// tenant set through <c>HangfireTenantScope</c> — for "Push now" and the hourly reconciliation. The sales
/// invoice service's own changes go through <see cref="SendAsync"/> here too, so both paths build an
/// invoice the same way.
/// </summary>
internal sealed class SalesInvoiceQuickBooksSource : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.SalesInvoice];

    private readonly FinanceDbContext                      _db;
    private readonly IQuickBooksGateway                    _gateway;
    private readonly ILogger<SalesInvoiceQuickBooksSource> _log;

    public SalesInvoiceQuickBooksSource(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<SalesInvoiceQuickBooksSource> log)
    {
        _db      = db;
        _gateway = gateway;
        _log     = log;
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

        foreach (var invoice in invoices)
            await SendAsync(invoice, SalesInvoiceQuickBooksRules.ForCurrentState(invoice.Status), ct);
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

            foreach (var invoice in batch)
                if (await SendAsync(invoice, SalesInvoiceQuickBooksRules.ForCurrentState(invoice.Status), ct))
                    sent++;

            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }

        return sent;
    }

    /// <summary>
    /// Does <paramref name="action"/> for one invoice (lines loaded). False when nothing was sent — no
    /// action, or the gateway failed, which is logged, never thrown.
    /// </summary>
    internal async Task<bool> SendAsync(SalesInvoice invoice, QuickBooksPushAction action, CancellationToken ct = default)
    {
        if (action == QuickBooksPushAction.None) return false;

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
