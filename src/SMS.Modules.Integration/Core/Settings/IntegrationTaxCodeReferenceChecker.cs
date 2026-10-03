using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>
/// Tells Finance whether an organization's tax code is mapped to a QuickBooks tax code, so Finance refuses to
/// rename it: a code mapping is matched by the code's text (S-11), so after a rename every line with the new
/// text would be Blocked as TAX_UNMAPPED until someone noticed and mapped it again.
/// <para>
/// Every connection of the organization counts, whatever its status — a disconnected company's mappings
/// apply again on reconnect (one connection row per organization and provider). Limited to the organization
/// explicitly rather than through the tenant filter, which a super admin bypasses. Codes are compared as the
/// mapping screen stores them: trimmed and upper-cased.
/// </para>
/// </summary>
internal sealed class IntegrationTaxCodeReferenceChecker : ITaxCodeReferenceChecker
{
    private readonly IntegrationDbContext _db;

    public IntegrationTaxCodeReferenceChecker(IntegrationDbContext db) => _db = db;

    public async Task<string?> FindCodeReferenceAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        if (SyncPayloads.NormalizeTaxCode(code) is not { } normalized) return null;

        var mapped = await _db.TaxCodeMappings.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(m => m.OrganizationId == organizationId && m.SourceTaxCode == normalized, ct);

        return mapped ? "the QuickBooks tax mappings" : null;
    }
}
