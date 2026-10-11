using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A39 D-11 — daily 00:30 UTC: ACTIVE store credit notes past their expiry lose their balance (EXPIRE transaction, status
/// EXPIRED, hub CreditNoteExpired to the issuing store). Per organization with MODULE_POS and FEATURE_POS_CREDIT_NOTES, in
/// its <see cref="HangfireTenantScope"/>. Returns the number of notes expired.
/// </summary>
internal sealed class StoreCreditExpiryJob
{
    internal const string RecurringJobId = "pos-store-credit-expiry";
    internal const string Cron = "30 0 * * *";

    private readonly PosDbContext     _db;
    private readonly IPosHubNotifier? _hub;
    private readonly IModuleGate?     _gate;
    private readonly TimeProvider     _clock;
    private readonly ILogger          _log;

    public StoreCreditExpiryJob(PosDbContext db, IPosHubNotifier? hub = null, IModuleGate? gate = null, TimeProvider? clock = null,
        ILogger<StoreCreditExpiryJob>? log = null)
    {
        _db = db; _hub = hub; _gate = gate; _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(600)]
    public async Task<int> RunAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var due = await _db.StoreCreditNotes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Status == PosStoreCreditStatus.Active && n.ExpiresAt != null && n.ExpiresAt <= now)
            .Select(n => new { n.Id, n.OrganizationId }).ToListAsync();
        var orgs = due.Select(d => d.OrganizationId).Distinct().ToList();
        var skipped = new HashSet<Guid>(await _gate.SkippedAmongAsync(orgs, ModuleCodes.Pos, _log, nameof(StoreCreditExpiryJob)));
        skipped.UnionWith(await _gate.SkippedAmongAsync(orgs.Where(o => !skipped.Contains(o)), PosFeatureCodes.CreditNotes, _log, nameof(StoreCreditExpiryJob)));

        var expired = 0;
        foreach (var org in due.Where(d => !skipped.Contains(d.OrganizationId)).GroupBy(d => d.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                var stores = await _db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org.Key).ToDictionaryAsync(s => s.Id, s => s.Uuid);
                foreach (var item in org)
                {
                    var cn = await _db.StoreCreditNotes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == item.Id && n.OrganizationId == org.Key);
                    if (cn is null || cn.Status != PosStoreCreditStatus.Active || !cn.IsExpiredAt(now)) continue;
                    var amount = cn.RemainingBalance;
                    cn.Transactions.Add(PosStoreCreditService.Txn(cn, PosStoreCreditTxnType.Expire, -amount, 0m, null, cn.IssuedStoreId, 0, now, "Expired."));
                    cn.RemainingBalance = 0m;
                    cn.Status = PosStoreCreditStatus.Expired;
                    try
                    {
                        await _db.SaveChangesAsync();
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _db.ChangeTracker.Clear(); // redeemed at the same moment — the next run sees it again
                        continue;
                    }
                    expired++;
                    if (_hub is not null && stores.TryGetValue(cn.IssuedStoreId, out var storeUuid))
                        await _hub.StoreEventAsync(org.Key, storeUuid, PosHubEvents.CreditNoteExpired,
                            new { creditNoteUuid = cn.Uuid, number = cn.Number, customerUuid = cn.CustomerUuid, expiredAmount = amount });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Store credit expiry failed for organization {OrganizationId}.", org.Key);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
                _db.ChangeTracker.Clear();
            }
        }
        if (expired > 0) _log.LogInformation("Store credit expiry: {Count} note(s) expired.", expired);
        return expired;
    }
}

