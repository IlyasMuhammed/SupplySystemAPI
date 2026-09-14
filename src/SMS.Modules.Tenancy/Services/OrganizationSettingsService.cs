using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

internal sealed class OrganizationSettingsService : IOrganizationSettingsService
{
    private readonly TenancyDbContext _db;

    public OrganizationSettingsService(TenancyDbContext db) => _db = db;

    public async Task<int> GetAckLinkExpiryDaysAsync(Guid organizationId)
    {
        var days = await _db.OrganizationSettings
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => (int?)s.AckLinkExpiryDays)
            .FirstOrDefaultAsync();

        if (days is null
            || days < OrganizationSettingsDefaults.AckLinkExpiryMinDays
            || days > OrganizationSettingsDefaults.AckLinkExpiryMaxDays)
            return OrganizationSettingsDefaults.AckLinkExpiryDefaultDays;

        return days.Value;
    }

    public async Task<bool> UpdateAckLinkExpiryDaysAsync(Guid organizationId, int days, int modifiedBy)
    {
        if (days < OrganizationSettingsDefaults.AckLinkExpiryMinDays
            || days > OrganizationSettingsDefaults.AckLinkExpiryMaxDays)
            return false;

        var settings = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId);

        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = organizationId };
            _db.OrganizationSettings.Add(settings);
        }

        settings.AckLinkExpiryDays = days;
        settings.ModifiedBy        = modifiedBy;
        settings.ModifiedDate      = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }
}
