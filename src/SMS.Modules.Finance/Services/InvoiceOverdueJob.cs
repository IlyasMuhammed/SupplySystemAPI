using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A29-P7-07 §9.5 — daily sweep that marks sales invoices OVERDUE once their due date has passed with
/// something still owing: <c>status IN (ISSUED, PARTIALLY_PAID) AND due_date &lt; today AND balance_due &gt; 0</c>.
/// <para>
/// <b>Daily and by date.</b> An invoice due on the 20th is overdue from the 21st: the customer has the
/// whole of the due date to pay. "Today" is the UTC date, the same clock invoices are dated by.
/// </para>
/// <para>
/// <b>It only moves status.</b> Nothing about the money changes, so the ledger, the amounts and the
/// allocations are left exactly as they were. Only ISSUED and PARTIALLY_PAID are candidates: DRAFT has
/// no receivable, PAID/CANCELLED/CREDIT_NOTE owe nothing, and OVERDUE is already flagged — which also
/// makes a second run the same day a no-op.
/// </para>
/// <para>
/// <b>No ambient tenant.</b> A bare recurring job has no organization of its own, so every query here
/// bypasses the tenant filter deliberately and every organization's invoices are swept in one pass.
/// Nothing is written that needs stamping with an organization, and each invoice keeps its own.
/// </para>
/// <para>
/// <b>In batches, saved as it goes.</b> A large backlog is flagged a batch at a time, so one failed
/// save costs a batch rather than the whole sweep, and Hangfire's retry simply picks up what is left —
/// flagged invoices no longer match, so nothing is done twice.
/// </para>
/// <para>
/// <b>Never over a payment.</b> An invoice carries optimistic concurrency on its <c>ModifiedDate</c>,
/// so if a payment settles one between this job reading it and saving, the save fails rather than
/// overwriting PAID with OVERDUE. The job then fails and is retried, and the retry finds it settled.
/// </para>
/// </summary>
internal sealed class InvoiceOverdueJob
{
    internal const string RecurringJobId = "finance-sales-invoice-overdue-sweep";

    /// <summary>Recorded against the system, not a person — nobody pressed a button.</summary>
    internal const int SystemUserId = 0;

    internal const int BatchSize = 200;

    private static readonly string[] Candidates =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid];

    private readonly FinanceDbContext _db;
    private readonly ILogger<InvoiceOverdueJob> _log;
    private readonly TimeProvider _clock;

    public InvoiceOverdueJob(FinanceDbContext db, ILogger<InvoiceOverdueJob> log, TimeProvider? clock = null)
    {
        _db    = db;
        _log   = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Daily at 00:00 UTC — the start of the day after a due date, the moment an invoice becomes overdue.</summary>
    internal static void Schedule() =>
        RecurringJob.AddOrUpdate<InvoiceOverdueJob>(RecurringJobId, job => job.RunAsync(), Cron.Daily);

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var flagged = await SweepAsync();

        if (flagged > 0)
            _log.LogInformation("Sales invoice overdue sweep: marked {Count} invoice(s) OVERDUE.", flagged);
    }

    /// <summary>Marks every overdue invoice and returns how many it marked.</summary>
    internal async Task<int> SweepAsync()
    {
        var now   = _clock.GetUtcNow().UtcDateTime;
        var today = now.Date;

        var flagged = 0;
        var lastId  = 0;

        while (true)
        {
            // A cursor on Id as well as the predicate: a batch is guaranteed to move forward even if
            // something stopped a row from leaving the predicate, so the sweep cannot loop on it.
            var batch = await _db.SalesInvoices
                .IgnoreQueryFilters()
                .Where(i => i.Id > lastId
                         && !i.IsDelete
                         && Candidates.Contains(i.Status)
                         && i.DueDate < today
                         && i.BalanceDue > 0m)
                .OrderBy(i => i.Id)
                .Take(BatchSize)
                .ToListAsync();

            if (batch.Count == 0) break;

            foreach (var invoice in batch)
            {
                invoice.Status       = SalesInvoiceStatuses.Overdue;
                invoice.ModifiedBy   = SystemUserId;
                invoice.ModifiedDate = now;
            }

            await _db.SaveChangesAsync();

            flagged += batch.Count;
            lastId   = batch[^1].Id;

            // Nothing in a finished batch is needed again; keep the tracker from growing with the backlog.
            _db.ChangeTracker.Clear();
        }

        return flagged;
    }
}
