using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>One line of a manual allocation: pay this much of this invoice.</summary>
public sealed record ManualPaymentAllocation(Guid InvoiceUuid, decimal Amount);

/// <param name="CurrencyCode">
/// The currency the money arrived in, as the Lookups catalog spells it. Required: an allocation only
/// ever pairs a payment with invoices in the same currency, so a guessed currency would quietly leave
/// the whole receipt unapplied.
/// </param>
/// <param name="PaymentDate">When the money was received. Defaults to today.</param>
/// <param name="ChequeNumber">Required for CHEQUE, so a bounce can be matched to its receipt.</param>
/// <param name="Allocations">
/// <c>null</c> lets the service apply the payment FIFO. A list — even an empty one — is the caller's
/// override and is applied exactly as written: whatever it does not name stays unallocated, on the
/// customer's account, rather than being spilled onto other invoices behind their back.
/// </param>
public sealed record CustomerPaymentDetails(
    string CurrencyCode,
    DateTime? PaymentDate = null,
    string? ChequeNumber = null,
    string? BankReference = null,
    string? Notes = null,
    IReadOnlyList<ManualPaymentAllocation>? Allocations = null);

public sealed record AppliedPaymentAllocation(
    Guid    InvoiceUuid,
    string  InvoiceNumber,
    decimal Amount,
    decimal BalanceDue,
    string  InvoiceStatus);

/// <param name="UnallocatedAmount">Received but not applied to any invoice — held on the customer's account.</param>
/// <param name="PartnerBalance">What the customer owes after this payment — the ledger's running balance. Negative means they are in credit.</param>
public sealed record CustomerPaymentRecorded(
    Guid    PaymentUuid,
    string  PaymentNumber,
    decimal Amount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    string  CurrencyCode,
    IReadOnlyList<AppliedPaymentAllocation> Allocations,
    decimal PartnerBalance);

/// <param name="AllocatedAmount">Applied to invoices in all — what was applied before, plus this call.</param>
/// <param name="UnallocatedAmount">Still on the customer's account after this call.</param>
/// <param name="Allocations">Only what this call applied, not the payment's earlier allocations.</param>
public sealed record CustomerPaymentAllocated(
    Guid    PaymentUuid,
    string  PaymentNumber,
    decimal Amount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    IReadOnlyList<AppliedPaymentAllocation> Allocations);

/// <param name="Amount">The whole payment, which is what the ledger is debited with.</param>
/// <param name="Reversed">What was taken back off each invoice, one line per allocation the payment had.</param>
/// <param name="PartnerBalance">What the customer owes after the reversal — the ledger's running balance.</param>
public sealed record CustomerPaymentBounced(
    Guid    PaymentUuid,
    string  PaymentNumber,
    decimal Amount,
    IReadOnlyList<ReversedPaymentAllocation> Reversed,
    decimal PartnerBalance);

/// <param name="BalanceDue">What is owing on the invoice once this allocation is taken back.</param>
public sealed record ReversedPaymentAllocation(
    Guid    InvoiceUuid,
    string  InvoiceNumber,
    decimal Amount,
    decimal BalanceDue,
    string  InvoiceStatus);

/// <summary>
/// Records money received from a customer and applies it to their invoices (A29 §9.3–§9.5).
/// </summary>
public interface ICustomerPaymentService
{
    /// <summary>
    /// A cheque the bank sent back: the receivable stands again. In one transaction it takes each of the
    /// payment's allocations back off its invoice (the balance is owing again; the invoice returns to
    /// ISSUED, PARTIALLY_PAID for what other payments still cover, or OVERDUE if it is past due),
    /// marks the payment BOUNCED and debits the customer's ledger for the full amount, offsetting the
    /// credit booked when it was received. The allocation rows are kept, so the history still says what
    /// the payment was once applied to.
    /// <para>
    /// Only a RECEIVED cheque can bounce: other methods do not come back from the bank this way.
    /// Bouncing the same payment twice is refused, and two requests at once cannot both succeed — the
    /// loser re-reads the winner's result and is refused rather than debiting the customer twice.
    /// </para>
    /// </summary>
    Task<CustomerPaymentBounced> BounceAsync(Guid paymentUuid, string? reason, int userId);

    /// <summary>One payment with everything it was applied to, or <c>null</c> if there is none.</summary>
    Task<CustomerPaymentDetailModel?> GetAsync(Guid paymentUuid);

    /// <summary>A page of payments, newest first.</summary>
    Task<PaginatedResponse<CustomerPaymentListItemModel>> ListAsync(CustomerPaymentFilter filter);

    /// <summary>
    /// Applies what is left of a received payment to invoices: FIFO when <paramref name="allocations"/>
    /// is <c>null</c>, exactly as written otherwise. The customer's ledger is <b>not</b> touched — the
    /// credit for the whole payment was booked when it was recorded, and moving it from "on account"
    /// to a particular invoice does not change what the customer owes.
    /// <para>
    /// Refused if the payment is not RECEIVED, has nothing left, or there is nothing to apply it to.
    /// Two requests to apply the same payment at once cannot both succeed: the loser re-reads what the
    /// winner committed and, finding nothing left, is refused rather than applying the money twice.
    /// </para>
    /// </summary>
    Task<CustomerPaymentAllocated> AllocateAsync(
        Guid paymentUuid, IReadOnlyList<ManualPaymentAllocation>? allocations, int userId);

    /// <summary>
    /// Records a RECEIVED payment. In one transaction it writes the payment and its allocations,
    /// reduces each named invoice's balance (PARTIALLY_PAID while some is owing, PAID at zero) and
    /// credits the customer's ledger for the full amount received.
    /// <para>
    /// Unless <see cref="CustomerPaymentDetails.Allocations"/> says otherwise, the money goes to the
    /// customer's oldest unpaid invoices first. Only invoices that have been issued are payable — a
    /// draft has booked no receivable to pay down.
    /// </para>
    /// </summary>
    Task<CustomerPaymentRecorded> RecordPaymentAsync(
        Guid partnerId, decimal amount, string method, CustomerPaymentDetails details, int userId);
}
