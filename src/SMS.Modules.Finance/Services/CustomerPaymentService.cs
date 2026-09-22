using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

// A29-P7-05 §9.3 / §9.4 / §9.5.
internal sealed class CustomerPaymentService : ICustomerPaymentService
{
    private const int MaxAttempts = 5;
    private const int MaxNotesLength = 500;
    private const int MaxBounceReasonLength = 200;
    private const string NumberPrefix = "CPAY";

    /// <summary>Only an issued invoice has a receivable booked, so only these have anything to pay down.</summary>
    private static readonly string[] PayableStatuses =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Overdue];

    private readonly FinanceDbContext           _db;
    private readonly ICustomerLedgerService     _ledger;
    private readonly ISupplierNameLookupService _partnerNames;
    private readonly ILookupsService            _lookups;
    private readonly TimeProvider               _clock;

    public CustomerPaymentService(
        FinanceDbContext db, ICustomerLedgerService ledger, ISupplierNameLookupService partnerNames,
        ILookupsService lookups, TimeProvider? clock = null)
    {
        _db           = db;
        _ledger       = ledger;
        _partnerNames = partnerNames;
        _lookups      = lookups;
        _clock        = clock ?? TimeProvider.System;
    }

    public async Task<CustomerPaymentRecorded> RecordPaymentAsync(
        Guid partnerId, decimal amount, string method, CustomerPaymentDetails details, int userId)
    {
        ArgumentNullException.ThrowIfNull(details);

        var paymentMethod = ValidateMethod(method, details);
        var currency      = ResolveCurrency(details.CurrencyCode);
        ValidateAmount(amount, "The payment");
        ValidateManualAllocations(details.Allocations, amount);

        var partnerName = await ResolvePartnerNameAsync(partnerId);

        var today       = _clock.GetUtcNow().UtcDateTime.Date;
        var paymentDate = details.PaymentDate?.Date ?? today;

        for (var attempt = 1; ; attempt++)
        {
            // Everything below is re-read and re-decided on every pass. A lost race means another
            // payment (or an invoice) for this customer committed first, and what it committed may
            // have been the very balance this one was about to reduce.
            var plan = details.Allocations is null
                ? await PlanFifoAsync(partnerId, amount, currency)
                : await PlanManualAsync(partnerId, currency, details.Allocations);

            var now     = _clock.GetUtcNow().UtcDateTime;
            var payment = new CustomerPayment
            {
                UUID          = Guid.NewGuid(),
                PartnerId     = partnerId,
                PartnerName   = partnerName,
                PaymentNumber = await NextNumberAsync(today),
                PaymentDate   = paymentDate,
                Amount        = amount,
                PaymentMethod = paymentMethod,
                ChequeNumber  = Clean(details.ChequeNumber),
                BankReference = Clean(details.BankReference),
                CurrencyCode  = currency,
                Notes         = Clean(details.Notes),
                Status        = CustomerPaymentStatuses.Received,
                CreatedBy     = userId,
                CreatedDate   = now
            };

            var allocations = Apply(payment, plan, now, userId);

            _db.CustomerPayments.Add(payment);

            // §9.5 — the credit is written in the same transaction as the allocations: one SaveChanges
            // below commits the payment, the invoices' new balances and the ledger entry, or none.
            // The full amount is credited, allocated or not: cash held on account still reduces what
            // the customer owes.
            var entry = await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                partnerId, CustomerLedgerEntryTypes.Payment,
                "CustomerPayment", payment.UUID, payment.PaymentNumber,
                Debit: 0m, Credit: amount, currency,
                Narrate(payment, plan.Select(p => p.Invoice.InvoiceNumber).ToList()),
                paymentDate, userId));

            try
            {
                await _db.SaveChangesAsync();

                var applied = plan
                    .Select(p => new AppliedPaymentAllocation(
                        p.Invoice.UUID, p.Invoice.InvoiceNumber, p.Amount, p.Invoice.BalanceDue, p.Invoice.Status))
                    .ToList();
                var allocated = applied.Sum(a => a.Amount);

                return new CustomerPaymentRecorded(
                    payment.UUID, payment.PaymentNumber, amount, allocated, amount - allocated, currency,
                    applied, entry.RunningBalance);
            }
            catch (DbUpdateException)
            {
                // Another writer took this customer's next ledger sequence, or this payment number.
                // Forget everything this pass tracked; the next one starts from what is now committed.
                // On the last attempt too, so a failed payment never lingers on the context waiting
                // for some later save to commit half of it.
                Detach([payment, entry, .. allocations, .. plan.Select(p => p.Invoice)]);
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    public async Task<CustomerPaymentAllocated> AllocateAsync(
        Guid paymentUuid, IReadOnlyList<ManualPaymentAllocation>? allocations, int userId)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Re-read on every pass. This action writes no ledger entry, so nothing about the customer's
            // ledger serializes two of them: the payment's own ModifiedDate does (see its map). A
            // request that loses that race comes back here, finds the money already applied, and is
            // refused below rather than applying it a second time.
            var payment = await _db.CustomerPayments.Include(p => p.Allocations)
                .FirstOrDefaultAsync(p => p.UUID == paymentUuid)
                ?? throw new NotFoundException("CustomerPayment", paymentUuid);

            if (payment.Status != CustomerPaymentStatuses.Received)
                throw new ConflictException(
                    $"Payment {payment.PaymentNumber} is {payment.Status}. Only a RECEIVED payment can be applied to invoices.");

            var remaining = payment.Amount - payment.Allocations.Sum(a => a.AllocatedAmount);
            if (remaining <= 0m)
                throw new BadRequestException(
                    $"Payment {payment.PaymentNumber} has already been applied in full; there is nothing left to allocate.");

            ValidateManualAllocations(allocations, remaining, "what is left to apply from the payment,");

            var plan = allocations is null
                ? await PlanFifoAsync(payment.PartnerId, remaining, payment.CurrencyCode)
                : await PlanManualAsync(payment.PartnerId, payment.CurrencyCode, allocations);

            if (plan.Count == 0)
                throw new BadRequestException(allocations is null
                    ? $"There is no unpaid {payment.CurrencyCode} invoice to apply payment {payment.PaymentNumber} to."
                    : "No allocations were given.");

            var now      = _clock.GetUtcNow().UtcDateTime;
            var existing = payment.Allocations.ToList();
            var added    = Apply(payment, plan, now, userId);

            // A fresh value on the payment is what makes a stale second request fail its save.
            payment.ModifiedBy   = userId;
            payment.ModifiedDate = now;

            try
            {
                await _db.SaveChangesAsync();

                var applied = plan
                    .Select(p => new AppliedPaymentAllocation(
                        p.Invoice.UUID, p.Invoice.InvoiceNumber, p.Amount, p.Invoice.BalanceDue, p.Invoice.Status))
                    .ToList();
                var allocated = payment.Allocations.Sum(a => a.AllocatedAmount);

                return new CustomerPaymentAllocated(
                    payment.UUID, payment.PaymentNumber, payment.Amount, allocated, payment.Amount - allocated, applied);
            }
            catch (DbUpdateException)
            {
                Detach([payment, .. existing, .. added, .. plan.Select(p => p.Invoice)]);
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    public async Task<CustomerPaymentBounced> BounceAsync(Guid paymentUuid, string? reason, int userId)
    {
        reason = Clean(reason);
        if (reason is { Length: > MaxBounceReasonLength })
            throw new BadRequestException($"The reason is longer than {MaxBounceReasonLength} characters.");

        for (var attempt = 1; ; attempt++)
        {
            // Re-read on every pass, for the same reason as AllocateAsync: a request that loses the race
            // to bounce this payment (or to pay one of its invoices) comes back to what was committed.
            var payment = await _db.CustomerPayments
                .Include(p => p.Allocations).ThenInclude(a => a.SalesInvoice)
                .FirstOrDefaultAsync(p => p.UUID == paymentUuid)
                ?? throw new NotFoundException("CustomerPayment", paymentUuid);

            if (payment.PaymentMethod != CustomerPaymentMethods.Cheque)
                throw new BadRequestException(
                    $"Payment {payment.PaymentNumber} was received by {payment.PaymentMethod}. Only a cheque can bounce.");

            if (payment.Status != CustomerPaymentStatuses.Received)
                throw new ConflictException(
                    $"Payment {payment.PaymentNumber} is {payment.Status}. Only a RECEIVED payment can bounce.");

            var now   = _clock.GetUtcNow().UtcDateTime;
            var today = now.Date;

            var reversed = new List<ReversedPaymentAllocation>();
            foreach (var allocation in payment.Allocations.OrderBy(a => a.AllocatedAt).ThenBy(a => a.Id))
            {
                var invoice = allocation.SalesInvoice;

                invoice.AmountPaid -= allocation.AllocatedAmount;
                invoice.BalanceDue += allocation.AllocatedAmount;

                // An invoice that has since been cancelled or credited is no longer a receivable to
                // reopen: its money is put back, its status is left alone.
                if (invoice.Status is SalesInvoiceStatuses.Paid or SalesInvoiceStatuses.PartiallyPaid
                                   or SalesInvoiceStatuses.Overdue or SalesInvoiceStatuses.Issued)
                {
                    invoice.Status = invoice.DueDate < today ? SalesInvoiceStatuses.Overdue
                                   : invoice.AmountPaid > 0m ? SalesInvoiceStatuses.PartiallyPaid
                                   : SalesInvoiceStatuses.Issued;
                }

                invoice.ModifiedBy   = userId;
                invoice.ModifiedDate = now;

                reversed.Add(new ReversedPaymentAllocation(
                    invoice.UUID, invoice.InvoiceNumber, allocation.AllocatedAmount, invoice.BalanceDue, invoice.Status));
            }

            payment.Status       = CustomerPaymentStatuses.Bounced;
            payment.Notes        = WithBounceNote(payment.Notes, now, reason);
            payment.ModifiedBy   = userId;
            payment.ModifiedDate = now;

            // §9.5 — the debit offsets the credit written when the cheque was received, in the same
            // transaction as the invoices reopening: the customer owes the money again, or the books
            // still say they do not.
            var entry = await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                payment.PartnerId, CustomerLedgerEntryTypes.Payment,
                "CustomerPayment", payment.UUID, payment.PaymentNumber,
                Debit: payment.Amount, Credit: 0m, payment.CurrencyCode,
                NarrateBounce(payment, reason), now, userId));

            try
            {
                await _db.SaveChangesAsync();

                return new CustomerPaymentBounced(
                    payment.UUID, payment.PaymentNumber, payment.Amount, reversed, entry.RunningBalance);
            }
            catch (DbUpdateException)
            {
                Detach([payment, entry, .. payment.Allocations, .. payment.Allocations.Select(a => a.SalesInvoice)]);
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<CustomerPaymentDetailModel?> GetAsync(Guid paymentUuid)
    {
        var payment = await _db.CustomerPayments.AsNoTracking()
            .Include(p => p.Allocations).ThenInclude(a => a.SalesInvoice)
            .FirstOrDefaultAsync(p => p.UUID == paymentUuid);

        if (payment is null) return null;

        // A bounced or reversed payment keeps its allocation rows as history, but none of it is applied
        // any more and none of it is on account: the money is not there.
        var allocated = payment.Status == CustomerPaymentStatuses.Received
            ? payment.Allocations.Sum(a => a.AllocatedAmount)
            : 0m;
        var unallocated = payment.Status == CustomerPaymentStatuses.Received ? payment.Amount - allocated : 0m;

        return new CustomerPaymentDetailModel
        {
            Uuid              = payment.UUID,
            PaymentNumber     = payment.PaymentNumber,
            PartnerId         = payment.PartnerId,
            PartnerName       = payment.PartnerName,
            PaymentDate       = payment.PaymentDate,
            Amount            = payment.Amount,
            AllocatedAmount   = allocated,
            UnallocatedAmount = unallocated,
            PaymentMethod     = payment.PaymentMethod,
            ChequeNumber      = payment.ChequeNumber,
            BankReference     = payment.BankReference,
            CurrencyCode      = payment.CurrencyCode,
            Status            = payment.Status,
            Notes             = payment.Notes,
            CreatedBy         = payment.CreatedBy,
            CreatedDate       = payment.CreatedDate,
            ModifiedBy        = payment.ModifiedBy,
            ModifiedDate      = payment.ModifiedDate,
            Allocations = [.. payment.Allocations
                .OrderBy(a => a.AllocatedAt).ThenBy(a => a.Id)
                .Select(a => new CustomerPaymentAllocationModel
                {
                    AllocationUuid    = a.UUID,
                    InvoiceUuid       = a.SalesInvoice.UUID,
                    InvoiceNumber     = a.SalesInvoice.InvoiceNumber,
                    AllocatedAmount   = a.AllocatedAmount,
                    AllocatedAt       = a.AllocatedAt,
                    AllocatedBy       = a.AllocatedBy,
                    InvoiceBalanceDue = a.SalesInvoice.BalanceDue,
                    InvoiceStatus     = a.SalesInvoice.Status
                })]
        };
    }

    public async Task<PaginatedResponse<CustomerPaymentListItemModel>> ListAsync(CustomerPaymentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "payment list");

        var status = filter.Status?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(status) && !CustomerPaymentStatuses.All.Contains(status))
            throw new BadRequestException(
                $"'{filter.Status}' is not a payment status. Valid: {string.Join(", ", CustomerPaymentStatuses.All)}.");

        var method = filter.Method?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(method) && !CustomerPaymentMethods.All.Contains(method))
            throw new BadRequestException(
                $"'{filter.Method}' is not a payment method. Valid: {string.Join(", ", CustomerPaymentMethods.All)}.");

        var query = _db.CustomerPayments.AsNoTracking().AsQueryable();

        if (filter.PartnerId is { } partner)  query = query.Where(p => p.PartnerId == partner);
        if (!string.IsNullOrEmpty(status))    query = query.Where(p => p.Status == status);
        if (!string.IsNullOrEmpty(method))    query = query.Where(p => p.PaymentMethod == method);
        if (start is { } from)                query = query.Where(p => p.PaymentDate >= from);
        if (endExclusive is { } end)          query = query.Where(p => p.PaymentDate < end);

        // "Unallocated" is about money still on account, so a payment that was bounced or reversed —
        // whose money is not on account at all — is not one of them, whatever its allocations say.
        if (filter.Unallocated == true)
            query = query.Where(p => p.Status == CustomerPaymentStatuses.Received
                                  && p.Amount > p.Allocations.Sum(a => a.AllocatedAmount));
        else if (filter.Unallocated == false)
            query = query.Where(p => p.Status == CustomerPaymentStatuses.Received
                                  && p.Amount <= p.Allocations.Sum(a => a.AllocatedAmount));

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            query = query.Where(p => p.PaymentNumber.ToLower().Contains(s)
                                  || p.PartnerName.ToLower().Contains(s)
                                  || (p.ChequeNumber != null && p.ChequeNumber.ToLower().Contains(s))
                                  || (p.BankReference != null && p.BankReference.ToLower().Contains(s)));
        }

        var total = await query.CountAsync();
        var (page, pageSize) = PagedResults.Clamp(filter.Page, filter.PageSize);

        var items = await query
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new CustomerPaymentListItemModel
            {
                Uuid              = p.UUID,
                PaymentNumber     = p.PaymentNumber,
                PartnerId         = p.PartnerId,
                PartnerName       = p.PartnerName,
                PaymentDate       = p.PaymentDate,
                Amount            = p.Amount,
                AllocatedAmount   = p.Status == CustomerPaymentStatuses.Received
                    ? p.Allocations.Sum(a => a.AllocatedAmount) : 0m,
                UnallocatedAmount = p.Status == CustomerPaymentStatuses.Received
                    ? p.Amount - p.Allocations.Sum(a => a.AllocatedAmount) : 0m,
                PaymentMethod     = p.PaymentMethod,
                ChequeNumber      = p.ChequeNumber,
                BankReference     = p.BankReference,
                CurrencyCode      = p.CurrencyCode,
                Status            = p.Status
            })
            .ToListAsync();

        return PagedResults.Of(items, total, page, pageSize);
    }

    // ── Applying ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a plan into allocation rows and moves each invoice's balance and status — in one place,
    /// so recording a payment and applying one later cannot disagree about what "paid" means.
    /// </summary>
    private static List<PaymentAllocation> Apply(
        CustomerPayment payment, IEnumerable<(SalesInvoice Invoice, decimal Amount)> plan, DateTime now, int userId)
    {
        var added = new List<PaymentAllocation>();

        foreach (var (invoice, applied) in plan)
        {
            var allocation = new PaymentAllocation
            {
                UUID            = Guid.NewGuid(),
                SalesInvoice    = invoice,
                AllocatedAmount = applied,
                AllocatedAt     = now,
                AllocatedBy     = userId
            };
            payment.Allocations.Add(allocation);
            added.Add(allocation);

            // §9.5: PARTIALLY_PAID once anything is paid, PAID when nothing is owing.
            invoice.AmountPaid  += applied;
            invoice.BalanceDue  -= applied;
            invoice.Status       = invoice.BalanceDue == 0m ? SalesInvoiceStatuses.Paid : SalesInvoiceStatuses.PartiallyPaid;
            invoice.ModifiedBy   = userId;
            invoice.ModifiedDate = now;
        }

        return added;
    }

    // ── Planning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §9.4 FIFO: the customer's oldest unpaid invoice first, each paid down as far as the money goes.
    /// "Oldest" is the invoice date, with creation order breaking a tie. Draft, paid, cancelled and
    /// credit-note invoices, other customers', and other currencies' are never candidates.
    /// </summary>
    private async Task<List<(SalesInvoice Invoice, decimal Amount)>> PlanFifoAsync(
        Guid partnerId, decimal amount, string currency)
    {
        var open = await _db.SalesInvoices
            .Where(i => i.PartnerId == partnerId && !i.IsDelete && i.CurrencyCode == currency
                     && i.BalanceDue > 0m && PayableStatuses.Contains(i.Status))
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)
            .ToListAsync();

        var plan      = new List<(SalesInvoice, decimal)>();
        var remaining = amount;

        foreach (var invoice in open)
        {
            if (remaining <= 0m) break;

            var applied = Math.Min(remaining, invoice.BalanceDue);
            plan.Add((invoice, applied));
            remaining -= applied;
        }

        return plan;
    }

    /// <summary>The caller's override: exactly what was asked for, or a refusal that says why.</summary>
    private async Task<List<(SalesInvoice Invoice, decimal Amount)>> PlanManualAsync(
        Guid partnerId, string currency, IReadOnlyList<ManualPaymentAllocation> requested)
    {
        var uuids    = requested.Select(r => r.InvoiceUuid).ToList();
        var invoices = await _db.SalesInvoices.Where(i => uuids.Contains(i.UUID) && !i.IsDelete).ToListAsync();

        var plan = new List<(SalesInvoice, decimal)>();

        foreach (var request in requested)
        {
            var invoice = invoices.FirstOrDefault(i => i.UUID == request.InvoiceUuid)
                ?? throw new NotFoundException("SalesInvoice", request.InvoiceUuid);

            if (invoice.PartnerId != partnerId)
                throw new BadRequestException(
                    $"Sales invoice {invoice.InvoiceNumber} belongs to another customer and cannot be paid from this one's payment.");

            if (!PayableStatuses.Contains(invoice.Status))
                throw new BadRequestException(
                    $"Sales invoice {invoice.InvoiceNumber} is {invoice.Status}. Only an ISSUED, PARTIALLY_PAID or OVERDUE invoice can be paid.");

            if (invoice.CurrencyCode != currency)
                throw new BadRequestException(
                    $"Sales invoice {invoice.InvoiceNumber} is in {invoice.CurrencyCode}, but the payment is in {currency}.");

            if (request.Amount > invoice.BalanceDue)
                throw new BadRequestException(
                    $"{request.Amount:0.00} is more than the {invoice.BalanceDue:0.00} still owing on sales invoice {invoice.InvoiceNumber}.");

            plan.Add((invoice, request.Amount));
        }

        return plan;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static string ValidateMethod(string method, CustomerPaymentDetails details)
    {
        var normalized = method?.Trim().ToUpperInvariant() ?? string.Empty;

        if (!CustomerPaymentMethods.All.Contains(normalized))
            throw new BadRequestException(
                $"'{method}' is not a payment method. Valid: {string.Join(", ", CustomerPaymentMethods.All)}.");

        if (normalized == CustomerPaymentMethods.Cheque && string.IsNullOrWhiteSpace(details.ChequeNumber))
            throw new BadRequestException(
                "A cheque payment needs the cheque number, so a returned cheque can be matched to its receipt.");

        if (Clean(details.ChequeNumber) is { Length: > 30 })
            throw new BadRequestException("The cheque number is longer than 30 characters.");
        if (Clean(details.BankReference) is { Length: > 100 })
            throw new BadRequestException("The bank reference is longer than 100 characters.");
        if (Clean(details.Notes) is { Length: > 500 })
            throw new BadRequestException("The notes are longer than 500 characters.");

        return normalized;
    }

    /// <summary>Money is kept to two decimal places, so a third is refused rather than rounded away from the ledger.</summary>
    private static void ValidateAmount(decimal amount, string what)
    {
        if (amount <= 0m)
            throw new BadRequestException($"{what} must be more than zero.");

        if (decimal.Round(amount, 2) != amount)
            throw new BadRequestException($"{what} has more than two decimal places.");
    }

    private static void ValidateManualAllocations(
        IReadOnlyList<ManualPaymentAllocation>? requested, decimal ceiling, string ceilingLabel = "the payment of")
    {
        if (requested is null) return;

        foreach (var request in requested)
            ValidateAmount(request.Amount, "An allocation");

        if (requested.GroupBy(r => r.InvoiceUuid).Any(g => g.Count() > 1))
            throw new BadRequestException("An invoice is named twice in the allocations; give it one amount.");

        var total = requested.Sum(r => r.Amount);
        if (total > ceiling)
            throw new BadRequestException(
                $"The allocations come to {total:0.00}, more than {ceilingLabel} {ceiling:0.00}.");
    }

    private string ResolveCurrency(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new BadRequestException("The payment must say what currency it was received in.");

        // The catalog's own spelling, so "pkr" matches invoices that were stamped "PKR".
        var known = _lookups.GetCurrencies()
            .Select(c => c.Code?.Trim())
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && string.Equals(c, code.Trim(), StringComparison.OrdinalIgnoreCase));

        return known
            ?? throw new BadRequestException($"'{code.Trim()}' is not a currency in the Lookups catalog.");
    }

    private async Task<string> ResolvePartnerNameAsync(Guid partnerId)
    {
        var names = await _partnerNames.GetNamesAsync([partnerId]);
        if (names is not null && names.TryGetValue(partnerId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        // The master record may be unreachable, but a customer we have already billed is still known.
        var billed = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.PartnerId == partnerId)
            .Select(i => i.PartnerName)
            .FirstOrDefaultAsync();

        return billed ?? throw new NotFoundException("Customer", partnerId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CPAY-YYYYMMDD-NNNN</c>, one counter per organization per day (§9.3): the highest number
    /// already used that day plus one, with the unique <c>(organization, number)</c> index as the
    /// guard — two writers reading the same maximum cannot both commit, and the loser retries.
    /// </summary>
    private async Task<string> NextNumberAsync(DateTime date)
    {
        var prefix = $"{NumberPrefix}-{date:yyyyMMdd}-";

        var used = await _db.CustomerPayments
            .Where(p => p.PaymentNumber.StartsWith(prefix))
            .Select(p => p.PaymentNumber)
            .ToListAsync();

        var highest = used
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{highest + 1:D4}";
    }

    private static string Narrate(CustomerPayment payment, IReadOnlyList<string> invoiceNumbers)
    {
        var reference = payment.ChequeNumber ?? payment.BankReference;
        var text = $"Payment {payment.PaymentNumber} received by {payment.PaymentMethod}"
                 + (reference is null ? "" : $" {reference}");

        text += invoiceNumbers.Count == 0
            ? ", held on account"
            : ", applied to " + string.Join(", ", invoiceNumbers.Take(5))
                              + (invoiceNumbers.Count > 5 ? $" and {invoiceNumbers.Count - 5} more" : "");

        return text.Length <= 500 ? text : text[..500];
    }

    private static string NarrateBounce(CustomerPayment payment, string? reason)
    {
        var text = $"Cheque {payment.ChequeNumber} ({payment.PaymentNumber}) bounced"
                 + (reason is null ? "" : $": {reason}");

        return text.Length <= 500 ? text : text[..500];
    }

    /// <summary>
    /// The payment's notes with the bounce recorded after them. The notes column holds 500 characters,
    /// and the bounce is the part that matters now, so it is what makes room when the two do not fit.
    /// </summary>
    private static string WithBounceNote(string? existing, DateTime now, string? reason)
    {
        var bounce = $"Bounced {now:yyyy-MM-dd}" + (reason is null ? "" : $": {reason}");
        if (string.IsNullOrEmpty(existing)) return bounce;

        var room = MaxNotesLength - bounce.Length - 1;
        if (room <= 0) return bounce;

        return (existing.Length <= room ? existing : existing[..room]) + "\n" + bounce;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A failed save leaves what it tried to write still tracked; the retry must not see any of it.</summary>
    private void Detach(IEnumerable<object> tracked)
    {
        foreach (var entity in tracked)
            _db.Entry(entity).State = EntityState.Detached;
    }
}
