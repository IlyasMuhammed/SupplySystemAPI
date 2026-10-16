using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway;

/// <summary>
/// A44 D-2 / D-10: what QuickBooks is to the financial posting gateway (Accounting) — a target among others. Both targets
/// are thin: they hand the transaction's provider-neutral document (D-4) to <see cref="IQuickBooksGateway"/>, which
/// validates, stores and queues it exactly as before A44 (nothing here calls Intuit), and translate its answer.
/// <para>Active = MODULE_INTEGRATION on for the ambient organization and a usable QuickBooks connection.</para>
/// </summary>
internal static class QuickBooksTargetActivity
{
    public static async Task<bool> IsActiveAsync(IServiceProvider services, IConnectionAccessor connections, CancellationToken ct)
    {
        var gate   = services.GetService<IModuleGate>();
        var tenant = services.GetService<ITenantContext>();
        if (gate is not null && tenant is not null && tenant.OrganizationId != Guid.Empty
            && !await gate.IsEnabledAsync(tenant.OrganizationId, IntegrationFeature.Code, ct))
            return false;

        var connection = await connections.GetCurrentAsync(ct);
        return connection is not null && connection.IsUsable();
    }

    /// <summary>The map's state after an upsert said NotSynced: held by sync control, or just not sent (out of scope).</summary>
    public static async Task<SyncStatus?> StatusAsync(IQuickBooksGateway gateway, SyncKind kind, string externalId, CancellationToken ct) =>
        (await gateway.GetStatusAsync(kind, [externalId], ct)).FirstOrDefault();
}

/// <summary>
/// <see cref="IFinancialPostingTarget"/> "quickbooks". Post: the document's runtime type picks the upsert (a cancelled
/// invoice payload is a void, as on the gateway). Void: the kind is found from the SCM document's map — a POS sale may
/// be a SalesReceipt or an Invoice (D-21/D-22) — and voided, deleted or flagged per D-51. Reverse (supplier invoice,
/// D-26): flag for the accountant, never an external transaction. A document never sent is a no-op (BR-25 / T-60).
/// Never throws.
/// </summary>
internal sealed class QuickBooksPostingTarget : IFinancialPostingTarget
{
    private readonly IQuickBooksGateway               _gateway;
    private readonly IConnectionAccessor              _connections;
    private readonly IServiceProvider                 _services;
    private readonly ILogger<QuickBooksPostingTarget> _logger;

    public QuickBooksPostingTarget(IQuickBooksGateway gateway, IConnectionAccessor connections, IServiceProvider services,
                                   ILogger<QuickBooksPostingTarget> logger)
    {
        _gateway     = gateway;
        _connections = connections;
        _services    = services;
        _logger      = logger;
    }

    public string TargetCode => PostingTargetCodes.QuickBooks;

    public Task<bool> IsActiveAsync(CancellationToken ct = default) => QuickBooksTargetActivity.IsActiveAsync(_services, _connections, ct);

