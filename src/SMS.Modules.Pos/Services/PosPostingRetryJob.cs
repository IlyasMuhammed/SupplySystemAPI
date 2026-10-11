using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 E4 — every 15 minutes: orders whose stock or finance posting is still pending are posted again (idempotent), with
/// backoff on <c>PostingAttempts</c> (15 min × 2^(n−1), at most a day). Always per organization inside a
/// <see cref="HangfireTenantScope"/> — also when every organization has POS — because Inventory's ledger running balance
/// and the master product ledger's org stamp come from the ambient tenant (INV R-6), and Finance refuses a posting for an
/// organization other than the caller's. Organizations without MODULE_POS are left out of the batch.
/// </summary>
internal sealed class PosPostingRetryJob
{
    internal const string RecurringJobId = "pos-posting-retry";
    internal const string Cron = "*/15 * * * *";
    internal const int BatchSize = 200;

    private readonly PosDbContext       _db;
    private readonly IPosPostingService _posting;
    private readonly IModuleGate?       _gate;
    private readonly TimeProvider       _clock;
    private readonly ILogger            _log;

    public PosPostingRetryJob(
        PosDbContext db, IPosPostingService posting, IModuleGate? gate = null, TimeProvider? clock = null, ILogger<PosPostingRetryJob>? log = null)
    {
        _db = db; _posting = posting; _gate = gate; _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    internal static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(15d * Math.Pow(2, Math.Max(0, attempts - 1)), TimeSpan.FromDays(1).TotalMinutes));

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(600)]
    public async Task<int> RunAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var pending = await _db.Orders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => !o.StockPosted || !o.FinancePosted)
            .Select(o => new { o.Id, o.OrganizationId, o.PostingAttempts, o.LastPostingAttemptAt })
            .ToListAsync();
        var skipped = await _gate.SkippedAmongAsync(pending.Select(o => o.OrganizationId), ModuleCodes.Pos, _log, nameof(PosPostingRetryJob));
        var due = pending
            .Where(o => !skipped.Contains(o.OrganizationId)
                        && (o.LastPostingAttemptAt is not { } last || now - last >= Backoff(o.PostingAttempts)))
            .OrderBy(o => o.Id).Take(BatchSize).ToList();

        var done = 0;
        foreach (var org in due.GroupBy(o => o.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                // Id order: a sale before its return (a return waits for the sale's invoice).
                foreach (var item in org.OrderBy(o => o.Id))
                {
                    var order = await _db.Orders.Include(o => o.Lines).Include(o => o.Payments)
                        .FirstOrDefaultAsync(o => o.Id == item.Id && o.OrganizationId == org.Key);
                    if (order is null || (order.StockPosted && order.FinancePosted)) continue;
                    await _posting.PostAsync(order);
                    if (order.StockPosted && order.FinancePosted) done++;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "POS posting retry failed for organization {OrganizationId}.", org.Key);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
                _db.ChangeTracker.Clear();
            }
        }
        if (due.Count > 0) _log.LogInformation("POS posting retry: {Done} of {Due} order(s) fully posted.", done, due.Count);
        return done;
    }
}

/// <summary>
/// A38 E4 / D-9 — daily: per active store, the variants with negative available stock in its warehouse → one notification
/// per store to each of its SUPERVISOR staff (none ⇒ nothing sent). Per organization with MODULE_POS, in its tenant scope.
/// </summary>
internal sealed class NegativeStockReportJob
{
    internal const string RecurringJobId = "pos-negative-stock-report";
    internal const string Cron = "0 6 * * *";
    internal const string NotificationType = "POS_NEGATIVE_STOCK_REPORT";
    internal const int MaxListed = 10;

    private readonly PosDbContext          _db;
    private readonly IPosStockPoster?      _stock;
    private readonly IPosCatalogSource?    _catalog;
    private readonly INotificationService? _notifications;
    private readonly IModuleGate?          _gate;
    private readonly ILogger               _log;

    public NegativeStockReportJob(
        PosDbContext db, IPosStockPoster? stock = null, IPosCatalogSource? catalog = null, INotificationService? notifications = null,
        IModuleGate? gate = null, ILogger<NegativeStockReportJob>? log = null)
    {
        _db = db; _stock = stock; _catalog = catalog; _notifications = notifications; _gate = gate;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Number of stores reported.</summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        if (_stock is null || _notifications is null) return 0;
        var stores = await _db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.IsActive).ToListAsync();
        var skipped = await _gate.SkippedAmongAsync(stores.Select(s => s.OrganizationId), ModuleCodes.Pos, _log, nameof(NegativeStockReportJob));
        var reported = 0;
        foreach (var org in stores.Where(s => !skipped.Contains(s.OrganizationId)).GroupBy(s => s.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                foreach (var store in org)
                {
                    var negatives = (await _stock.GetLevelsAsync(org.Key, store.WarehouseUuid)).Where(l => l.Available < 0m).ToList();
                    if (negatives.Count == 0) continue;
                    var names = _catalog is null ? [] : (await _catalog.GetByVariantUuidsAsync(org.Key, negatives.Select(n => n.VariantUuid).ToList()))
                        .ToDictionary(v => v.VariantUuid, v => v.Sku);
                    var listed = string.Join(", ", negatives.OrderBy(n => n.Available).Take(MaxListed)
                        .Select(n => $"{names.GetValueOrDefault(n.VariantUuid) ?? n.VariantUuid.ToString()} ({n.Available:0.##})"));
                    var more = negatives.Count > MaxListed ? $" and {negatives.Count - MaxListed} more" : string.Empty;
                    var sent = await PosSupervisors.NotifyAsync(_db, _notifications, org.Key, store.Id, NotificationType,
                        "POS negative stock", $"{store.Name}: {negatives.Count} product(s) below zero in {store.WarehouseName}: {listed}{more}.",
                        "PosStore", store.Uuid, $"/portal/pages/pos/stores/{store.Uuid}");
                    if (sent > 0) reported++;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "POS negative stock report failed for organization {OrganizationId}.", org.Key);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
        return reported;
    }
}
