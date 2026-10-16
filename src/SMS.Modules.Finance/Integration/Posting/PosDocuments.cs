using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>What a POS credit note (return) goes as (D-23).</summary>
internal enum PosCreditKind
{
    /// <summary>Refunded in full in cash-like tenders → RefundReceipt.</summary>
    RefundReceipt,
    /// <summary>On account / store credit / exchange only → CreditMemo.</summary>
    CreditMemo,
    /// <summary>Partly cash-like, partly not → CreditMemo, flagged for the accountant with the cash refunds.</summary>
    Mixed
}

/// <summary>A POS sale as the posting layer sees it.</summary>
internal sealed record PosSaleShape(bool IsReceipt, bool IsVoided, IReadOnlyList<PosTender> Tenders);

/// <summary>A POS tender payment allocated to its sale (or a refund paid against a credit note).</summary>
internal sealed record PosTender(Guid Uuid, string Number, string Method, decimal Amount);

/// <summary>
/// A44 (D-21..D-23, D-73) — the rules that decide which accounting document a POS sale or return becomes, from what
/// SCM stores (no new columns): a sale whose tender payments are all cash-like and cover its total is a sales receipt;
/// anything else (an ON_ACCOUNT part, store credit, exchange value) is an invoice with its tenders as payments. A void
/// is the credit note CounterSaleService writes with the source number "VOID …".
/// </summary>
internal static class PosDocuments
{
    /// <summary>The source-number prefix of the credit note a POS void writes (CounterSaleService.VoidSaleAsync).</summary>
    public const string VoidPrefix = "VOID ";

