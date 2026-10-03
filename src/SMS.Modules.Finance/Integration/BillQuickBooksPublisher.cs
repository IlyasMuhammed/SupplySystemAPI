using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// What the supplier invoice service calls once its own save has committed: re-reads the invoice and, if
/// it is approved, hands it to the QuickBooks gateway as a bill.
/// <para>
/// <b>Never throws.</b> The approval or edit stands whatever QuickBooks makes of it; a failure is logged
/// with the invoice's id and the gateway's hourly reconciliation offers it again.
/// </para>
/// <para>
/// A <b>reversed</b> invoice (S-7) is not approved, so nothing here, in "Push now" or in the hourly
/// reconciliation sends it again. The gateway cannot void a bill, so one already in QuickBooks stays there
/// until the accountant voids it by hand — <see cref="NoteReversedAsync"/> logs which.
/// </para>
/// </summary>
internal sealed class BillQuickBooksPublisher
{
    private readonly FinanceDbContext                 _db;
    private readonly BillQuickBooksSource             _source;
    private readonly ILogger<BillQuickBooksPublisher> _log;
    private readonly IQuickBooksGateway?              _gateway;

    /// <param name="gateway">Asked, after a reversal, whether the bill ever reached QuickBooks. Optional for hand-built rigs.</param>
    public BillQuickBooksPublisher(
        FinanceDbContext db, BillQuickBooksSource source, ILogger<BillQuickBooksPublisher> log,
        IQuickBooksGateway? gateway = null)
    {
        _db      = db;
        _source  = source;
        _log     = log;
        _gateway = gateway;
    }

    /// <summary>
    /// Whether an edit touched anything a bill is built from: the supplier's number, the due date, the tax
    /// (and with it the total), or the match status itself. Payment status, method, notes and the
    /// attachment are not on a bill — and payments are not synced (plan D-8). Since S-7 an approved
    /// invoice's tax and match status cannot be edited at all, so in practice it is the number and due date.
    /// </summary>
    internal static bool TouchesBill(PatchInvoiceRequest req) =>
        req.SupplierInvoiceNo is not null || req.DueDate is not null || req.TaxAmount is not null
     || req.TaxCodeUuid is not null || req.MatchStatus is not null;

    /// <summary>After an approval, or an edit that <see cref="TouchesBill"/>: sends the invoice if it is approved now.</summary>
    public async Task PublishIfApprovedAsync(Guid invoiceUuid)
    {
        try
        {
            var invoice = await _db.Invoices.AsNoTracking()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete);

            if (invoice is null || !BillPayloadFactory.IsApproved(invoice)) return;

            await _source.SendApprovedAsync([invoice]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Supplier invoice {InvoiceUuid} was saved, but could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer it again.",
                invoiceUuid);
        }
    }

    /// <summary>
    /// After a reversal has committed. Sends nothing — the invoice is no longer approved. Flags the bill for the
    /// accountant at the gateway (<see cref="IQuickBooksGateway.FlagForAccountantAsync"/>): anything still queued for
    /// it is dropped; a bill that is (or may be) in QuickBooks shows on the sync dashboard as needing the accountant —
    /// it must be voided there by hand, the gateway has no way to void a bill — and one that never got there is closed.
    /// Also asks whether the bill ever reached QuickBooks, and logs which. Never throws.
    /// </summary>
    /// <param name="reason">Why it was reversed, for the dashboard; optional.</param>
    public async Task NoteReversedAsync(Guid invoiceUuid, string invoiceNumber, string? reason = null)
    {
        try
        {
            var status = _gateway is null
                ? null
                : (await _gateway.GetStatusAsync(SyncKind.Bill, [invoiceUuid.ToString()])).FirstOrDefault();

            if (_gateway is not null)
                await _gateway.FlagForAccountantAsync(SyncKind.Bill, invoiceUuid.ToString(), AccountantNote(invoiceNumber, reason));

            if (status is null)
            {
                _log.LogInformation(
                    "Supplier invoice {InvoiceUuid} ({InvoiceNumber}) was reversed. It was never handed to QuickBooks, so there is nothing to undo there.",
                    invoiceUuid, invoiceNumber);
                return;
            }

            _log.LogWarning(
                "Supplier invoice {InvoiceUuid} ({InvoiceNumber}) was reversed in SCM, but its bill is {SyncState} at the QuickBooks gateway " +
                "(QuickBooks bill {RemoteDocNumber}, id {RemoteId}). The gateway cannot void a bill: void or delete it in QuickBooks by hand. " +
                "SCM will not send it again.",
                invoiceUuid, invoiceNumber, status.State, status.RemoteDocNumber, status.RemoteId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Supplier invoice {InvoiceUuid} ({InvoiceNumber}) was reversed, but the QuickBooks gateway could not say whether its bill was sent. " +
                "If it was, void or delete it in QuickBooks by hand.",
                invoiceUuid, invoiceNumber);
        }
    }

    /// <summary>What the sync dashboard tells the accountant about a reversed bill.</summary>
    internal static string AccountantNote(string invoiceNumber, string? reason) =>
        $"Supplier invoice {invoiceNumber} was reversed in SCM on {DateTime.UtcNow:dd MMM yyyy}"
      + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})")
      + ". Void or delete its bill in QuickBooks by hand, then mark this item resolved.";
}