/// <summary>
/// A39 D-7 / D-11 — weekly Mon 06:00 UTC: per organization with MODULE_POS, the cross-store returns and exchanges of the past
/// 7 days per (original store → processing store) pair, as one notification (type POS_CROSS_STORE_REPORT) to every active
/// user holding POS_RETURN_REPORTS — resolved per organization (the store staff plus the org's user search), never a role
/// broadcast (A38 R-1). Returns the number of notifications sent.
/// </summary>
internal sealed class CrossStoreReconciliationReport
{
    internal const string RecurringJobId = "pos-cross-store-report";
    internal const string Cron = "0 6 * * 1";
    internal const string NotificationType = "POS_CROSS_STORE_REPORT";
    internal const int UserSearchLimit = 100;

    private readonly PosDbContext          _db;
    private readonly IPosPinService?       _pins;
    private readonly INotificationService? _notifications;
    private readonly IModuleGate?          _gate;
    private readonly TimeProvider          _clock;
    private readonly ILogger               _log;

    public CrossStoreReconciliationReport(PosDbContext db, IPosPinService? pins = null, INotificationService? notifications = null,
        IModuleGate? gate = null, TimeProvider? clock = null, ILogger<CrossStoreReconciliationReport>? log = null)
    {
        _db = db; _pins = pins; _notifications = notifications; _gate = gate; _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        if (_pins is null || _notifications is null) return 0;
        var to = _clock.GetUtcNow().UtcDateTime;
        var from = to.AddDays(-7);
        var rows = await _db.Orders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.IsCrossStore && o.CreatedAt >= from && o.CreatedAt < to && o.OriginalStoreId != null)
            .Select(o => new
            {
                o.OrganizationId, OriginalStoreId = o.OriginalStoreId!.Value, o.StoreId, o.OrderType,
                Amount = o.OrderType == PosOrderType.Exchange
                    ? -o.Lines.Where(l => l.Quantity < 0).Sum(l => l.LineTotal) - o.RestockingFeeAmount
                    : o.GrandTotal
            }).ToListAsync();
        var skipped = await _gate.SkippedAmongAsync(rows.Select(r => r.OrganizationId), ModuleCodes.Pos, _log, nameof(CrossStoreReconciliationReport));

        var sent = 0;
        foreach (var org in rows.Where(r => !skipped.Contains(r.OrganizationId)).GroupBy(r => r.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                var stores = await _db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org.Key)
                    .ToDictionaryAsync(s => s.Id, s => s.Code);
                var pairs = org.GroupBy(r => (r.OriginalStoreId, r.StoreId))
                    .Select(g => $"sold at {stores.GetValueOrDefault(g.Key.OriginalStoreId)} → returned at {stores.GetValueOrDefault(g.Key.StoreId)}: " +
                                 $"{g.Count()} return(s), {g.Sum(x => x.Amount):0.00}")
                    .OrderBy(x => x).ToList();
                var message = $"Cross-store returns {from:yyyy-MM-dd} – {to.AddDays(-1):yyyy-MM-dd}: " + string.Join("; ", pairs) + ".";
                foreach (var userId in await RecipientsAsync(org.Key))
                {
                    await _notifications.TryCreateAsync(new NotificationRequest(userId, NotificationType, "POS cross-store returns", message,
                        "POS", "PosOrder", null, "/portal/pages/pos/orders?orderType=RETURN,EXCHANGE"));
                    sent++;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "POS cross-store report failed for organization {OrganizationId}.", org.Key);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
        return sent;
    }

    /// <summary>Active users of the organization holding POS_RETURN_REPORTS: every store's staff + the org's user search (≤ 100).</summary>
    internal async Task<IReadOnlyList<int>> RecipientsAsync(Guid org)
    {
        var staff = await _db.StoreStaff.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org && s.IsActive)
            .Select(s => s.UserId).Distinct().ToListAsync();
        var users = (await _pins!.GetUsersAsync(org, staff)).Concat(await _pins.SearchUsersAsync(org, null, UserSearchLimit));
        return users.Where(u => u.IsActive && u.Permissions.Contains(PosPermissions.ReturnReports, StringComparer.OrdinalIgnoreCase))
            .Select(u => u.UserId).Distinct().OrderBy(id => id).ToList();
    }
}