    public async Task<TargetResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default)
    {
        try
        {
            var document = transaction.Metadata?.Document;
            if (document is null)
                return TargetResult.Skipped(TargetCode, "NO_DOCUMENT: the transaction carries no document for QuickBooks.");

            ApplyProperties(document, transaction.Metadata?.Properties);
            var (kind, call) = Dispatch(document);
            if (kind is null) return TargetResult.Skipped(TargetCode, $"UNSUPPORTED_DOCUMENT: {document.GetType().Name} is not sent to QuickBooks.");

            return await TranslateAsync(kind.Value, document.ExternalId, await call!(ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "QuickBooks target: posting {DocumentType} {Id} failed.", transaction.DocumentType, transaction.SourceDocumentId);
            return TargetResult.Failed(TargetCode, ex.Message);
        }
    }

    public async Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default)
    {
        try
        {
            var externalId = request.SourceDocumentId.ToString();
            var kind = await FindKindAsync(request.DocumentType, externalId, ct);
            if (kind is null)
                return TargetResult.Skipped(TargetCode, "NEVER_SYNCED: nothing of this document is in QuickBooks.");

            var result = SyncPayloads.IsVoidable(kind.Value)
                ? await _gateway.VoidAsync(kind.Value, externalId, ct)
                : await _gateway.FlagForAccountantAsync(kind.Value, externalId, Reason(request, "cancelled"), ct);
            return await TranslateAsync(kind.Value, externalId, result, ct, isUndo: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "QuickBooks target: voiding {DocumentType} {Id} failed.", request.DocumentType, request.SourceDocumentId);
            return TargetResult.Failed(TargetCode, ex.Message);
        }
    }

    /// <summary>D-26: a supplier invoice reversed after it reached QuickBooks is the accountant's to undo there.</summary>
    public async Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default)
    {
        try
        {
            var externalId = request.SourceDocumentId.ToString();
            var kind = await FindKindAsync(request.DocumentType, externalId, ct);
            if (kind is null)
                return TargetResult.Skipped(TargetCode, "NEVER_SYNCED: nothing of this document is in QuickBooks.");

            var result = await _gateway.FlagForAccountantAsync(kind.Value, externalId, Reason(request, "reversed"), ct);
            return await TranslateAsync(kind.Value, externalId, result, ct, isUndo: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "QuickBooks target: reversing {DocumentType} {Id} failed.", request.DocumentType, request.SourceDocumentId);
            return TargetResult.Failed(TargetCode, ex.Message);
        }
    }

    /// <summary>Property key: reason to flag the document for the accountant once QuickBooks has it (D-23 mixed tenders).</summary>
    public const string FlagForAccountantProperty = "FlagForAccountant";
    /// <summary>Property key: amount of a credit memo applied to its original invoice (invariant decimal).</summary>
    public const string AppliedAmountProperty = "AppliedAmount";

    /// <summary>SRC's posting metadata → the payload's own optional fields (omitted from the stored JSON when absent).</summary>
    internal static void ApplyProperties(IAccountingDocument document, IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null) return;

        if (document is SalesDocumentPayload sales
            && properties.TryGetValue(FlagForAccountantProperty, out var reason) && !string.IsNullOrWhiteSpace(reason))
            sales.FlagForAccountant = reason.Trim();

        if (document is CreditMemoPayload memo
            && properties.TryGetValue(AppliedAmountProperty, out var text)
            && decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var applied)
            && applied > 0)
            memo.AppliedAmount = applied;
    }

    private static string Reason(VoidRequest request, string what)
    {
        var number = string.IsNullOrWhiteSpace(request.SourceDocumentNumber) ? "This document" : request.SourceDocumentNumber.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? string.Empty : $" ({request.Reason.Trim()})";
        return $"{number} was {what} in SCM{reason} after it reached QuickBooks. Void or delete it in QuickBooks, then mark this resolved.";
    }

    private (SyncKind? Kind, Func<CancellationToken, Task<GatewayResult>>? Call) Dispatch(IAccountingDocument document) => document switch
    {
        SalesInvoicePayload s  => (SyncKind.SalesInvoice, ct => _gateway.UpsertSalesInvoiceAsync(s, ct)),
        BillPayload b          => (SyncKind.Bill, ct => _gateway.UpsertBillAsync(b, ct)),
        PaymentPayload p       => (SyncKind.Payment, ct => _gateway.UpsertPaymentAsync(p, ct)),
        BillPaymentPayload bp  => (SyncKind.BillPayment, ct => _gateway.UpsertBillPaymentAsync(bp, ct)),
        SalesReceiptPayload r  => (SyncKind.SalesReceipt, ct => _gateway.UpsertSalesReceiptAsync(r, ct)),
        CreditMemoPayload m    => (SyncKind.CreditMemo, ct => _gateway.UpsertCreditMemoAsync(m, ct)),
        RefundReceiptPayload f => (SyncKind.RefundReceipt, ct => _gateway.UpsertRefundReceiptAsync(f, ct)),
        VendorCreditPayload v  => (SyncKind.VendorCredit, ct => _gateway.UpsertVendorCreditAsync(v, ct)),
        CustomerPayload c      => (SyncKind.Customer, ct => _gateway.UpsertCustomerAsync(c, ct)),
        VendorPayload v        => (SyncKind.Vendor, ct => _gateway.UpsertVendorAsync(v, ct)),
        ItemPayload i          => (SyncKind.Item, ct => _gateway.UpsertItemAsync(i, ct)),
        _                      => (null, null)
    };

    /// <summary>The kinds a posting document type can have been sent as, most specific first.</summary>
    internal static IReadOnlyList<SyncKind> KindsOf(FinDocType type) => type switch
    {
        FinDocType.SalesInvoice  => [SyncKind.SalesInvoice],
        FinDocType.POSSale       => [SyncKind.SalesReceipt, SyncKind.SalesInvoice],
        FinDocType.SalesReceipt  => [SyncKind.SalesReceipt],
        FinDocType.Payment       => [SyncKind.Payment],
        FinDocType.CreditNote    => [SyncKind.CreditMemo, SyncKind.RefundReceipt],
        FinDocType.POSReturn     => [SyncKind.RefundReceipt, SyncKind.CreditMemo],
        FinDocType.RefundReceipt => [SyncKind.RefundReceipt],
        FinDocType.PurchaseBill  => [SyncKind.Bill],
        FinDocType.BillPayment   => [SyncKind.BillPayment],
        FinDocType.VendorCredit  => [SyncKind.VendorCredit],
        _                        => []
    };

    private async Task<SyncKind?> FindKindAsync(FinDocType type, string externalId, CancellationToken ct)
    {
        foreach (var kind in KindsOf(type))
            if (await QuickBooksTargetActivity.StatusAsync(_gateway, kind, externalId, ct) is { State: not SyncState.Voided })
                return kind;
        return null;
    }

    private async Task<TargetResult> TranslateAsync(SyncKind kind, string externalId, GatewayResult result, CancellationToken ct, bool isUndo = false)
    {
        switch (result.Outcome)
        {
            case GatewayOutcome.NotConnected:
                return TargetResult.Skipped(TargetCode, "NOT_CONNECTED: no usable QuickBooks connection.");
            case GatewayOutcome.Disabled:
                return TargetResult.Skipped(TargetCode, "DISABLED: syncing this document type is switched off, or it is out of scope.");
            case GatewayOutcome.Invalid:
                return TargetResult.Failed(TargetCode,
                    "VALIDATION: " + string.Join(" ", result.Errors.Select(e => e.Message)));
            case GatewayOutcome.WaitingOnDependency:
                return Queued(externalId, null);
        }

        var status = await QuickBooksTargetActivity.StatusAsync(_gateway, kind, externalId, ct);
        return result.State switch
        {
            SyncState.Synced          => new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Posted, InternalId = externalId, ExternalId = status?.RemoteId },
            SyncState.Voided          => isUndo
                ? new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Posted, InternalId = externalId, ExternalId = status?.RemoteId }
                : TargetResult.Skipped(TargetCode, "VOIDED: the document is voided; later changes are not sent."),
            SyncState.NeedsResolution => isUndo
                ? new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Posted, InternalId = externalId, ExternalId = status?.RemoteId,
                                     Error = "FLAGGED_FOR_ACCOUNTANT: " + status?.LastError }
                : TargetResult.Skipped(TargetCode, "FLAGGED_FOR_ACCOUNTANT: " + (status?.LastError ?? "a person must resolve it in QuickBooks.")),
            SyncState.NotSynced when SyncHolds.IsHeldCode(status?.LastErrorCode) =>
                new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Held, InternalId = externalId, Error = status?.LastError },
            SyncState.NotSynced => isUndo
                ? new TargetResult { TargetCode = TargetCode, Status = TargetPostingStatus.Posted, InternalId = externalId }
                : Queued(externalId, status?.RemoteId),
            _ => Queued(externalId, status?.RemoteId)
        };
    }

    private TargetResult Queued(string externalId, string? remoteId) =>
        new() { TargetCode = TargetCode, Status = TargetPostingStatus.Queued, InternalId = externalId, ExternalId = remoteId };
}

