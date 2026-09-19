using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// Raises a payable for something outside the purchase-order flow — decision G10's Finance half.
/// <para>
/// <b>It goes through <see cref="IInvoiceRepository.CreateAsync"/> like everything else.</b> A
/// parallel insert would have avoided making the purchase order optional, and would have produced a
/// payable that the ageing report, the matching screen, the supplier ledger and the payment run each
/// had to learn about separately. One kind of payable is the point.
/// </para>
/// </summary>
internal sealed class SupplierInvoicePoster : ISupplierInvoicePoster
{
    private readonly FinanceDbContext   _db;
    private readonly IInvoiceRepository _invoices;

    public SupplierInvoicePoster(FinanceDbContext db, IInvoiceRepository invoices)
    {
        _db       = db;
        _invoices = invoices;
    }

    public async Task<SupplierInvoicePostingResult> PostAsync(
        SupplierInvoicePosting posting, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posting);

        if (string.IsNullOrWhiteSpace(posting.SourceType) || posting.SourceUuid == Guid.Empty)
            throw new BadRequestException(
                "A payable raised from another module must say what produced it. Without that, a "
              + "retry cannot tell whether it has already been posted.");

        // Asked first, so a retried job returns the invoice it already raised instead of a second
        // one. The unique index behind it is what makes this safe under a race rather than merely
        // usually right.
        var existing = await _db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.SourceType == posting.SourceType
                                   && i.SourceUuid == posting.SourceUuid
                                   && !i.IsDelete, ct);

        if (existing is not null)
            return new SupplierInvoicePostingResult(existing.UUID, existing.InvoiceNumber, true);

        if (posting.Lines is { Count: > 0 })
        {
            var sum = posting.Lines.Sum(l => l.Amount);

            if (Math.Abs(sum - posting.Subtotal) > 0.01m)
                throw new BadRequestException(
                    $"The lines come to {sum:F2} and the invoice says {posting.Subtotal:F2}. "
                  + "Posting them apart would hide the difference in a payable nobody reconciles.");
        }

        var uuid = await _invoices.CreateAsync(new CreateInvoiceRequest
        {
            SupplierId        = posting.SupplierId,
            SupplierInvoiceNo = posting.SupplierInvoiceNo,
            // No purchase order, and none invented. This is the whole reason PoUuid became nullable.
            PoUuid            = null,
            InvoiceDate       = posting.InvoiceDate,
            ReceivedDate      = DateTime.UtcNow.Date,
            DueDate           = posting.DueDate,
            Currency          = posting.Currency,
            Subtotal          = posting.Subtotal,
            TaxAmount         = posting.TaxAmount,
            Notes             = posting.Notes,
            Lines = posting.Lines?.Select(l => new InvoiceLineRequest
            {
                // No PO line either — a freight line charges for carriage, not for an ordered item.
                PoLineUuid      = null,
                ItemDescription = l.Description,
                QtyInvoiced     = 1m,
                UnitPrice       = l.Amount
            }).ToList()
        }, createdBy: userId);

        // Stamped after creation rather than threaded through CreateInvoiceRequest: this is how a
        // payable got here, not something anybody keying one in should be able to set.
        var invoice = await _db.Invoices.FirstAsync(i => i.UUID == uuid, ct);

        invoice.SourceType = posting.SourceType;
        invoice.SourceUuid = posting.SourceUuid;

        await _db.SaveChangesAsync(ct);

        return new SupplierInvoicePostingResult(invoice.UUID, invoice.InvoiceNumber, false);
    }
}