    /// <summary>Methods that move money (D-19): everything but store credit and exchange value (and ON_ACCOUNT, which records no payment).</summary>
    private static readonly HashSet<string> CashLikeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        CustomerPaymentMethods.Cash, CustomerPaymentMethods.Cheque, CustomerPaymentMethods.BankTransfer,
        CustomerPaymentMethods.Card, CustomerPaymentMethods.Online, CustomerPaymentMethods.Mobile
    };

    public static bool IsCashLike(string? method) => method is not null && CashLikeMethods.Contains(method.Trim());

    public static bool IsPos(SalesInvoice document) => document.SourceType == SalesInvoiceSources.Pos;

    /// <summary>Hold markers the QuickBooks gateway puts on a held map (QBO's PosSales.HeldCode / SyncHolds.HeldCode).</summary>
    private static readonly HashSet<string> HeldCodes = new(StringComparer.OrdinalIgnoreCase) { "POS_SALES_HELD", "HELD" };

    /// <summary>
    /// REV-11 / REV-20 — whether the QuickBooks gateway's SalesInvoice map of a POS sale really carries an invoice from
    /// before A44. It does if it reached QuickBooks (RemoteId), is anywhere past NotSynced (queued, sent, failed,
    /// waiting…), or is a held pre-A44 POS sale. An empty placeholder is not one: NotSynced, no remote id, no hold marker,
    /// as "Send to QuickBooks" creates for any id. The sale then stays a sales receipt.
    /// </summary>
    public static bool IsLegacyInvoice(SMS.Shared.Integration.QuickBooks.SyncStatus status) =>
        status.RemoteId is not null
     || status.State != SMS.Shared.Integration.QuickBooks.SyncState.NotSynced
     || (status.LastErrorCode is { } code && HeldCodes.Contains(code));

    /// <summary>Of these receipt-shaped POS sales, the uuids the QuickBooks gateway holds as pre-A44 invoices (<see cref="IsLegacyInvoice"/>).</summary>
    public static async Task<HashSet<string>> LegacyInvoicesAsync(
        SMS.Shared.Integration.QuickBooks.IQuickBooksGateway gateway, IReadOnlyCollection<SalesInvoice> sales, CancellationToken ct = default) =>
        sales.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : (await gateway.GetStatusAsync(SMS.Shared.Integration.QuickBooks.SyncKind.SalesInvoice, sales.Select(s => s.UUID.ToString()).ToList(), ct))
                .Where(IsLegacyInvoice).Select(s => s.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsVoidCreditNote(SalesInvoice creditNote) =>
        creditNote.Status == SalesInvoiceStatuses.CreditNote
     && (creditNote.SourceNumber?.StartsWith(VoidPrefix, StringComparison.Ordinal) ?? false);

    /// <summary>D-21: every tender cash-like and together the whole sale.</summary>
    public static bool SaleIsReceipt(decimal grandTotal, IReadOnlyCollection<PosTender> tenders) =>
        grandTotal > 0m && tenders.Count > 0 && tenders.All(t => IsCashLike(t.Method)) && tenders.Sum(t => t.Amount) == grandTotal;

    /// <summary>D-23, from the credit note's total and the refunds paid against it (ON_ACCOUNT records no refund).</summary>
    public static PosCreditKind ClassifyCredit(decimal total, IReadOnlyCollection<PosTender> refunds)
    {
        var cash = refunds.Where(r => IsCashLike(r.Method)).Sum(r => r.Amount);
        if (total > 0m && cash == total) return PosCreditKind.RefundReceipt;
        return cash == 0m ? PosCreditKind.CreditMemo : PosCreditKind.Mixed;
    }

    /// <summary>The largest tender's method (the first on a tie) — what decides a receipt's deposit / refund account.</summary>
    public static string MainMethod(IReadOnlyCollection<PosTender> tenders) =>
        tenders.OrderByDescending(t => t.Amount).Select(t => t.Method).FirstOrDefault() ?? CustomerPaymentMethods.Cash;

    /// <summary>
    /// For POS sale invoices (not credit notes): their tender payments (POS payments — SourceUuid set — allocated to
    /// them, whatever the payment's status: a bounced cheque keeps its allocation) and whether they were voided. Two
    /// queries for the lot, scoped explicitly to each invoice's organization.
    /// </summary>
    public static async Task<Dictionary<int, PosSaleShape>> ShapeSalesAsync(
        FinanceDbContext db, IReadOnlyCollection<SalesInvoice> invoices, CancellationToken ct = default)
    {
        var sales = invoices.Where(i => IsPos(i) && i.Status != SalesInvoiceStatuses.CreditNote).ToList();
        if (sales.Count == 0) return [];

        var orgs  = sales.Select(i => i.OrganizationId).Distinct().ToList();
        var ids   = sales.Select(i => i.Id).ToList();
        var uuids = sales.Select(i => (Guid?)i.UUID).ToList();

        var tenders = await db.PaymentAllocations.IgnoreQueryFilters().AsNoTracking()
            .Where(a => orgs.Contains(a.OrganizationId) && ids.Contains(a.SalesInvoiceId) && a.CustomerPayment.SourceUuid != null)
            .Select(a => new { a.SalesInvoiceId, a.CustomerPayment.UUID, a.CustomerPayment.PaymentNumber, a.CustomerPayment.PaymentMethod, a.AllocatedAmount, a.CustomerPayment.Id })
            .ToListAsync(ct);

        var voided = await db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(c => orgs.Contains(c.OrganizationId) && uuids.Contains(c.OriginalInvoiceUuid) && !c.IsDelete
                     && c.Status == SalesInvoiceStatuses.CreditNote && c.SourceNumber != null && c.SourceNumber.StartsWith(VoidPrefix))
            .Select(c => c.OriginalInvoiceUuid!.Value)
            .ToListAsync(ct);
        var voidedSet = voided.ToHashSet();

        return sales.ToDictionary(i => i.Id, i =>
        {
            var own = tenders.Where(t => t.SalesInvoiceId == i.Id).OrderBy(t => t.Id)
                .Select(t => new PosTender(t.UUID, t.PaymentNumber, t.PaymentMethod, t.AllocatedAmount)).ToList();
            var isVoided = voidedSet.Contains(i.UUID);
            return new PosSaleShape(!isVoided && SaleIsReceipt(i.GrandTotal, own), isVoided, own);
        });
    }

    /// <summary>The refunds paid against these credit notes, by credit note uuid (scoped explicitly to their organizations).</summary>
    public static async Task<Dictionary<Guid, List<PosTender>>> RefundsAsync(
        FinanceDbContext db, IReadOnlyCollection<SalesInvoice> creditNotes, CancellationToken ct = default)
    {
        if (creditNotes.Count == 0) return [];
        var orgs  = creditNotes.Select(c => c.OrganizationId).Distinct().ToList();
        var uuids = creditNotes.Select(c => c.UUID).ToList();

        var rows = await db.CustomerRefunds.IgnoreQueryFilters().AsNoTracking()
            .Where(r => orgs.Contains(r.OrganizationId) && uuids.Contains(r.CreditNoteUuid))
            .OrderBy(r => r.Id)
            .Select(r => new { r.CreditNoteUuid, r.UUID, r.RefundNumber, r.Method, r.Amount })
            .ToListAsync(ct);

        return uuids.ToDictionary(u => u, u => rows.Where(r => r.CreditNoteUuid == u)
            .Select(r => new PosTender(r.UUID, r.RefundNumber, r.Method, r.Amount)).ToList());
    }
}
