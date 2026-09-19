namespace SMS.Shared.Common;

/// <summary>One charge on a payable that no purchase order sits behind.</summary>
/// <param name="Description">What is being charged for, in the words it will be paid against.</param>
/// <param name="Amount">The line total. Negative for a credit.</param>
public sealed record SupplierInvoiceLine(string Description, decimal Amount);

/// <summary>
/// A payable raised by a module other than Finance, for something nobody raised a purchase order
/// for.
/// </summary>
/// <param name="SupplierId">Who is owed. The posting module is responsible for knowing this.</param>
/// <param name="SupplierInvoiceNo">Their own number, as printed.</param>
/// <param name="InvoiceDate">As printed on their document.</param>
/// <param name="DueDate">When they expect to be paid.</param>
/// <param name="Currency">ISO 4217.</param>
/// <param name="Subtotal">Before tax. Must equal the sum of the lines when lines are given.</param>
/// <param name="TaxAmount">Stated separately, as on the document.</param>
/// <param name="SourceType">What kind of thing produced this — for the note and for tracing back.</param>
/// <param name="SourceUuid">That thing's id, so a second posting of it can be refused.</param>
/// <param name="Notes">Free text carried onto the invoice.</param>
/// <param name="Lines">Optional. An invoice with none is a single figure.</param>
public sealed record SupplierInvoicePosting(
    Guid     SupplierId,
    string?  SupplierInvoiceNo,
    DateTime InvoiceDate,
    DateTime DueDate,
    string   Currency,
    decimal  Subtotal,
    decimal  TaxAmount,
    string   SourceType,
    Guid     SourceUuid,
    string?  Notes = null,
    IReadOnlyList<SupplierInvoiceLine>? Lines = null);

/// <param name="InvoiceUuid">The payable, whether this call created it or found it already there.</param>
/// <param name="InvoiceNumber">Finance's own number for it.</param>
/// <param name="AlreadyPosted">
/// True when a payable for this source already existed. The caller has not created a second one —
/// which is the only guarantee that matters, because paying a carrier twice looks exactly as
/// legitimate as paying it once.
/// </param>
public sealed record SupplierInvoicePostingResult(
    Guid InvoiceUuid, string InvoiceNumber, bool AlreadyPosted);

/// <summary>
/// Raises a payable in Finance for something outside the purchase-order flow.
/// <para>
/// <b>Why this exists (decision G10).</b> A carrier's freight bill is money the company owes, and
/// until this contract it could be approved in Logistics and then reach no ledger at all — the
/// approval said so in as many words. Everything a supplier is owed now goes through one payables
/// ledger, one ageing report and one payment run, whether a purchase order started it or not.
/// </para>
/// <para>
/// <b>The purchase order is optional, not faked.</b> Finance's invoice made <c>PoUuid</c> nullable
/// to accept these. A synthetic PO reference would have been cheaper and would have put a lie in a
/// column that matching, reporting and the payment run all read.
/// </para>
/// <para>
/// <b>The caller owns the decision that the charge is correct.</b> This posts what it is given. In
/// the freight case that judgement is the three-way match against quote, booking and bill, which has
/// already run by the time anything is approved — Finance then decides separately whether to pay it,
/// which is why the invoice arrives unapproved.
/// </para>
/// <para>
/// Implemented in SMS.Modules.Finance and resolved through DI, so callers need no project reference
/// to it — the same arrangement as <see cref="IGoodsIssuePoster"/>.
/// </para>
/// </summary>
public interface ISupplierInvoicePoster
{
    /// <summary>
    /// Creates the payable, or returns the one already raised for this source.
    /// <para>
    /// <b>Idempotent on <c>SourceUuid</c></b>, because the alternative is paying a carrier twice.
    /// A retried job, a double-clicked button and a resumed transaction all return the first
    /// invoice rather than raising a second.
    /// </para>
    /// </summary>
    Task<SupplierInvoicePostingResult> PostAsync(
        SupplierInvoicePosting posting, int userId, CancellationToken ct = default);
}
