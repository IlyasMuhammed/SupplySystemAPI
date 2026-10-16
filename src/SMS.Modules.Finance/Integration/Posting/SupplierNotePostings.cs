using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-24, D-75) — supplier credit notes (CN, on create) and debit notes we issue (DN, created ISSUED) as vendor
/// credits, and the QuickBooks source of kind VendorCredit (the external id is the note's uuid, CN or DN).
/// A debit note WRITTEN_OFF after it was sent is reversed (GL reversal + QuickBooks flag for the accountant); DISPUTED
/// posts nothing (a dispute moves nothing in SCM's ledger, and it may still be SETTLED — D-75).
/// <para>
/// Notes carry no currency of their own (D-75): the invoice they name gives it (with its rate snapshot); a note with no
/// invoice is in the organization's purchase base.
/// </para>
/// </summary>
internal sealed class SupplierNotePostings : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.VendorCredit];

    /// <summary>Debit note statuses that are still a claim on the supplier (what the pull-back sends).</summary>
    private static readonly string[] LiveDebitStatuses = ["ISSUED", "ACKNOWLEDGED", "DISPUTED", "SETTLED"];

    private readonly FinanceDbContext              _db;
    private readonly IQuickBooksGateway            _gateway;
    private readonly ILogger<SupplierNotePostings> _log;
    private readonly IFinancialPostingGateway?     _posting;
    private readonly ICurrencyService?             _currency;
    private readonly ILookupsService?              _lookups;

    public SupplierNotePostings(
        FinanceDbContext db, IQuickBooksGateway gateway, ILogger<SupplierNotePostings> log,
        IFinancialPostingGateway? posting = null, ICurrencyService? currency = null, ILookupsService? lookups = null)
    {
        _db       = db;
        _gateway  = gateway;
        _log      = log;
        _posting  = posting;
        _currency = currency;
        _lookups  = lookups;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    // ── After the note services' commits ─────────────────────────────────────

    public Task PublishCreditNoteAsync(Guid uuid, CancellationToken ct = default) => PublishAsync(uuid, ct);

    public Task PublishDebitNoteAsync(Guid uuid, CancellationToken ct = default) => PublishAsync(uuid, ct);

    /// <summary>After a debit note's status change. WRITTEN_OFF → reversal (flag); anything else → nothing. Never throws.</summary>
    public async Task PublishDebitNoteStatusAsync(Guid uuid, string newStatus, int userId, CancellationToken ct = default)
    {
        if (!string.Equals(newStatus, "WRITTEN_OFF", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var note = await _db.DebitNotes.AsNoTracking().FirstOrDefaultAsync(n => n.UUID == uuid, ct);
            if (note is null) return;

            var reason = $"Debit note {note.DebitNoteNumber} was written off in SCM on {DateTime.UtcNow:dd MMM yyyy}"
                       + (string.IsNullOrWhiteSpace(note.Notes) ? "" : $" ({note.Notes.Trim()})")
                       + ". Void or adjust its vendor credit in QuickBooks by hand, then mark this item resolved.";

            if (PostingSupport.IsActive(_posting))
                PostingSupport.LogResult(_log, await _posting!.ReverseAsync(PurchasePostings.ReverseVendorCredit(note, reason, userId), ct),
                    FinDocType.VendorCredit, note.UUID, note.DebitNoteNumber);
            else
                QuickBooksSupport.LogResult(_log, await _gateway.FlagForAccountantAsync(SyncKind.VendorCredit, note.UUID.ToString(), reason, ct),
                    SyncKind.VendorCredit, note.UUID.ToString(), note.DebitNoteNumber);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex, "Debit note {NoteUuid} was written off, but the posting gateway could not be told. Adjust its vendor credit in QuickBooks by hand.", uuid);
        }
    }

    private async Task PublishAsync(Guid uuid, CancellationToken ct)
    {
        try
        {
            foreach (var tx in await PostingsAsync([uuid], own: false, ct))
                await PostingSupport.SendAsync(_posting, _gateway, tx, _log, ct);
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Supplier note {NoteUuid} was saved, but could not be handed to the posting gateway; the hourly reconciliation will offer it to QuickBooks again.",
                uuid);
        }
    }

    // ── Pull-back (direct, D-3) ──────────────────────────────────────────────

    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);
        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;
        await DirectAsync(await PostingsAsync(ids, own: true, ct), ct);
    }

    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);
        var org = _db.TenantContext.OrganizationId;

        var credits = _db.CreditNotes.AsNoTracking().Where(n => n.OrganizationId == org && n.IsActive && !n.IsDelete);
        var debits  = _db.DebitNotes.AsNoTracking().Where(n => n.OrganizationId == org && n.IsActive && !n.IsDelete);
        if (changedSince is { } since)
        {
            credits = credits.Where(n => n.CreatedDate >= since || n.ModifiedDate >= since);
            debits  = debits.Where(n => n.CreatedDate >= since || n.ModifiedDate >= since);
        }

        var ids = await credits.Select(n => n.UUID).ToListAsync(ct);
        ids.AddRange(await debits.Select(n => n.UUID).ToListAsync(ct));

        var sent = 0;
        foreach (var chunk in ids.Chunk(QuickBooksSupport.BatchSize))
            sent += await DirectAsync(await PostingsAsync(chunk, own: true, ct), ct);
        return sent;
    }

    private async Task<int> DirectAsync(IReadOnlyList<FinancialTransaction> postings, CancellationToken ct)
    {
        var sent = 0;
        foreach (var tx in postings)
        {
            try
            {
                QuickBooksSupport.LogResult(_log, await _gateway.UpsertVendorCreditAsync((VendorCreditPayload)tx.Metadata!.Document!, ct),
                    SyncKind.VendorCredit, tx.SourceDocumentId.ToString(), tx.SourceDocumentNumber);
                sent++;
            }
            catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
            {
                _log.LogWarning(ex, "Supplier note {NoteUuid} ({Number}) could not be handed to the QuickBooks gateway; the hourly reconciliation will offer it again.",
                    tx.SourceDocumentId, tx.SourceDocumentNumber);
            }
        }
        return sent;
    }

    // ── Building ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The vendor-credit postings of these notes, credit notes and live debit notes alike (DRAFT, WRITTEN_OFF and deleted
    /// ones are left out). <paramref name="own"/>: limited explicitly to the current organization (pull-back, security audit Q1).
    /// </summary>
    private async Task<List<FinancialTransaction>> PostingsAsync(IReadOnlyCollection<Guid> ids, bool own, CancellationToken ct)
    {
        var org = _db.TenantContext.OrganizationId;
        var credits = await _db.CreditNotes.AsNoTracking()
            .Where(n => ids.Contains(n.UUID) && n.IsActive && !n.IsDelete && (!own || n.OrganizationId == org)).ToListAsync(ct);
        var debits = await _db.DebitNotes.AsNoTracking()
            .Where(n => ids.Contains(n.UUID) && n.IsActive && !n.IsDelete && LiveDebitStatuses.Contains(n.Status) && (!own || n.OrganizationId == org))
            .ToListAsync(ct);
        if (credits.Count == 0 && debits.Count == 0) return [];

        var invoiceUuids = credits.Select(n => n.InvoiceUuid).Concat(debits.Select(n => n.InvoiceUuid))
            .Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        var invoices = invoiceUuids.Count == 0 ? [] : await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => invoiceUuids.Contains(i.UUID))
            .Select(i => new { i.UUID, i.OrganizationId, i.Currency, i.ExchangeRate, i.BaseCurrencyCode })
            .ToListAsync(ct);

        var baseCode = invoices.Count == invoiceUuids.Count && credits.All(c => c.InvoiceUuid is not null) && debits.All(d => d.InvoiceUuid is not null)
            ? null
            : await PurchaseBaseAsync(credits.Select(c => c.OrganizationId).Concat(debits.Select(d => d.OrganizationId)).First(), ct);

        NoteCurrency CurrencyOf(Guid? invoice, Guid noteOrg)
        {
            var i = invoice is { } u ? invoices.FirstOrDefault(x => x.UUID == u && x.OrganizationId == noteOrg) : null;
            return i is not null ? new NoteCurrency(i.Currency, i.ExchangeRate, i.BaseCurrencyCode) : new NoteCurrency(baseCode ?? string.Empty, null, null);
        }

        return [.. credits.Select(n => PurchasePostings.VendorCredit(n, CurrencyOf(n.InvoiceUuid, n.OrganizationId))),
                .. debits.Select(n => PurchasePostings.VendorCredit(n, CurrencyOf(n.InvoiceUuid, n.OrganizationId)))];
    }

    /// <summary>The organization's purchase base currency code, when the host can say.</summary>
    private async Task<string?> PurchaseBaseAsync(Guid organizationId, CancellationToken ct)
    {
        if (_currency is null || _lookups is null) return null;
        try
        {
            var id = await _currency.GetBaseCurrencyIdAsync(organizationId, TransactionDomain.Purchase, ct);
            return _lookups.GetCurrencies().FirstOrDefault(c => c.Id == id)?.Code?.Trim();
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogDebug(ex, "The purchase base currency of {Org} could not be read for a supplier note.", organizationId);
            return null;
        }
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.VendorCredit)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This source sends supplier credit and debit notes only.");
    }
}
