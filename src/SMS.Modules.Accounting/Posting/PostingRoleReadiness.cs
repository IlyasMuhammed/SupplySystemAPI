using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A48 (AP, D-AP-12) — <see cref="IPostingRoleReadiness"/> for the internal GL: when the GL target is active for the
/// organization, a role whose A42 default has no account assigned is missing (the GL post would fail UNMAPPED_ROLE).
/// When the GL is not active nothing is missing (QuickBooks posts the document payload, not roles).
/// </summary>
internal sealed class GlPostingRoleReadiness : IPostingRoleReadiness
{
    private readonly AccountingDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly InternalGlPostingTarget _gl;

    public GlPostingRoleReadiness(AccountingDbContext db, ITenantContext tenant, InternalGlPostingTarget gl)
    {
        _db = db;
        _tenant = tenant;
        _gl = gl;
    }

    public async Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        if (roles.Count == 0 || !await _gl.IsActiveAsync(ct)) return [];
        var org = _tenant.OrganizationId;
        var keys = roles.Distinct().ToList();
        var mapped = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && keys.Contains(d.DefaultKey) && d.AccountId != null)
            .Select(d => d.DefaultKey).ToListAsync(ct);
        return keys.Where(k => !mapped.Contains(k)).ToList();
    }
}
