using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Material.Services;

// Handles MIR_PROJECT and MIR_GENERAL — clone creates a DRAFT copy for reissue.
internal sealed class MirProjectCloneHandler  : MirCloneHandlerBase { public override string InterfaceCode => "MIR_PROJECT"; public MirProjectCloneHandler(MaterialDbContext db) : base(db) { } }
internal sealed class MirGeneralCloneHandler  : MirCloneHandlerBase { public override string InterfaceCode => "MIR_GENERAL"; public MirGeneralCloneHandler(MaterialDbContext db) : base(db) { } }

internal abstract class MirCloneHandlerBase : IDocumentCloneHandler
{
    public abstract string InterfaceCode { get; }

    private readonly MaterialDbContext _db;

    protected MirCloneHandlerBase(MaterialDbContext db) => _db = db;

    public async Task<Guid> CloneDocumentAsync(Guid documentId)
    {
        var original = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == documentId && !m.IsDelete)
            ?? throw new NotFoundException("MIR", documentId);

        var clone = new MaterialIssueRequest
        {
            UUID           = Guid.NewGuid(),
            // TL-006: a reissue is a continuation of the same document, not a new chain.
            TraceId        = original.TraceId,
            RequestNo      = await GenerateRequestNoAsync(DateTime.UtcNow.Year),
            RequestType    = original.RequestType,
            ProjectId      = original.ProjectId,
            Department     = original.Department,
            MaintenanceRef = original.MaintenanceRef,
            RequestedBy    = original.RequestedBy,
            RequiredDate   = original.RequiredDate,
            Priority       = original.Priority,
            Purpose        = original.Purpose,
            Status         = "DRAFT",
            EstimatedValue = original.EstimatedValue,
            Notes          = original.Notes,
            IsActive       = true,
            CreatedBy      = original.RequestedBy,
            CreatedDate    = DateTime.UtcNow,
            Lines = original.Lines.Select((l, i) => new MaterialIssueRequestDetail
            {
                UUID               = Guid.NewGuid(),
                LineNo             = i + 1,
                VariantUuid        = l.VariantUuid,
                ItemDescription    = l.ItemDescription,
                UnitOfMeasure      = l.UnitOfMeasure,
                RequestedQty       = l.RequestedQty,
                UnitCost           = l.UnitCost,
                EstimatedLineValue = l.EstimatedLineValue,
                Purpose            = l.Purpose,
                Notes              = l.Notes
            }).ToList()
        };

        _db.MaterialIssueRequests.Add(clone);
        await _db.SaveChangesAsync();
        return clone.UUID;
    }

    private async Task<string> GenerateRequestNoAsync(int year)
    {
        var prefix = $"MIR-{year}-";
        var last = await _db.MaterialIssueRequests
            .Where(m => m.RequestNo.StartsWith(prefix))
            .OrderByDescending(m => m.RequestNo)
            .Select(m => m.RequestNo)
            .FirstOrDefaultAsync();

        int next = 1;
        if (last is not null && last.Length > prefix.Length &&
            int.TryParse(last[prefix.Length..], out var n))
            next = n + 1;

        return $"{prefix}{next:D5}";
    }
}
