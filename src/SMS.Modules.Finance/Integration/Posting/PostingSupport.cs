using Microsoft.Extensions.Logging;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-25) — how this module hands a document to the financial posting gateway, in one place.
/// <para>
/// <b>Two routes.</b> With the posting layer in the host (SMS.Modules.Accounting's gateway), every document goes as a
/// <see cref="FinancialTransaction"/> to <see cref="IFinancialPostingGateway"/>, which sends it to every active target
/// (the internal GL, QuickBooks). Without it — <see cref="NullFinancialPostingGateway"/>, or a hand-built rig that
/// passes none — the document's payload goes straight to <see cref="IQuickBooksGateway"/>, exactly as before A44, so a
/// host without Accounting loses nothing (D-70).
/// </para>
/// </summary>
internal static class PostingSupport
{
    /// <summary>True when a real posting layer is in the host.</summary>
    public static bool IsActive(IFinancialPostingGateway? gateway) => gateway is not null and not NullFinancialPostingGateway;

    /// <summary>The QuickBooks kind a posted document type is stored under (D-72; the QuickBooks target's mapping).</summary>
    public static SyncKind KindOf(FinDocType type) => type switch
    {
        FinDocType.SalesInvoice  => SyncKind.SalesInvoice,
        FinDocType.SalesReceipt  => SyncKind.SalesReceipt,
        FinDocType.CreditNote    => SyncKind.CreditMemo,
        FinDocType.RefundReceipt => SyncKind.RefundReceipt,
        FinDocType.Payment       => SyncKind.Payment,
        FinDocType.PurchaseBill  => SyncKind.Bill,
        FinDocType.BillPayment   => SyncKind.BillPayment,
        FinDocType.VendorCredit  => SyncKind.VendorCredit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "This document type is not sent to QuickBooks.")
    };

    /// <summary>The direct route: the payload to the QuickBooks gateway's upsert of its kind.</summary>
    public static Task<GatewayResult> UpsertDirectAsync(IQuickBooksGateway gateway, IAccountingDocument document, CancellationToken ct) =>
        document switch
        {
            SalesInvoicePayload p  => gateway.UpsertSalesInvoiceAsync(p, ct),
            BillPayload p          => gateway.UpsertBillAsync(p, ct),
            PaymentPayload p       => gateway.UpsertPaymentAsync(p, ct),
            BillPaymentPayload p   => gateway.UpsertBillPaymentAsync(p, ct),
            SalesReceiptPayload p  => gateway.UpsertSalesReceiptAsync(p, ct),
            CreditMemoPayload p    => gateway.UpsertCreditMemoAsync(p, ct),
            RefundReceiptPayload p => gateway.UpsertRefundReceiptAsync(p, ct),
            VendorCreditPayload p  => gateway.UpsertVendorCreditAsync(p, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(document), document.GetType().Name, "Not a document payload.")
        };

    /// <summary>The direct route's void: invoices through their own call (as before A44), every other kind through VoidAsync.</summary>
    public static Task<GatewayResult> VoidDirectAsync(IQuickBooksGateway gateway, SyncKind kind, string externalId, CancellationToken ct) =>
        kind == SyncKind.SalesInvoice
            ? gateway.VoidSalesInvoiceAsync(externalId, ct)
            : gateway.VoidAsync(kind, externalId, ct);

    /// <summary>
    /// Sends one posting by whichever route the host has. Throws whatever the gateway throws — the caller logs it
    /// with the document's id; the posting gateway itself never throws for a target failure.
    /// </summary>
    public static async Task SendAsync(
        IFinancialPostingGateway? posting, IQuickBooksGateway quickBooks, FinancialTransaction transaction, ILogger log, CancellationToken ct)
    {
        if (IsActive(posting))
        {
            var result = transaction.DocumentType is FinDocType.CreditNote or FinDocType.RefundReceipt or FinDocType.VendorCredit
                ? await posting!.IssueCreditAsync(transaction, ct)
                : await posting!.PostAsync(transaction, ct);
            LogResult(log, result, transaction.DocumentType, transaction.SourceDocumentId, transaction.SourceDocumentNumber);
            return;
        }

        var document = transaction.Metadata?.Document
            ?? throw new InvalidOperationException($"{transaction.DocumentType} {transaction.SourceDocumentNumber} carries no document.");
        var kind = KindOf(transaction.DocumentType);
        var direct = await UpsertDirectAsync(quickBooks, document, ct);
        QuickBooksSupport.LogResult(log, direct, kind, document.ExternalId, transaction.SourceDocumentNumber);
    }

    /// <summary>A void (cancel, bounce, POS void) by whichever route the host has. Throws like <see cref="SendAsync"/>.</summary>
    public static async Task VoidAsync(
        IFinancialPostingGateway? posting, IQuickBooksGateway quickBooks, VoidRequest request, ILogger log, CancellationToken ct)
    {
        if (IsActive(posting))
        {
            var result = await posting!.VoidAsync(request, ct);
            LogResult(log, result, request.DocumentType, request.SourceDocumentId, request.SourceDocumentNumber);
            return;
        }

        var kind = KindOf(request.DocumentType);
        var direct = await VoidDirectAsync(quickBooks, kind, request.SourceDocumentId.ToString(), ct);
        QuickBooksSupport.LogResult(log, direct, kind, request.SourceDocumentId.ToString(), request.SourceDocumentNumber);
    }

    /// <summary>
    /// What the gateway made of a posting. A refusal (ValidationFailed) or a target that failed is logged at
    /// Information — the posting log and the QuickBooks dashboard are where it is shown and retried — everything else at Debug.
    /// </summary>
    public static void LogResult(ILogger log, PostingResult? result, FinDocType type, Guid documentId, string? label)
    {
        if (result is null)
        {
            log.LogDebug("Posting gateway returned nothing for {DocumentType} {Label} ({DocumentId}).", type, label, documentId);
            return;
        }

        var failures = (result.TargetResults ?? [])
            .Where(t => t.Status == TargetPostingStatus.Failed)
            .Select(t => $"{t.TargetCode}: {t.Error}")
            .ToList();

        if (result.Status is PostingStatus.ValidationFailed or PostingStatus.PartialFailure || failures.Count > 0)
        {
            log.LogInformation(
                "Posting gateway: {DocumentType} {Label} ({DocumentId}) -> {Status}. {Message} {Failures}",
                type, label, documentId, result.Status, result.Message, string.Join("; ", failures));
            return;
        }

        log.LogDebug(
            "Posting gateway: {DocumentType} {Label} ({DocumentId}) -> {Status} [{Targets}].",
            type, label, documentId, result.Status,
            string.Join(", ", (result.TargetResults ?? []).Select(t => $"{t.TargetCode}={t.Status}")));
    }

    /// <summary>The ISO code, trimmed and upper-cased; empty when there is none.</summary>
    public static string Currency(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;
}
