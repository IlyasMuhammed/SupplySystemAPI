using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// What the sales invoice service calls once its own save has committed. Decides with
/// <see cref="SalesInvoiceQuickBooksRules"/> whether QuickBooks hears about the change at all, then sends.
/// <para>
/// <b>Never throws.</b> The invoice is issued (or whatever the change was) whatever QuickBooks makes of it;
/// a failure is logged with the invoice's id and the gateway's hourly reconciliation offers it again.
/// </para>
/// </summary>
internal sealed class SalesInvoiceQuickBooksPublisher
{
    private readonly SalesInvoiceQuickBooksSource             _source;
    private readonly ILogger<SalesInvoiceQuickBooksPublisher> _log;

    public SalesInvoiceQuickBooksPublisher(
        SalesInvoiceQuickBooksSource source, ILogger<SalesInvoiceQuickBooksPublisher> log)
    {
        _source = source;
        _log    = log;
    }

    /// <param name="invoice">The invoice as saved, with its lines loaded.</param>
    /// <param name="previousStatus">Its status before the change; null if it did not exist.</param>
    /// <param name="contentChanged">Whether anything QuickBooks is sent changed, beyond the status.</param>
    public async Task OnChangedAsync(SalesInvoice invoice, string? previousStatus, bool contentChanged)
    {
        try
        {
            var action = SalesInvoiceQuickBooksRules.OnChange(previousStatus, invoice.Status, contentChanged);
            await _source.SendAsync(invoice, action);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Sales invoice {InvoiceUuid} ({InvoiceNumber}) was saved, but could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer it again.",
                invoice.UUID, invoice.InvoiceNumber);
        }
    }
}
