using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Shared.Integration.QuickBooks;
using Qbo = Intuit.Ipp.Data;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// A44 (D-19..D-24): what the payment and credit documents need beyond a plain create / update / read / find, which go
/// through the same paths as invoices and bills in the main part.
/// </summary>
internal sealed partial class QuickBooksAccountingProvider
{
    /// <summary>
    /// D-23 ON_ACCOUNT: creates the credit memo, then applies it to its invoice with a zero-total Payment in the same
    /// call. The credit memo is what this call is about: if the application fails, the create still succeeded (its id
    /// is returned, so it is never created twice) and <see cref="RemoteRecord.Note"/> tells the accountant to apply it by
    /// hand. Only creates apply — an update never adds a second application. The amount applied is QuickBooks' own
    /// total of the memo when it sent one (it includes the tax QuickBooks computed), else SCM's.
    /// </summary>
    private Task<ProviderResult<RemoteRecord>> CreateAppliedCreditMemoAsync(
        ProviderContext ctx, RemoteCreditMemo memo, Qbo.IntuitEntity sdk, CancellationToken ct) =>
        ExecuteAsync(ctx, QboOperation.Create, QboJson.Entity(sdk), (client, _) =>
        {
            var saved  = client.Add(QboEntityMapper.AsEntity(sdk));
            var record = QboEntityMapper.ToRecord(SyncKind.CreditMemo, saved);

            string? note = null;
            try
            {
                // SCM's applied amount (may be less than the memo, SRC "AppliedAmount"), never more than QuickBooks' total.
                var amount = record.TotalAmount is { } total && memo.ApplyAmount > 0 ? Math.Min(total, memo.ApplyAmount)
                           : memo.ApplyAmount > 0 ? memo.ApplyAmount : record.TotalAmount ?? 0m;
                if (amount > 0)
                    client.Add(QboPaymentMapper.CreditApplication(memo.CustomerId, memo.ApplyToInvoiceId!, record.RemoteId, amount,
                        memo.TxnDate, memo.CurrencyCode, memo.ExchangeRate, memo.DocNumber));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = _errors.Classify(ex, QboOperation.Create);
                _logger.LogWarning("QuickBooks credit memo {RemoteId} for connection {ConnectionId}: applying it to invoice {InvoiceId} failed ({Outcome} {Code}).",
                    record.RemoteId, ctx.ConnectionId, memo.ApplyToInvoiceId, failure.Outcome, failure.Code);
                note = $"The credit memo was created in QuickBooks, but applying it to its invoice failed ({failure.Code}). " +
                       "Apply it to the invoice in QuickBooks by hand (Receive payment → the invoice and the credit).";
            }

            return Reply<RemoteRecord>.Ok(record with { Note = note }, QboJson.Entity(saved));
        }, ct);

    /// <summary>
    /// A44 D-51 / D-56. Invoice → <see cref="VoidInvoiceAsync"/>. Payment and SalesReceipt → the SDK's void, which for
    /// these two is <c>POST /{entity}?operation=update&amp;include=void</c> with an Id + SyncToken + sparse body (the record
    /// stays, its amounts become zero, "Voided" is noted). BillPayment and RefundReceipt → <c>POST
    /// /{entity}?operation=delete</c>: SDK 14.7.1.6's void has no include=void path for them (QuickBooks' audit log keeps
    /// the deleted transaction). CreditMemo and VendorCredit are never undone here — a credit may already be applied in
    /// QuickBooks — and are refused with <see cref="IAccountingProvider.VoidUnsupportedCode"/>, without a call.
    /// </summary>
    public Task<ProviderResult<RemoteRecord>> VoidAsync(
        ProviderContext ctx, SyncKind kind, string remoteId, string syncToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncToken);

        switch (kind)
        {
            case SyncKind.SalesInvoice:
                return VoidInvoiceAsync(ctx, remoteId, syncToken, ct);

            case SyncKind.Payment:
            case SyncKind.SalesReceipt:
            case SyncKind.BillPayment:
            case SyncKind.RefundReceipt:
            {
                var probe = QboEntityMapper.NewOfKind(kind);
                probe.Id        = remoteId;
                probe.SyncToken = syncToken;
                var delete = kind is SyncKind.BillPayment or SyncKind.RefundReceipt;
                if (!delete)
                {
                    // D-56: the SDK sends the object as given on operation=update&include=void; Intuit's void request
                    // for these is a sparse body. Without the flag it is a full update missing CustomerRef / Line.
                    probe.sparse          = true;
                    probe.sparseSpecified = true;
                }

                return ExecuteAsync(ctx, QboOperation.Void, QboJson.Entity(probe), (client, _) =>
                {
                    var entity = QboEntityMapper.AsEntity(probe);
                    var done   = delete ? client.Delete(entity) : client.Void(entity);
                    // A delete answers with the bare Id and status; whatever comes back, the record is gone or void.
                    var record = done is Qbo.IntuitEntity { Id: { Length: > 0 } } returned
                        ? new RemoteRecord(returned.Id, returned.SyncToken ?? syncToken, null, null, Active: false)
                        : new RemoteRecord(remoteId, syncToken, null, null, Active: false);
                    return Reply<RemoteRecord>.Ok(record, QboJson.Entity(done as Qbo.IntuitEntity));
                }, ct);
            }

            default:
                return Task.FromResult(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.Refused, IAccountingProvider.VoidUnsupportedCode,
                    $"QuickBooks {kind} records are not voided or deleted by the integration; they are left for the accountant."));
        }
    }
}
