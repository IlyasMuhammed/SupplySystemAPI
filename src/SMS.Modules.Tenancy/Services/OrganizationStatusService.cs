using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

// Implements the SMS.Shared.Common cross-module interface so SMS.Modules.Auth can check whether a
// user's organization is active at login time, without a project reference to Tenancy.
internal sealed class OrganizationStatusService : IOrganizationStatusService
{
    private readonly TenancyDbContext _db;

    public OrganizationStatusService(TenancyDbContext db) => _db = db;

    public async Task<bool> IsOrganizationActiveAsync(Guid organizationId) =>
        await _db.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => o.IsActive)
            .FirstOrDefaultAsync();
}
