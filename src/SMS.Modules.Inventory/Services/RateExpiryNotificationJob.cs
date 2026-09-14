using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

// RC-007 (FSD Addendum 28) — daily Hangfire recurring job. Warns 30 days ahead of a rate's
// EffectiveTo, unless a successor rate is already scheduled for the same supplier+variant
// (EffectiveFrom > this row's EffectiveTo). Each expiring rate notifies exactly once — dedup via
// ExpiryNotifiedAt, stamped whether or not a notification was actually sent (a skipped row, because
// a successor already covers it, should never be re-evaluated on a later run either).
internal sealed class RateExpiryNotificationJob
{
    private readonly InventoryDbContext _db;
    private readonly ISupplierNameLookupService _supplierNames;
    private readonly IUserQueryService _userQuery;
    private readonly INotificationService _notifications;

    private const int WarningWindowDays = 30;

    public RateExpiryNotificationJob(
        InventoryDbContext db, ISupplierNameLookupService supplierNames,
        IUserQueryService userQuery, INotificationService notifications)
    {
        _db            = db;
        _supplierNames = supplierNames;
        _userQuery     = userQuery;
        _notifications = notifications;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var today     = DateTime.UtcNow.Date;
        var windowEnd = today.AddDays(WarningWindowDays);

        // No org scoped yet — bare recurring job sees every organization (see StaleRateAlertJob).
        var expiringRows = await _db.VariantSuppliers
            .Where(x => x.IsActive && x.ExpiryNotifiedAt == null
                     && x.EffectiveTo != null && x.EffectiveTo >= today && x.EffectiveTo <= windowEnd)
            .Select(x => new
            {
                x.Id,
                x.Uuid,
                x.OrganizationId,
                x.SupplierId,
                x.VariantId,
                EffectiveTo = x.EffectiveTo!.Value,
                VariantSku  = x.Variant.Sku,
                VariantName = x.Variant.VariantName
            })
            .ToListAsync();

        if (expiringRows.Count == 0) return;

        // A "successor exists" candidate set — any other row sharing the same supplier+variant,
        // regardless of org (pairs are already org-distinct in practice since Supplier/Variant ids
        // are tenant-owned), fetched in one batch rather than per-row.
        var supplierIds = expiringRows.Select(r => r.SupplierId).Distinct().ToList();
        var variantIds  = expiringRows.Select(r => r.VariantId).Distinct().ToList();
        var candidates = await _db.VariantSuppliers
            .Where(x => supplierIds.Contains(x.SupplierId) && variantIds.Contains(x.VariantId))
            .Select(x => new { x.SupplierId, x.VariantId, x.EffectiveFrom })
            .ToListAsync();
        var successorFromsByPair = candidates
            .GroupBy(x => (x.SupplierId, x.VariantId))
            .ToDictionary(g => g.Key, g => g.Select(x => x.EffectiveFrom).ToList());

        var entityIds = expiringRows.Select(r => r.Id).ToList();
        var entities = await _db.VariantSuppliers
            .Where(x => entityIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        var now = DateTime.UtcNow;

        foreach (var orgGroup in expiringRows.GroupBy(r => r.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = orgGroup.Key;
            try
            {
                var names = await _supplierNames.GetNamesAsync(orgGroup.Select(r => r.SupplierId).Distinct().ToList());
                var recipients = await _userQuery.GetActiveUsersByRoleAsync((int)EnumRole.ProcurementManager);

                foreach (var row in orgGroup)
                {
                    var hasSuccessor = successorFromsByPair.TryGetValue((row.SupplierId, row.VariantId), out var froms)
                        && froms.Any(f => f > row.EffectiveTo);

                    if (!hasSuccessor)
                    {
                        var supplierName = names.TryGetValue(row.SupplierId, out var n) ? n : "(unknown supplier)";
                        var daysLeft = (row.EffectiveTo.Date - today).Days;
                        var message = $"{supplierName} — {row.VariantName} ({row.VariantSku}) rate expires in {daysLeft} day(s), on {row.EffectiveTo:dd MMM yyyy}.";

                        foreach (var recipient in recipients)
                        {
                            await _notifications.TryCreateAsync(new NotificationRequest(
                                UserId:        recipient.UserId,
                                Type:          "RATE_EXPIRING",
                                Category:      "Inventory",
                                Title:         "Rate Expiring",
                                Message:       message,
                                EntityType:    "VariantSupplier",
                                EntityUuid:    row.Uuid.ToString(),
                                NavigationUrl: "/portal/pages/suppliers/rate-cards"
                            ));
                        }
                    }

                    if (entities.TryGetValue(row.Id, out var entity))
                        entity.ExpiryNotifiedAt = now;
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        await _db.SaveChangesAsync();
    }
}
