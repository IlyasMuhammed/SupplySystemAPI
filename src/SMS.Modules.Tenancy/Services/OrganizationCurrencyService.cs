using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

// Implements the SMS.Shared.Common cross-module interface so SMS.Modules.Inventory can resolve an
// organization's default currency without a project reference to Tenancy — mirrors
// OrganizationStatusService.
internal sealed class OrganizationCurrencyService : IOrganizationCurrencyService
{
    private readonly TenancyDbContext _db;

    public OrganizationCurrencyService(TenancyDbContext db) => _db = db;

    public async Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) =>
        await _db.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => o.BaseCurrency)
            .FirstOrDefaultAsync();
}
