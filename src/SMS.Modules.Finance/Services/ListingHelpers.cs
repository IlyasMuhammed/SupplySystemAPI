using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>What every receivables listing does the same way, so the ledger, the invoices and the payments cannot drift apart.</summary>
internal static class PagedResults
{
    internal const int MaxPageSize = 100;

    /// <summary>A page below 1 is the first; a page size is held to 1–100 rather than refused.</summary>
    internal static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    internal static PaginatedResponse<T> Of<T>(List<T> data, int total, int page, int pageSize) => new()
    {
        Data         = data,
        TotalRecords = total,
        Page         = page,
        PageSize     = pageSize,
        TotalPages   = (int)Math.Ceiling((double)total / pageSize)
    };
}

/// <summary>
/// A date range in whole days, inclusive at both ends. A bare "on or before DateTo" would drop
/// everything that happened during the last day, because a date arrives as midnight and records carry
/// a time of day; so the end is taken as the start of the day after it.
/// </summary>
internal static class DayRange
{
    /// <param name="what">What the range is over, for the refusal — "ledger", "invoice list".</param>
    /// <returns>The first instant included, and the first instant excluded; either may be open.</returns>
    internal static (DateTime? Start, DateTime? EndExclusive) Of(DateTime? from, DateTime? to, string what)
    {
        if (from is { } f && to is { } t && f.Date > t.Date)
            throw new BadRequestException($"The {what}'s start date is after its end date.");

        return (from?.Date, to?.Date.AddDays(1));
    }
}
