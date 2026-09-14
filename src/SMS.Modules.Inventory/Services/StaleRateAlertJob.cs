using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

// RC-007 (FSD Addendum 28) — monthly Hangfire recurring job. Scans every organization's
// VariantSuppliers for rates that haven't been reviewed within the configured threshold (or never
// reviewed at all) and notifies each affected organization's own Procurement Managers with a
// per-supplier stale count. See VariantSupplierService's grid ComputeStatus for the STALE badge,
// which shares the same threshold source but (intentionally) treats never-reviewed as ACTIVE —
// this job's own detection is a separate query that does include never-reviewed, per this ticket.
internal sealed class StaleRateAlertJob
{
    private readonly InventoryDbContext _db;
    private readonly ISupplierNameLookupService _supplierNames;
    private readonly IUserQueryService _userQuery;
    private readonly INotificationService _notifications;
    private readonly IConfiguration _configuration;

    public StaleRateAlertJob(
        InventoryDbContext db, ISupplierNameLookupService supplierNames,
        IUserQueryService userQuery, INotificationService notifications, IConfiguration configuration)
    {
        _db            = db;
        _supplierNames = supplierNames;
        _userQuery     = userQuery;
        _notifications = notifications;
        _configuration = configuration;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var thresholdDays = int.TryParse(_configuration["RateCard:StaleThresholdDays"], out var d) ? d : 180;
        var cutoff = DateTime.UtcNow.Date.AddDays(-thresholdDays);

        // No org scoped yet here — TenantContext.IsSuperAdmin is true for a bare recurring job
        // (see HangfireTenantScope), so this naturally scans every organization in one query.
        var staleRows = await _db.VariantSuppliers
            .Where(x => x.IsActive && (x.LastReviewedAt == null || x.LastReviewedAt < cutoff))
            .Select(x => new { x.OrganizationId, x.SupplierId })
            .ToListAsync();

        if (staleRows.Count == 0) return;

        foreach (var orgGroup in staleRows.GroupBy(x => x.OrganizationId))
        {
            // Scope every query/write below to this one org, so recipient lookup and any future
            // per-org write lands correctly — same AsyncLocal TenantPropagatingJobFilter uses for
            // enqueue-time jobs, set manually here since this job iterates orgs itself.
            HangfireTenantScope.OrganizationId = orgGroup.Key;
            try
            {
                var bySupplier = orgGroup
                    .GroupBy(x => x.SupplierId)
                    .Select(g => new { SupplierId = g.Key, Count = g.Count() })
                    .OrderByDescending(s => s.Count)
                    .ToList();

                var supplierIds = bySupplier.Select(s => s.SupplierId).ToList();
                var names = await _supplierNames.GetNamesAsync(supplierIds);

                var lines = bySupplier.Select(s =>
                    $"{(names.TryGetValue(s.SupplierId, out var n) ? n : "(unknown supplier)")}: {s.Count} stale rate(s)");
                var totalCount = orgGroup.Count();
                var message = $"{totalCount} supplier rate(s) are overdue for review — {string.Join("; ", lines)}.";

                var recipients = await _userQuery.GetActiveUsersByRoleAsync((int)EnumRole.ProcurementManager);
                foreach (var recipient in recipients)
                {
                    await _notifications.TryCreateAsync(new NotificationRequest(
                        UserId:        recipient.UserId,
                        Type:          "STALE_RATE_ALERT",
                        Category:      "Inventory",
                        Title:         "Stale Supplier Rates Need Review",
                        Message:       message,
                        NavigationUrl: "/portal/pages/suppliers/rate-cards"
                    ));
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
    }
}
