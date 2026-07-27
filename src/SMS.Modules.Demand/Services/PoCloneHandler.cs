using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

internal sealed class PoCloneHandler : IDocumentCloneHandler
{
    public string InterfaceCode => "PO";

    private readonly DemandDbContext _db;

    public PoCloneHandler(DemandDbContext db) => _db = db;

    public async Task<Guid> CloneDocumentAsync(Guid documentId)
    {
        var source = await _db.PurchaseOrders
            .Include(p => p.Lines.OrderBy(l => l.LineNo))
            .Include(p => p.PrLinks)
            .FirstOrDefaultAsync(p => p.UUID == documentId && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", documentId);

        var now      = DateTime.UtcNow;
        var year     = now.Year;
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.PurchaseOrders
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        var newPoNumber = $"PO-{year}-{(count + 1):D5}";

        var clone = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            // TL-006: a reissue is a continuation of the same document, not a new chain.
            TraceId      = source.TraceId,
            PoNumber     = newPoNumber,
            Title        = source.Title,
            SupplierId   = source.SupplierId,
            SupplierName = source.SupplierName,
            Status       = "DRAFT",
            TotalAmount  = source.TotalAmount,
            DeliveryDate = source.DeliveryDate,
            Notes        = source.Notes,
            IsActive     = true,
            CreatedBy    = source.CreatedBy,
            CreatedDate  = now,
            Lines        = source.Lines.Select((l, i) => new PurchaseOrderLine
            {
                UUID             = Guid.NewGuid(),
                LineNo           = i + 1,
                SourcePrLineUuid = l.SourcePrLineUuid,
                ProductUuid      = l.ProductUuid,
                ItemDescription  = l.ItemDescription,
                Specification    = l.Specification,
                UnitOfMeasure    = l.UnitOfMeasure,
                Quantity         = l.Quantity,
                UnitPrice        = l.UnitPrice,
                LineTotal        = l.LineTotal,
                RequiredDate     = l.RequiredDate,
                LineNotes        = l.LineNotes,
                BudgetCode       = l.BudgetCode
            }).ToList(),
            PrLinks = source.PrLinks.Select(link => new PurchaseOrderPrLink
            {
                PrUuid = link.PrUuid
            }).ToList()
        };

        _db.PurchaseOrders.Add(clone);
        await _db.SaveChangesAsync();
        return clone.UUID;
    }
}
