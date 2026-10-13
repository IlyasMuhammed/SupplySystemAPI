using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Services;

/// <summary>A37 D-18 — open leads and opportunities, shown before an administrator switches the CRM off.</summary>
internal sealed class CrmImpactProvider : IModuleImpactProvider
{
    private readonly CrmDbContext _db;

    public CrmImpactProvider(CrmDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Crm;

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var open = LeadStatuses.Open.ToArray();
        var leads = await _db.Leads.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.OrganizationId == organizationId && !l.IsDeleted && open.Contains(l.Status), ct);
        var opportunities = await _db.Opportunities.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(o => o.OrganizationId == organizationId && !o.IsDeleted && o.Status == OpportunityStatuses.Open, ct);
        var items = new List<ModuleImpactItem>();
        if (leads > 0) items.Add(new ModuleImpactItem("Leads – Open", leads));
        if (opportunities > 0) items.Add(new ModuleImpactItem("Opportunities – Open", opportunities));
        return items;
    }
}
