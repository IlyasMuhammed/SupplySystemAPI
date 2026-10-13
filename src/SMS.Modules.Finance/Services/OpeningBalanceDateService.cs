using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>A40 (TFIN, D-7) — <c>{ date: 'yyyy-MM-dd' | null }</c>, both ways (API-CONTRACT §Finance).</summary>
public sealed class OpeningBalanceDateModel
{
    public string? Date { get; set; }
}

/// <summary>
/// A40 (TFIN, D-7) — the organization's opening balance date: the "as at" date of customer / supplier opening balances and
/// opening stock imported at go-live. The caller's organization; other modules read any organization's through
/// <see cref="IOpeningBalanceDateProvider"/>.
/// </summary>
public interface IOpeningBalanceDateService
{
    Task<DateOnly?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets (or, with null, clears) the date. Refused (409) when opening documents already imported would fall after the new
    /// date, or — for a clear — when any exist: their date is what they were imported "as at". A42 D-11: also refused (409)
    /// while an <see cref="IOpeningDateLockSource"/> locks it, unless the new date is the locked date.
    /// </summary>
    Task<DateOnly?> SetAsync(DateOnly? date, int userId, CancellationToken ct = default);
}

internal sealed class OpeningBalanceDateService : IOpeningBalanceDateService, IOpeningBalanceDateProvider
{
    internal static readonly DateOnly Earliest = new(2000, 1, 1);

    private readonly FinanceDbContext         _db;
    private readonly TimeProvider             _clock;
    private readonly IOpeningStockDateSource? _openingStock;
    private readonly IReadOnlyList<IOpeningDateLockSource> _locks;

    /// <param name="openingStock">QA-R1 — Inventory's posted opening stock dates; optional (a host without Inventory has none).</param>
    /// <param name="lockSources">
    /// A42 D-11 — everything that pins the date (e.g. Accounting once GL opening balances are posted). While any returns a
    /// date, the opening balance date can be neither cleared nor changed to another date.
    /// </param>
    public OpeningBalanceDateService(
        FinanceDbContext db, TimeProvider? clock = null, IOpeningStockDateSource? openingStock = null,
        IEnumerable<IOpeningDateLockSource>? lockSources = null)
    {
        _db           = db;
        _clock        = clock ?? TimeProvider.System;
        _openingStock = openingStock;
        _locks        = lockSources?.ToList() ?? [];
    }

    public Task<DateOnly?> GetAsync(CancellationToken ct = default) => GetAsync(_db.TenantContext.OrganizationId, ct);

    /// <summary><see cref="IOpeningBalanceDateProvider"/> — explicit organization, whoever the caller is.</summary>
    public async Task<DateOnly?> GetAsync(Guid organizationId, CancellationToken ct = default) =>
        await _db.OrgSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => s.OpeningBalanceDate)
            .FirstOrDefaultAsync(ct);

    public async Task<DateOnly?> SetAsync(DateOnly? date, int userId, CancellationToken ct = default)
    {
        if (date is { } d && d < Earliest)
            throw new BadRequestException($"The opening balance date cannot be before {Earliest:yyyy-MM-dd}.");

        var org = _db.TenantContext.OrganizationId;
        await EnsureNotLockedAsync(org, date, ct);
        await EnsureOpeningDocumentsFitAsync(org, date, ct);

        for (var attempt = 1; ; attempt++)
        {
            var row = await _db.OrgSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == org, ct);
            var now = _clock.GetUtcNow().UtcDateTime;

            if (row is null)
            {
                row = new FinanceOrgSettings { OrganizationId = org, OpeningBalanceDate = date, CreatedBy = userId, CreatedDate = now };
                _db.OrgSettings.Add(row);
            }
            else
            {
                row.OpeningBalanceDate = date;
                row.ModifiedBy         = userId;
                row.ModifiedDate       = now;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return row.OpeningBalanceDate;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Two first saves for one organization: the unique index refused the second insert. Read the winner's row.
                _db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// A42 D-11 — a source's locked date pins the opening balance date: clearing it, or setting any other date, is a 409.
    /// Setting it to the locked date itself (a no-op save) is allowed. Sources are asked in registration order.
    /// </summary>
    private async Task EnsureNotLockedAsync(Guid org, DateOnly? date, CancellationToken ct)
    {
        foreach (var source in _locks)
        {
            if (await source.GetLockedDateAsync(org, ct) is not { } locked || date == locked) continue;
            throw new ConflictException(
                $"The opening balance date is locked by {source.Description} posted as at "
              + $"{locked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        }
    }

    private async Task EnsureOpeningDocumentsFitAsync(Guid org, DateOnly? date, CancellationToken ct)
    {
        var sales = _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceType == SalesInvoiceSources.Opening && !i.IsDelete
                     && i.Status != SalesInvoiceStatuses.Cancelled);
        var bills = _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceType == SalesInvoiceSources.Opening && !i.IsDelete
                     && i.MatchStatus != InvoiceMatchStatus.Reversed);

        // QA-R1 — opening stock posted by Inventory's import counts as an opening document too.
        var stock = _openingStock is null ? null : await _openingStock.GetLatestAsync(org, ct);

        if (date is not { } d)
        {
            var count = await sales.CountAsync(ct) + await bills.CountAsync(ct) + (stock is null ? 0 : 1);
            if (count > 0)
                throw new ConflictException(
                    "Opening balances were already imported as at the opening balance date, so it cannot be cleared.");
            return;
        }

        var end   = d.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var later = await sales.Where(i => i.InvoiceDate >= end).Select(i => (DateTime?)i.InvoiceDate).MaxAsync(ct);
        var laterBill = await bills.Where(i => i.InvoiceDate >= end).Select(i => (DateTime?)i.InvoiceDate).MaxAsync(ct);
        DateTime? laterStock = stock is { } s && s > d ? s.ToDateTime(TimeOnly.MinValue) : null;
        var latest = new[] { later, laterBill, laterStock }.Max();

        if (latest is { } l)
            throw new ConflictException(
                $"Opening balance documents dated up to {l.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} were already imported; "
              + "the opening balance date cannot be earlier than that.");
    }

    /// <summary>yyyy-MM-dd, or null for blank. Anything else is a 400.</summary>
    internal static DateOnly? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new BadRequestException($"'{value}' is not a date in the form yyyy-MM-dd.");
    }

    internal static string? Format(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
