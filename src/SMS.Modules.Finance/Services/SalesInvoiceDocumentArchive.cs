using System.Globalization;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A29-P7-09 §9.1 — files a sales invoice's PDF as an attachment on the invoice.
/// <para>
/// <b>Why this is a separate service from the ones it uses.</b> Rendering the PDF needs the invoice
/// (<see cref="ISalesInvoiceService"/>), and issuing an invoice is what triggers filing it; folding
/// the two into the invoice service would make it depend on the thing that depends on it. Here the
/// arrows only point one way, and the invoice service stays about invoices.
/// </para>
/// <para>
/// <b>Filed under <c>SALES_INVOICE</c>,</b> the code an invoice page opens its attachment panel with,
/// and readable only with <c>SALES_INVOICE_VIEW</c>: the same permission that lets a person open the
/// invoice, and so the PDF, in the first place.
/// </para>
/// </summary>
internal sealed class SalesInvoiceDocumentArchive : ISalesInvoiceDocumentArchive
{
    /// <summary>What an invoice's attachment panel is opened with — and what its files are filed under.</summary>
    internal const string InterfaceCode = "SALES_INVOICE";

    private readonly ISalesInvoiceService         _invoices;
    private readonly ISalesInvoiceDocumentService _documents;
    private readonly IAttachmentService           _attachments;
    private readonly ILogger<SalesInvoiceDocumentArchive> _log;

    public SalesInvoiceDocumentArchive(
        ISalesInvoiceService invoices, ISalesInvoiceDocumentService documents,
        IAttachmentService attachments, ILogger<SalesInvoiceDocumentArchive> log)
    {
        _invoices    = invoices;
        _documents   = documents;
        _attachments = attachments;
        _log         = log;
    }

    public async Task<Guid?> TryFileIssuedPdfAsync(Guid invoiceUuid, int userId)
    {
        try
        {
            var stored = await FileAsync(invoiceUuid, userId, "Filed when the invoice was issued.");
            return stored.Uuid;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Sales invoice {InvoiceUuid} was issued but its PDF could not be filed as an attachment; file it from the invoice.",
                invoiceUuid);
            return null;
        }
    }

    public Task<StoredAttachment> FilePdfAsync(Guid invoiceUuid, int userId) =>
        FileAsync(invoiceUuid, userId, note: null);

    private async Task<StoredAttachment> FileAsync(Guid invoiceUuid, int userId, string? note)
    {
        var invoice = await _invoices.GetAsync(invoiceUuid)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        if (invoice.Status == SalesInvoiceStatuses.Draft)
            throw new BadRequestException(
                $"Sales invoice {invoice.InvoiceNumber} is a DRAFT. Only an invoice that has been issued is kept on file, " +
                "because a draft is not what the customer is sent.");

        var pdf = await _documents.GeneratePdfAsync(invoiceUuid);

        return await _attachments.StoreGeneratedAsync(new GeneratedAttachmentRequest
        {
            InterfaceCode      = InterfaceCode,
            DocumentId         = invoiceUuid,
            FileName           = pdf.FileName,
            ContentType        = "application/pdf",
            Content            = pdf.Content,
            // What the copy shows, so a list of several filings says which is which.
            Notes              = note ?? string.Create(CultureInfo.InvariantCulture,
                $"Filed with the invoice {invoice.Status.Replace('_', ' ').ToLowerInvariant()}; balance due {invoice.BalanceDue:N2} {invoice.CurrencyCode}."),
            RequiredPermission = PermissionCodes.SALES_INVOICE_VIEW
        }, userId);
    }
}
