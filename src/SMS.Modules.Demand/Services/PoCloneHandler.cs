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
    private readonly IPurchaseOrderLineFactory _lines;

    public PoCloneHandler(DemandDbContext db, IPurchaseOrderLineFactory? lines = null)
    {
        _db    = db;
        // A46 C5: a reissue's lines are made by the factory too (it copies the price and its source untouched).
        _lines = lines ?? new PurchaseOrderLineFactory(
            new PurchaseOrderLinePricingMiddleware(db, new PurchasePriceResolver(db)));
    }

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
            // A35 — the currency travels with the reissue; its rate does not (the reissue locks its own on approval).
            CurrencyId   = source.CurrencyId,
            DeliveryDate = source.DeliveryDate,
            OrderDate    = now.Date,
            Notes        = source.Notes,
            IsActive     = true,
            CreatedBy    = source.CreatedBy,
            CreatedDate  = now,
            PrLinks = source.PrLinks.Select(link => new PurchaseOrderPrLink
            {
                PrUuid = link.PrUuid
            }).ToList()
        };

        var lineNo = 1;
        foreach (var l in source.Lines)
        {
            var copy = await _lines.CreateLineAsync(clone, new CreateLineInput
            {
                Origin           = LineOrigin.Clone,
                LineNo           = lineNo++,
                SourcePrLineUuid = l.SourcePrLineUuid,
                VariantUuid      = l.VariantUuid,
                ItemDescription  = l.ItemDescription,
                Specification    = l.Specification,
                UnitOfMeasure    = l.UnitOfMeasure,
                Quantity         = l.Quantity,
                RequiredDate     = l.RequiredDate,
                LineNotes        = l.LineNotes,
                BudgetCode       = l.BudgetCode,
                CopyFrom         = l
            });
            clone.Lines.Add(copy.Line);
        }

        _db.PurchaseOrders.Add(clone);
        await _db.SaveChangesAsync();
        return clone.UUID;
    }
}

