namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>
/// A48 C10 (D-8) — how the existing supplier-invoice and supplier-payment services keep the payment schedules in step,
/// after their own commits. Every method is safe to call more than once and never throws (a failure is logged and the
/// next sync — every payment-run proposal re-syncs what it reads — repairs it).
/// </summary>
internal interface IPaymentScheduleSync
{
    /// <summary>An invoice was approved: create its schedule (due date, priority/method from the profile, discount terms).</summary>
    Task OnInvoiceApprovedAsync(Guid invoiceUuid, CancellationToken ct = default);

    /// <summary>
    /// The invoices' amounts owed or statuses may have changed (a payment posted, bounced or cancelled; a note applied;
    /// an invoice reversed): recompute AmountRemaining and Status (PartiallyPaid / Paid / Cancelled / Open).
    /// </summary>
    Task SyncAsync(IReadOnlyCollection<Guid> invoiceUuids, CancellationToken ct = default);
}
