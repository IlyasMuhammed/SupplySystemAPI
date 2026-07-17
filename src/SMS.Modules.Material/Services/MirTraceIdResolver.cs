using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

internal sealed class MirProjectTraceIdResolver : MirTraceIdResolverBase { public override string InterfaceCode => "MIR_PROJECT"; public MirProjectTraceIdResolver(MaterialDbContext db) : base(db) { } }
internal sealed class MirGeneralTraceIdResolver : MirTraceIdResolverBase { public override string InterfaceCode => "MIR_GENERAL"; public MirGeneralTraceIdResolver(MaterialDbContext db) : base(db) { } }

internal abstract class MirTraceIdResolverBase : ITraceIdResolver
{
    public abstract string InterfaceCode { get; }

    private readonly MaterialDbContext _db;
    protected MirTraceIdResolverBase(MaterialDbContext db) => _db = db;

    public async Task<Guid?> ResolveTraceIdAsync(Guid documentId) =>
        await _db.MaterialIssueRequests
            .Where(m => m.UUID == documentId)
            .Select(m => (Guid?)m.TraceId)
            .FirstOrDefaultAsync();
}
