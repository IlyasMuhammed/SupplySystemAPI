using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

// PV-007 — lets Inventory's variant-delete flow ask "has this variant ever appeared on an MIR
// line" without a project reference back into Material.
internal sealed class MirLineVariantReferenceChecker : IVariantReferenceChecker
{
    private readonly MaterialDbContext _db;
    public MirLineVariantReferenceChecker(MaterialDbContext db) => _db = db;

    public Task<bool> IsVariantReferencedAsync(Guid variantUuid) =>
        _db.MaterialIssueRequestDetails.AnyAsync(l => l.VariantUuid == variantUuid);
}
