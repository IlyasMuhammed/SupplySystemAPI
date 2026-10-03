using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Integration;

internal enum QuickBooksPushAction
{
    /// <summary>Nothing QuickBooks needs to hear about.</summary>
    None,
    Upsert,
    Void
}

/// <summary>
/// When a sales invoice goes to QuickBooks (plan D-7, D-8), in one place so every code path that changes
/// an invoice asks the same question:
/// <list type="bullet">
/// <item>A DRAFT never goes — it is not yet anything the customer owes.</item>
/// <item>Leaving DRAFT (issued) sends it.</item>
/// <item>CANCELLED after it left DRAFT voids it. A draft that is cancelled was never sent, so nothing.</item>
/// <item>Once issued, a change of <b>content</b> is sent again. A change of status alone — PARTIALLY_PAID,
/// PAID, OVERDUE, all caused by payments or the calendar — is not: payments are not synced (D-8), and to
/// QuickBooks every one of those is the same issued invoice.</item>
/// </list>
/// </summary>
internal static class SalesInvoiceQuickBooksRules
{
    /// <param name="previousStatus">The status before the change; null for an invoice that did not exist.</param>
    /// <param name="currentStatus">The status after it.</param>
    /// <param name="contentChanged">Whether anything QuickBooks is sent (dates, lines, amounts, notes) changed.</param>
    public static QuickBooksPushAction OnChange(string? previousStatus, string currentStatus, bool contentChanged)
    {
        var wasDraft = previousStatus is null || previousStatus == SalesInvoiceStatuses.Draft;

        if (currentStatus == SalesInvoiceStatuses.Draft)
            return QuickBooksPushAction.None;

        if (currentStatus == SalesInvoiceStatuses.Cancelled)
            return wasDraft || previousStatus == SalesInvoiceStatuses.Cancelled
                ? QuickBooksPushAction.None
                : QuickBooksPushAction.Void;

        if (wasDraft) return QuickBooksPushAction.Upsert;

        return contentChanged ? QuickBooksPushAction.Upsert : QuickBooksPushAction.None;
    }

    /// <summary>What the source does for an invoice asked for by id, or found by the reconciliation.</summary>
    public static QuickBooksPushAction ForCurrentState(string status) => status switch
    {
        SalesInvoiceStatuses.Draft     => QuickBooksPushAction.None,
        SalesInvoiceStatuses.Cancelled => QuickBooksPushAction.Void,
        _                              => QuickBooksPushAction.Upsert
    };
}