/// <summary>
/// <see cref="IEntitySyncTarget"/> "quickbooks": customers, vendors and products through the existing upserts. QuickBooks
/// never deletes a name-list record, so Deactivate and Delete both send it inactive. Never throws.
/// </summary>
internal sealed class QuickBooksEntitySyncTarget : IEntitySyncTarget
{
    private readonly IQuickBooksGateway                  _gateway;
    private readonly IConnectionAccessor                 _connections;
    private readonly IServiceProvider                    _services;
    private readonly ILogger<QuickBooksEntitySyncTarget> _logger;

    public QuickBooksEntitySyncTarget(IQuickBooksGateway gateway, IConnectionAccessor connections, IServiceProvider services,
                                      ILogger<QuickBooksEntitySyncTarget> logger)
    {
        _gateway     = gateway;
        _connections = connections;
        _services    = services;
        _logger      = logger;
    }

    public string TargetCode => PostingTargetCodes.QuickBooks;

    public Task<bool> IsActiveAsync(CancellationToken ct = default) => QuickBooksTargetActivity.IsActiveAsync(_services, _connections, ct);

    public async Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default)
    {
        try
        {
            var inactive = request.Action is SyncAction.Deactivate or SyncAction.Delete;
            (SyncKind kind, Task<GatewayResult> call) = request.Document switch
            {
                CustomerPayload c => (SyncKind.Customer, _gateway.UpsertCustomerAsync(Deactivated(c, inactive), ct)),
                VendorPayload v   => (SyncKind.Vendor, _gateway.UpsertVendorAsync(Deactivated(v, inactive), ct)),
                ItemPayload i     => (SyncKind.Item, _gateway.UpsertItemAsync(Deactivated(i, inactive), ct)),
                null              => throw new ArgumentException("The request carries no document."),
                _                 => throw new ArgumentException($"{request.Document.GetType().Name} is not a customer, vendor or product.")
            };

            var result = await call;
            return result.Outcome switch
            {
                GatewayOutcome.NotConnected => new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.NoTargetConfigured, ErrorMessage = "No usable QuickBooks connection." },
                GatewayOutcome.Disabled     => new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.NoTargetConfigured, ErrorMessage = "Syncing this kind is switched off, or the record is skipped." },
                GatewayOutcome.Invalid      => new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.ValidationFailed,
                                                                      ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Message)) },
                _ when result.State == SyncState.Synced => new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.DuplicateSkipped },
                _ when result.State == SyncState.NotSynced
                       && SyncHolds.IsHeldCode((await QuickBooksTargetActivity.StatusAsync(_gateway, kind, request.Document.ExternalId, ct))?.LastErrorCode)
                    => new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.Held },
                // NotSynced otherwise = stored, out of the partner/item scope (D-6): kept for matching, not sent.
                GatewayOutcome.Accepted or GatewayOutcome.WaitingOnDependency =>
                    new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.Queued, TargetsQueued = result.State == SyncState.NotSynced ? 0 : 1 },
                _ => new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.Failed, ErrorMessage = $"Unexpected gateway answer {result.Outcome}." }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A crash is Failed (callers log it at Warning); ValidationFailed is kept for real payload refusals.
            _logger.LogError(ex, "QuickBooks target: entity sync of {EntityType} {Id} failed.", request.EntityType, request.EntityId);
            return new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.Failed, ErrorMessage = ex.Message };
        }
    }

    private static T Deactivated<T>(T payload, bool inactive) where T : class
    {
        if (!inactive) return payload;
        switch (payload)
        {
            case PartyPayload p: p.IsActive = false; break;
            case ItemPayload i:  i.IsActive = false; break;
        }
        return payload;
    }
}
