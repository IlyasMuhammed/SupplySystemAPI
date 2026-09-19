using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal sealed class PurchaseOrderRepository : IPurchaseOrderRepository
{
    private readonly DemandDbContext _db;
    private readonly ILogger<PurchaseOrderRepository> _logger;
    // Optional: null in older unit tests that construct this repository directly without an
    // Inventory context — those tests don't exercise variant resolution. Production DI always
    // supplies the real InventoryDbContext since it's registered by the Inventory module.
    private readonly InventoryDbContext? _inv;

    public PurchaseOrderRepository(DemandDbContext db, ILogger<PurchaseOrderRepository> logger, InventoryDbContext? inv = null)
    {
        _db     = db;
        _logger = logger;
        _inv    = inv;
    }

    // ── Single-vendor conversion: all PR lines → one PO ──────────────────────

    public async Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == prUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseRequisition", prUuid);

        if (pr.Status != "APPROVED" && pr.Status != "PARTIALLY_CONVERTED")
            throw new UnprocessableEntityException(
                $"Only APPROVED requisitions can be converted to PO. Current status: {pr.Status}.");

        ValidateQuotationRequirements(pr.Lines);

        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);

        var defaultVariants = await ResolveDefaultVariantUuidsAsync(
            pr.Lines.Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value));

        var poLines = new List<PurchaseOrderLine>();
        int lineNo  = 1;
        foreach (var prLine in pr.Lines.OrderBy(l => l.LineNo))
        {
            poLines.Add(new PurchaseOrderLine
            {
                UUID             = Guid.NewGuid(),
                LineNo           = lineNo++,
                SourcePrLineUuid = prLine.UUID,
                VariantUuid      = prLine.ProductId.HasValue && defaultVariants.TryGetValue(prLine.ProductId.Value, out var vUuid) ? vUuid : null,
                ItemDescription  = prLine.ItemDescription,
                Specification    = prLine.Specification,
                UnitOfMeasure    = prLine.UnitOfMeasure,
                Quantity         = prLine.Quantity,
                UnitPrice        = prLine.EstimatedUnitPrice,
                LineTotal        = prLine.LineTotal,
                RequiredDate     = prLine.RequiredDate,
                BudgetCode       = prLine.BudgetCode
            });
            prLine.LineStatus = "FULLY_CONVERTED";
        }

        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            TraceId      = pr.TraceId,
            PoNumber     = poNumber,
            Title        = pr.PrTitle,
            SupplierId   = req.SupplierId,
            SupplierName = req.SupplierName,
            Status       = "DRAFT",
            TotalAmount  = poLines.Sum(l => l.LineTotal),
            DeliveryDate = req.DeliveryDate,
            Notes               = req.Notes,
            DeliveryWarehouseId = pr.WarehouseUuid,
            IsActive            = true,
            CreatedBy    = createdBy,
            CreatedDate  = now,
            Lines        = poLines,
            PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
        };

        _db.PurchaseOrders.Add(po);

        pr.Status       = "FULLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return po.UUID;
    }

    // ── Split-by-vendor conversion: each vendor gets their own PO ─────────────

    public async Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == prUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseRequisition", prUuid);

        if (pr.Status != "APPROVED" && pr.Status != "PARTIALLY_CONVERTED")
            throw new UnprocessableEntityException(
                $"Only APPROVED requisitions can be converted to PO. Current status: {pr.Status}.");

        var lineMap = pr.Lines.ToDictionary(l => l.UUID);

        var assignments = req.Lines
            .Where(a => lineMap.ContainsKey(a.PrLineUuid))
            .Select(a => (Assignment: a, PrLine: lineMap[a.PrLineUuid]))
            .ToList();

        if (assignments.Count == 0)
            throw new BadRequestException("No valid PR lines were specified for conversion.");

        ValidateQuotationRequirements(assignments.Select(x => x.PrLine));

        var grouped    = assignments.GroupBy(x => x.Assignment.SupplierId).ToList();
        var now        = DateTime.UtcNow;
        var poNumbers  = await GeneratePoNumbersAsync(now.Year, grouped.Count);
        var poUuids    = new List<Guid>();
        int poIndex    = 0;

        var defaultVariants = await ResolveDefaultVariantUuidsAsync(
            assignments.Where(a => a.PrLine.ProductId.HasValue).Select(a => a.PrLine.ProductId!.Value));

        foreach (var group in grouped)
        {
            var firstAssignment = group.First().Assignment;
            var poLines         = new List<PurchaseOrderLine>();
            int lineNo          = 1;

            foreach (var (_, prLine) in group)
            {
                poLines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = prLine.UUID,
                    VariantUuid      = prLine.ProductId.HasValue && defaultVariants.TryGetValue(prLine.ProductId.Value, out var vUuid) ? vUuid : null,
                    ItemDescription  = prLine.ItemDescription,
                    Specification    = prLine.Specification,
                    UnitOfMeasure    = prLine.UnitOfMeasure,
                    Quantity         = prLine.Quantity,
                    UnitPrice        = prLine.EstimatedUnitPrice,
                    LineTotal        = prLine.LineTotal,
                    RequiredDate     = prLine.RequiredDate,
                    BudgetCode       = prLine.BudgetCode
                });
                prLine.LineStatus = "FULLY_CONVERTED";
            }

            var po = new PurchaseOrder
            {
                UUID         = Guid.NewGuid(),
                TraceId      = pr.TraceId,
                PoNumber     = poNumbers[poIndex++],
                Title        = pr.PrTitle,
                SupplierId   = group.Key,
                SupplierName = firstAssignment.SupplierName,
                Status       = "DRAFT",
                TotalAmount  = poLines.Sum(l => l.LineTotal),
                DeliveryDate = req.DeliveryDate,
                Notes        = req.Notes,
                IsActive     = true,
                CreatedBy    = createdBy,
                CreatedDate  = now,
                Lines        = poLines,
                PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
            };

            _db.PurchaseOrders.Add(po);
            poUuids.Add(po.UUID);
        }

        pr.Status = pr.Lines.All(l => l.LineStatus == "FULLY_CONVERTED")
            ? "FULLY_CONVERTED"
            : "PARTIALLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return poUuids;
    }

    // ── Manual PO creation (with optional multi-PR consolidation) ─────────────

    public async Task<Guid> CreateAsync(CreatePoRequest req, int createdBy)
    {
        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);

        var po = new PurchaseOrder
        {
            UUID                  = req.PoUuid is { } gid && gid != Guid.Empty ? gid : Guid.NewGuid(),
            TraceId               = Guid.NewGuid(),
            PoNumber              = poNumber,
            Title                 = req.Title,
            SupplierId            = req.SupplierId,
            SupplierName          = req.SupplierName,
            Status                = "DRAFT",
            DeliveryDate          = req.DeliveryDate,
            DeliveryWarehouseId   = req.DeliveryWarehouseId,
            DeliveryWarehouseName = req.DeliveryWarehouseName,
            Notes                 = req.Notes,
            InternalNotes         = req.InternalNotes,
            IsActive              = true,
            CreatedBy             = createdBy,
            CreatedDate           = now
        };

        if (req.PrIds?.Count > 0)
        {
            var prs = await _db.PurchaseRequisitions
                .Include(p => p.Lines)
                .Where(p => req.PrIds.Contains(p.UUID) && !p.IsDelete)
                .ToListAsync();

            var invalidPrs = prs
                .Where(p => p.Status != "APPROVED" && p.Status != "PARTIALLY_CONVERTED")
                .Select(p => p.PrNumber)
                .ToList();
            if (invalidPrs.Count > 0)
                throw new UnprocessableEntityException(
                    $"Requisitions must be APPROVED before conversion: {string.Join(", ", invalidPrs)}");

            // TL-006: inherit trace_id from the first linked PR (request order); warn if the
            // consolidated PRs don't all belong to the same trace chain.
            var orderedPrs = req.PrIds
                .Select(id => prs.FirstOrDefault(p => p.UUID == id))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();
            po.TraceId = orderedPrs[0].TraceId;
            var distinctTraceIds = orderedPrs.Select(p => p.TraceId).Distinct().ToList();
            if (distinctTraceIds.Count > 1)
                _logger.LogWarning(
                    "Manual PO consolidates {Count} purchase requisitions spanning {ChainCount} different trace chains: {TraceIds}. Using the first PR's trace_id {TraceId}.",
                    orderedPrs.Count, distinctTraceIds.Count, string.Join(", ", distinctTraceIds), po.TraceId);

            foreach (var pr in prs)
                ValidateQuotationRequirements(pr.Lines);

            var defaultVariants = await ResolveDefaultVariantUuidsAsync(
                prs.SelectMany(p => p.Lines).Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value));

            int lineNo = 1;
            foreach (var pr in prs)
            {
                foreach (var prLine in pr.Lines.OrderBy(l => l.LineNo))
                {
                    po.Lines.Add(new PurchaseOrderLine
                    {
                        UUID             = Guid.NewGuid(),
                        LineNo           = lineNo++,
                        SourcePrLineUuid = prLine.UUID,
                        VariantUuid      = prLine.ProductId.HasValue && defaultVariants.TryGetValue(prLine.ProductId.Value, out var vUuid) ? vUuid : null,
                        ItemDescription  = prLine.ItemDescription,
                        Specification    = prLine.Specification,
                        UnitOfMeasure    = prLine.UnitOfMeasure,
                        Quantity         = prLine.Quantity,
                        UnitPrice        = prLine.EstimatedUnitPrice,
                        LineTotal        = prLine.LineTotal,
                        RequiredDate     = prLine.RequiredDate,
                        BudgetCode       = prLine.BudgetCode
                    });
                    prLine.LineStatus = "FULLY_CONVERTED";
                }

                po.PrLinks.Add(new PurchaseOrderPrLink { PrUuid = pr.UUID });

                pr.Status       = "FULLY_CONVERTED";
                pr.ModifiedBy   = createdBy;
                pr.ModifiedDate = now;
            }
        }
        else if (req.Lines?.Count > 0)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            var variantPrices = await ResolveVariantPricesAsync(
                req.Lines.Where(l => l.VariantUuid.HasValue).Select(l => l.VariantUuid!.Value));

            int lineNo = 1;
            foreach (var line in req.Lines)
            {
                var unitPrice = ResolveLineUnitPrice(line.UnitPrice, line.VariantUuid, variantPrices);
                po.Lines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = line.SourcePrLineUuid,
                    VariantUuid      = line.VariantUuid,
                    ItemDescription  = line.ItemDescription,
                    Specification    = line.Specification,
                    UnitOfMeasure    = line.UnitOfMeasure,
                    Quantity         = line.Quantity,
                    UnitPrice        = unitPrice,
                    LineDiscountPct  = line.LineDiscountPct,
                    LineTotal        = line.Quantity * unitPrice,
                    RequiredDate     = line.RequiredDate,
                    LineNotes        = line.LineNotes,
                    BudgetCode       = line.BudgetCode,
                    WarehouseId      = line.WarehouseId,
                    WarehouseName    = line.WarehouseName,
                    RequiresInspection = line.RequiresInspection
                });
            }
        }

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();
        return po.UUID;
    }

    // ── A29-P5-03 §6.1/§6.3 — PO generated from a sale order line's deficit ──

    // Always exactly one line, for that variant — §6.2's later edits (extra items, splits) happen on
    // the PO itself afterwards. Numbered by the same generator as every other PO: a second scheme
    // would collide with it on the (OrganizationId, PoNumber) unique index.
    public async Task<CreatedPurchaseOrder> CreateFromSaleOrderDeficitAsync(SaleOrderDeficitPo spec, int createdBy)
    {
        ValidateLineQuantities([spec.Quantity]);

        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);

        // _inv is null only in older unit tests that build this repository without an Inventory
        // context; production DI always supplies it. Without it the line falls back to a generic
        // description rather than failing — it is editable while the PO is a draft.
        var variant = _inv is null ? null : await _inv.ProductVariants.AsNoTracking()
            .Where(v => v.Uuid == spec.VariantUuid)
            .Select(v => new { v.Sku, v.VariantName, v.IsDefault, ProductName = v.Product.Name, v.Product.UomCode })
            .FirstOrDefaultAsync();

        var description = variant is null
            ? $"Variant {spec.VariantUuid}"
            : variant.IsDefault
                ? $"{variant.ProductName} ({variant.Sku})"
                : $"{variant.ProductName} - {variant.VariantName} ({variant.Sku})";
        if (description.Length > 300) description = description[..300];

        var label = spec.Source == PurchaseOrderSources.DropShip ? "Drop ship" : "Back-to-back";

        var po = new PurchaseOrder
        {
            UUID                      = Guid.NewGuid(),
            TraceId                   = spec.TraceId,
            PoNumber                  = poNumber,
            Title                     = $"{label} for {spec.SoNumber}",
            SupplierId                = spec.SupplierId,
            SupplierName              = spec.SupplierName,
            Status                    = spec.Status,
            IsActive                  = true,
            CreatedBy                 = createdBy,
            CreatedDate               = now,
            Source                    = spec.Source,
            LinkedSoId                = spec.SaleOrderId,
            LinkedSoLineId            = spec.SaleOrderLineId,
            CustomerShippingAddressId = spec.CustomerShippingAddressId,
            // §6.2 — the values the system chose, kept as-is however the team edits the PO afterwards.
            AutoGeneratedQty          = spec.Quantity,
            AutoGeneratedPrice        = spec.UnitPrice,
            AutoSelectedSupplierId    = spec.SupplierId
        };

        po.Lines.Add(new PurchaseOrderLine
        {
            UUID            = Guid.NewGuid(),
            LineNo          = 1,
            VariantUuid     = spec.VariantUuid,
            ItemDescription = description,
            UnitOfMeasure   = variant?.UomCode,
            Quantity        = spec.Quantity,
            UnitPrice       = spec.UnitPrice,
            LineTotal       = spec.Quantity * spec.UnitPrice
        });

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();

        return new CreatedPurchaseOrder(po.UUID, po.Id, po.PoNumber, variant?.Sku);
    }

    // ── Update DRAFT PO ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PoFieldChange>> UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be edited. Current status: {po.Status}.");

        // A29-P5-11 — snapshot before anything is applied, so what changed can be told afterwards.
        // Only a PO the system raised for a sale order is audited field by field; a hand-made PO's
        // edits keep the one generic audit row the controller has always written.
        var audited      = po.Source != PurchaseOrderSources.Manual;
        var changes      = new List<PoFieldChange>();
        var oldSupplier  = po.SupplierName;
        var oldLines     = po.Lines.Select(LineSnapshot.Of).ToList();
        var oldWarehouse = po.DeliveryWarehouseName;
        var oldDelivery  = po.DeliveryDate;
        var oldTitle     = po.Title;
        var oldNotes     = po.Notes;
        var supplierChanged = (req.SupplierId.HasValue && req.SupplierId.Value != po.SupplierId)
                           || (req.SupplierName is not null && req.SupplierName != po.SupplierName);

        if (req.SupplierName is not null)          po.SupplierName          = req.SupplierName;
        if (req.SupplierId.HasValue)               po.SupplierId            = req.SupplierId.Value;
        if (req.Title is not null)                 po.Title                 = req.Title;
        if (req.DeliveryDate.HasValue)             po.DeliveryDate          = req.DeliveryDate;
        if (req.DeliveryWarehouseId.HasValue)      po.DeliveryWarehouseId   = req.DeliveryWarehouseId;
        if (req.DeliveryWarehouseName is not null) po.DeliveryWarehouseName = req.DeliveryWarehouseName;
        if (req.Notes is not null)                 po.Notes                 = req.Notes;

        if (req.Lines is not null)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            var variantPrices = await ResolveVariantPricesAsync(
                req.Lines.Where(l => l.VariantUuid.HasValue).Select(l => l.VariantUuid!.Value));

            // Cleared as well as removed: RemoveRange only marks the old lines Deleted, they stay in
            // the collection until the save, and the total below is summed from that collection — so
            // without the Clear every edit of a PO's lines left TotalAmount at old + new.
            _db.PurchaseOrderLines.RemoveRange(po.Lines);
            po.Lines.Clear();
            int lineNo = 1;
            foreach (var l in req.Lines)
            {
                var unitPrice = ResolveLineUnitPrice(l.UnitPrice, l.VariantUuid, variantPrices);
                po.Lines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = l.SourcePrLineUuid,
                    VariantUuid      = l.VariantUuid,
                    ItemDescription  = l.ItemDescription,
                    Specification    = l.Specification,
                    UnitOfMeasure    = l.UnitOfMeasure,
                    Quantity         = l.Quantity,
                    UnitPrice        = unitPrice,
                    LineDiscountPct  = l.LineDiscountPct,
                    LineTotal        = l.Quantity * unitPrice,
                    RequiredDate     = l.RequiredDate,
                    LineNotes        = l.LineNotes,
                    BudgetCode       = l.BudgetCode,
                    WarehouseId      = l.WarehouseId,
                    WarehouseName    = l.WarehouseName,
                    RequiresInspection = l.RequiresInspection
                });
            }
            po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        }

        if (audited)
        {
            if (supplierChanged)                       changes.Add(new("Supplier", oldSupplier, po.SupplierName));
            AddIfDifferent(changes, "Title", oldTitle, po.Title);
            AddIfDifferent(changes, "Delivery date", FormatDate(oldDelivery), FormatDate(po.DeliveryDate));
            AddIfDifferent(changes, "Delivery warehouse", oldWarehouse, po.DeliveryWarehouseName);
            AddIfDifferent(changes, "Notes", oldNotes, po.Notes);
            if (req.Lines is not null)
                changes.AddRange(DiffLines(oldLines, po.Lines.Select(LineSnapshot.Of)));
        }

        // A29-P5-11 — the sale order line this PO was raised for follows what the team did to it: its
        // supplier, and the margin it now has against what the PO will actually cost. One save, so
        // the PO and the line can never disagree about it.
        await SyncLinkedSaleOrderLineAsync(po, supplierChanged, recomputeMargin: req.Lines is not null);

        po.ModifiedBy   = modifiedBy;
        po.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return changes;
    }

    // ── A29-P5-11 §6.2 — split a DRAFT PO raised for a sale order line ────────

    // Moves part of one line's quantity onto a new DRAFT PO for another supplier. The new PO inherits
    // everything that ties it to the sale order (source, both links, the customer address for a drop
    // ship, and the trace_id), so the order's timeline, cancellation and the GRN auto-reservation all
    // treat it as the order's own. It is not "auto-generated": the system chose nothing for it, so
    // its audit columns stay empty and the original PO keeps the record of what the system chose.
    public async Task<SplitPoResult> SplitAsync(Guid uuid, SplitPoRequest req, int userId)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be split. Current status: {po.Status}.");
        if (po.LinkedSoLineId is null || po.LinkedSoId is null)
            throw new UnprocessableEntityException(
                "Only a purchase order raised for a sale order line can be split here.");

        var line = po.Lines.FirstOrDefault(l => l.UUID == req.LineUuid)
            ?? throw new NotFoundException("PurchaseOrderLine", req.LineUuid);

        if (req.Quantity <= 0m || req.Quantity >= line.Quantity)
            throw new BadRequestException(
                $"The quantity to move must be greater than zero and less than the line's {line.Quantity:0.####} — moving all of it is just a change of supplier.");
        if (req.SupplierId == Guid.Empty || string.IsNullOrWhiteSpace(req.SupplierName))
            throw new BadRequestException("A split needs the supplier the moved quantity goes to.");
        var price = req.UnitPrice ?? line.UnitPrice;
        if (price < 0m)
            throw new BadRequestException("The price must not be negative.");

        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);
        var oldQty   = line.Quantity;

        var newPo = new PurchaseOrder
        {
            UUID                      = Guid.NewGuid(),
            TraceId                   = po.TraceId,
            PoNumber                  = poNumber,
            Title                     = $"Split from {po.PoNumber}",
            SupplierId                = req.SupplierId,
            SupplierName              = req.SupplierName,
            Status                    = "DRAFT",
            DeliveryDate              = po.DeliveryDate,
            DeliveryWarehouseId       = po.DeliveryWarehouseId,
            DeliveryWarehouseName     = po.DeliveryWarehouseName,
            InternalNotes             = $"Split from {po.PoNumber}: {req.Quantity:0.####} of {line.ItemDescription}.",
            IsActive                  = true,
            CreatedBy                 = userId,
            CreatedDate               = now,
            Source                    = po.Source,
            LinkedSoId                = po.LinkedSoId,
            LinkedSoLineId            = po.LinkedSoLineId,
            CustomerShippingAddressId = po.CustomerShippingAddressId
        };
        newPo.Lines.Add(new PurchaseOrderLine
        {
            UUID               = Guid.NewGuid(),
            LineNo             = 1,
            VariantUuid        = line.VariantUuid,
            ItemDescription    = line.ItemDescription,
            Specification      = line.Specification,
            UnitOfMeasure      = line.UnitOfMeasure,
            Quantity           = req.Quantity,
            UnitPrice          = price,
            LineDiscountPct    = line.LineDiscountPct,
            LineTotal          = req.Quantity * price,
            RequiredDate       = line.RequiredDate,
            LineNotes          = line.LineNotes,
            BudgetCode         = line.BudgetCode,
            WarehouseId        = line.WarehouseId,
            WarehouseName      = line.WarehouseName,
            RequiresInspection = line.RequiresInspection
        });
        newPo.TotalAmount = newPo.Lines.Sum(l => l.LineTotal);

        line.Quantity  = oldQty - req.Quantity;
        line.LineTotal = line.Quantity * line.UnitPrice;
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        po.InternalNotes = AppendNote(po.InternalNotes, $"Split: {req.Quantity:0.####} of {line.ItemDescription} moved to {poNumber}.");
        po.ModifiedBy   = userId;
        po.ModifiedDate = now;

        _db.PurchaseOrders.Add(newPo);

        var soLine = await _db.SaleOrderLines.FirstOrDefaultAsync(l => l.Id == po.LinkedSoLineId);
        if (soLine is not null) await RecomputeMarginAsync(soLine, po, newPo);

        await _db.SaveChangesAsync();

        return new SplitPoResult(newPo.UUID, poNumber, po.UUID, po.PoNumber, po.TraceId,
        [
            new PoFieldChange($"Quantity — {line.ItemDescription}", Fmt(oldQty), Fmt(line.Quantity)),
            new PoFieldChange("Split", null, $"{Fmt(req.Quantity)} → {poNumber} ({req.SupplierName})")
        ]);
    }

    // ── Audit / linkage helpers (A29-P5-11) ───────────────────────────────────

    private static readonly string[] DeadStatuses = ["CANCELLED", "REJECTED"];

    private sealed record LinkedSo(Guid Uuid, string SoNumber);

    // The sale order this PO was raised for, and — when the system generated it — what it chose
    // beside what the PO now says. "The line" the system created is whatever lines buy the sale order
    // line's own variant; anything else on the PO is something the team added (§3.4).
    private async Task<(LinkedSo? SaleOrder, PoAutoGeneratedModel? AutoGenerated)> ResolveSaleOrderLinkAsync(PurchaseOrder po)
    {
        if (po.LinkedSoId is null && po.LinkedSoLineId is null && po.AutoGeneratedQty is null)
            return (null, null);

        LinkedSo? saleOrder = null;
        if (po.LinkedSoId is { } soId)
            saleOrder = await _db.SaleOrders.AsNoTracking()
                .Where(s => s.Id == soId)
                .Select(s => new LinkedSo(s.UUID, s.SoNumber))
                .FirstOrDefaultAsync();

        if (po.AutoGeneratedQty is null && po.AutoGeneratedPrice is null && po.AutoSelectedSupplierId is null)
            return (saleOrder, null);

        Guid? lineVariant = null;
        if (po.LinkedSoLineId is { } lineId)
            lineVariant = await _db.SaleOrderLines.AsNoTracking()
                .Where(l => l.Id == lineId)
                .Select(l => (Guid?)l.VariantUuid)
                .FirstOrDefaultAsync();

        var tracked = lineVariant is null
            ? po.Lines.OrderBy(l => l.LineNo).Take(1).ToList()
            : po.Lines.Where(l => l.VariantUuid == lineVariant).ToList();
        var currentQty   = tracked.Count == 0 ? (decimal?)null : tracked.Sum(l => l.Quantity);
        var currentPrice = tracked.OrderBy(l => l.LineNo).FirstOrDefault()?.UnitPrice;

        return (saleOrder, new PoAutoGeneratedModel
        {
            Qty               = po.AutoGeneratedQty,
            Price             = po.AutoGeneratedPrice,
            SupplierId        = po.AutoSelectedSupplierId,
            CurrentQty        = currentQty,
            CurrentPrice      = currentPrice,
            CurrentSupplierId = po.SupplierId,
            QtyModified       = currentQty != po.AutoGeneratedQty,
            PriceModified     = currentPrice != po.AutoGeneratedPrice,
            SupplierModified  = po.SupplierId != po.AutoSelectedSupplierId,
            AddedLineCount    = po.Lines.Count - tracked.Count
        });
    }

    private sealed record LineSnapshot(string Key, string Description, decimal Qty, decimal Price)
    {
        // A line is the same line before and after if it is the same variant; a free-text line is
        // matched on its description. Lines are wholesale-replaced on edit, so their own ids are new.
        internal static LineSnapshot Of(PurchaseOrderLine l) => new(
            l.VariantUuid?.ToString("N") ?? $"text:{l.ItemDescription}", l.ItemDescription, l.Quantity, l.UnitPrice);
    }

    private static string Fmt(decimal v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    private static string? FormatDate(DateTime? d) => d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddIfDifferent(List<PoFieldChange> changes, string field, string? oldValue, string? newValue)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            changes.Add(new PoFieldChange(field, oldValue, newValue));
    }

    private static string AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing} | {note}";

    private static IEnumerable<PoFieldChange> DiffLines(IEnumerable<LineSnapshot> before, IEnumerable<LineSnapshot> after)
    {
        var remaining = before.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => new Queue<LineSnapshot>(g));
        var result = new List<PoFieldChange>();

        foreach (var now in after)
        {
            if (remaining.TryGetValue(now.Key, out var queue) && queue.Count > 0)
            {
                var was = queue.Dequeue();
                if (was.Qty   != now.Qty)   result.Add(new($"Quantity — {now.Description}",   Fmt(was.Qty),   Fmt(now.Qty)));
                if (was.Price != now.Price) result.Add(new($"Unit price — {now.Description}", Fmt(was.Price), Fmt(now.Price)));
            }
            else
            {
                result.Add(new("Line added", null, $"{now.Description} × {Fmt(now.Qty)} @ {Fmt(now.Price)}"));
            }
        }

        foreach (var was in remaining.Values.SelectMany(q => q))
            result.Add(new("Line removed", $"{was.Description} × {Fmt(was.Qty)} @ {Fmt(was.Price)}", null));

        return result;
    }

    // Only the PO that IS the line's own (its back-pointer) speaks for the line's supplier — a split
    // PO buying part of it elsewhere must not overwrite who the line is mainly bought from.
    private async Task SyncLinkedSaleOrderLineAsync(PurchaseOrder po, bool supplierChanged, bool recomputeMargin)
    {
        if (po.LinkedSoLineId is not { } soLineId) return;
        var soLine = await _db.SaleOrderLines.FirstOrDefaultAsync(l => l.Id == soLineId);
        if (soLine is null) return;

        if (supplierChanged && soLine.LinkedPoId == po.Id)
            soLine.SelectedSupplierId = po.SupplierId;
        if (recomputeMargin)
            await RecomputeMarginAsync(soLine, po);
    }

    // §14.2 margin against what the line will actually cost: the quantity-weighted average price of
    // every live PO line bought for it. One PO makes that its price; a split across suppliers makes it
    // the blend. The POs passed in are the ones with unsaved changes, so they are read from memory and
    // excluded from the query. Cleared (not zero) when nothing is being bought for the line any more.
    private async Task RecomputeMarginAsync(SaleOrderLine soLine, params PurchaseOrder[] inMemory)
    {
        var inMemoryIds = inMemory.Where(p => p.Id != 0).Select(p => p.Id).ToList();

        var others = await _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.PurchaseOrder.LinkedSoLineId == soLine.Id
                     && !inMemoryIds.Contains(l.PurchaseOrderId)
                     && !l.PurchaseOrder.IsDelete
                     && !DeadStatuses.Contains(l.PurchaseOrder.Status)
                     && l.VariantUuid == soLine.VariantUuid)
            .Select(l => new { l.Quantity, l.UnitPrice })
            .ToListAsync();

        var mine = inMemory
            .Where(p => !DeadStatuses.Contains(p.Status))
            .SelectMany(p => p.Lines)
            .Where(l => l.VariantUuid == soLine.VariantUuid)
            .Select(l => new { l.Quantity, l.UnitPrice });

        var all = others.Concat(mine).ToList();
        var qty = all.Sum(x => x.Quantity);
        if (qty <= 0m)
        {
            soLine.Margin = null;
            soLine.MarginPercent = null;
            return;
        }

        var averageCost = all.Sum(x => x.Quantity * x.UnitPrice) / qty;
        (soLine.Margin, soLine.MarginPercent) = Services.SaleOrderMargin.Compute(soLine.UnitPrice, averageCost);
    }

    // ── Send PO to supplier (APPROVED → SENT) ─────────────────────────────────

    public async Task SendAsync(Guid uuid, string? contactMobile, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "APPROVED")
            throw new UnprocessableEntityException(
                $"Only APPROVED purchase orders can be sent. Current status: {po.Status}.");

        po.Status                = "SENT";
        po.SupplierContactMobile = contactMobile;
        po.ModifiedBy            = modifiedBy;
        po.ModifiedDate          = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // A29-P4-04 §4.5 — cancelling a DRAFT PO a sale order line created a deficit against.
    public async Task CancelAsync(Guid uuid, string? reason, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be cancelled this way. Current status: {po.Status}.");

        po.Status       = "CANCELLED";
        po.InternalNotes = string.IsNullOrWhiteSpace(reason)
            ? po.InternalNotes
            : $"{po.InternalNotes}{(string.IsNullOrWhiteSpace(po.InternalNotes) ? "" : " | ")}Cancelled: {reason}";
        po.ModifiedBy    = modifiedBy;
        po.ModifiedDate  = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ── Query methods ─────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PoListItemModel>> GetListAsync(PoListFilter filter)
    {
        var query = _db.PurchaseOrders.Where(p => !p.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(p => p.Status == filter.Status);
        if (filter.SupplierId.HasValue)
            query = query.Where(p => p.SupplierId == filter.SupplierId.Value);
        if (filter.DateFrom.HasValue)
            query = query.Where(p => p.CreatedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(p => p.CreatedDate <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(p => p.PoNumber.ToLower().Contains(term)
                                  || (p.Title != null && p.Title.ToLower().Contains(term))
                                  || p.SupplierName.ToLower().Contains(term));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(p => p.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PoListItemModel
            {
                UUID         = p.UUID,
                TraceId      = p.TraceId,
                PoNumber     = p.PoNumber,
                Title        = p.Title,
                SupplierId   = p.SupplierId,
                SupplierName = p.SupplierName,
                Status       = p.Status,
                TotalAmount  = p.TotalAmount,
                DeliveryDate = p.DeliveryDate,
                CreatedDate  = p.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<PoListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PoDetailModel?> GetByIdAsync(Guid uuid)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines.OrderBy(l => l.LineNo))
            .Include(p => p.PrLinks)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (po is null) return null;

        var variantInfo = await ResolveVariantDisplayInfoAsync(
            po.Lines.Where(l => l.VariantUuid.HasValue).Select(l => l.VariantUuid!.Value));

        // PurchaseOrderPrLink stores only the PR's UUID (no FK), so its number/title has to be
        // batch-resolved separately rather than left for the caller to fake from array position.
        var prUuids = po.PrLinks.Select(l => l.PrUuid).ToList();
        var prInfo = prUuids.Count == 0
            ? new Dictionary<Guid, (string PrNumber, string PrTitle)>()
            : await _db.PurchaseRequisitions
                .Where(pr => prUuids.Contains(pr.UUID))
                .Select(pr => new { pr.UUID, pr.PrNumber, pr.PrTitle })
                .ToDictionaryAsync(x => x.UUID, x => (x.PrNumber, x.PrTitle));

        var (linkedSo, autoGenerated) = await ResolveSaleOrderLinkAsync(po);

        return new PoDetailModel
        {
            Source                    = po.Source,
            LinkedSaleOrderUuid       = linkedSo?.Uuid,
            LinkedSaleOrderNumber     = linkedSo?.SoNumber,
            CustomerShippingAddressId = po.CustomerShippingAddressId,
            AutoGenerated             = autoGenerated,
            UUID                  = po.UUID,
            TraceId               = po.TraceId,
            PoNumber              = po.PoNumber,
            Title                 = po.Title,
            SupplierId            = po.SupplierId,
            SupplierName          = po.SupplierName,
            SupplierContactMobile = po.SupplierContactMobile,
            Status                = po.Status,
            TotalAmount           = po.TotalAmount,
            DeliveryDate          = po.DeliveryDate,
            DeliveryWarehouseId   = po.DeliveryWarehouseId,
            DeliveryWarehouseName = po.DeliveryWarehouseName,
            Notes                 = po.Notes,
            InternalNotes         = po.InternalNotes,
            CreatedBy             = po.CreatedBy,
            CreatedDate           = po.CreatedDate,
            LinkedPrUuids         = po.PrLinks.Select(l =>
            {
                var info = prInfo.TryGetValue(l.PrUuid, out var found) ? found : (PrNumber: string.Empty, PrTitle: string.Empty);
                return new LinkedPrModel { Uuid = l.PrUuid, PrNumber = info.PrNumber, PrTitle = info.PrTitle };
            }).ToList(),
            Lines = po.Lines.Select(l =>
            {
                var vi = l.VariantUuid.HasValue && variantInfo.TryGetValue(l.VariantUuid.Value, out var info) ? info : null;
                return new PoLineModel
                {
                    UUID                  = l.UUID,
                    LineNo                = l.LineNo,
                    SourcePrLineUuid      = l.SourcePrLineUuid,
                    VariantUuid           = l.VariantUuid,
                    ProductUuid           = vi?.ProductUuid,
                    VariantSku            = vi?.Sku,
                    VariantName           = vi?.VariantName,
                    ProductName           = vi?.ProductName,
                    ProductImageUrl       = vi?.ImageUrl,
                    ItemDescription       = l.ItemDescription,
                    Specification         = l.Specification,
                    UnitOfMeasure         = l.UnitOfMeasure,
                    Quantity              = l.Quantity,
                    UnitPrice             = l.UnitPrice,
                    LineDiscountPct       = l.LineDiscountPct,
                    LineTotal             = l.LineTotal,
                    QtyReceived           = l.QtyReceived,
                    QtyInvoiced           = l.QtyInvoiced,
                    RequiredDate          = l.RequiredDate,
                    LineNotes             = l.LineNotes,
                    BudgetCode            = l.BudgetCode,
                    WarehouseId           = l.WarehouseId,
                    WarehouseName         = l.WarehouseName,
                    EffectiveWarehouseId  = l.EffectiveWarehouseId,
                    EffectiveWarehouseName = l.EffectiveWarehouseName,
                    RequiresInspection    = l.RequiresInspection
                };
            }).ToList()
        };
    }

    public async Task<List<PoSearchItemModel>> SearchForGrnAsync(string? q, bool receivableOnly)
    {
        var query = _db.PurchaseOrders.Where(p => !p.IsDelete).AsQueryable();

        // A29-P5-05 §4.3 scenario 4 — a drop-ship PO's vendor ships straight to the customer, so it
        // never goes into a warehouse and is never something to receive against.
        if (receivableOnly)
            query = query.Where(p => (p.Status == "SENT" || p.Status == "PARTIALLY_RECEIVED")
                                  && p.Source != PurchaseOrderSources.DropShip);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(p => p.PoNumber.ToLower().Contains(term)
                                  || p.SupplierName.ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(p => p.ModifiedDate ?? p.CreatedDate)
            .Take(20)
            .Select(p => new PoSearchItemModel
            {
                UUID         = p.UUID,
                PoNumber     = p.PoNumber,
                SupplierName = p.SupplierName,
                Status       = p.Status,
                GrandTotal   = p.TotalAmount,
                Currency     = null,
                QtyPending   = p.Lines.Where(l => l.Quantity > l.QtyReceived)
                                       .Sum(l => l.Quantity - l.QtyReceived)
            })
            .ToListAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Frontend p-inputNumber widgets already soft-clamp to this range, but that's only a UI
    // hint — nothing stopped a pasted value, a race before blur, or a direct API call from
    // submitting an out-of-range quantity. This is the actual enforcement.
    private const decimal MaxLineQuantity = 999999m;

    private static void ValidateLineQuantities(IEnumerable<decimal> quantities)
    {
        foreach (var qty in quantities)
        {
            if (qty <= 0)
                throw new BadRequestException("Line quantity must be greater than zero.");
            if (qty > MaxLineQuantity)
                throw new BadRequestException($"Line quantity must not exceed {MaxLineQuantity:N0}.");
        }
    }

    // PV-004 — a PR line only ever references a Product; converting it into a PO line requires a
    // specific ProductVariant. Every product has exactly one is_default=true variant (auto-created
    // by PV-001, even for single-SKU products like Cement), so conversion always resolves cleanly.
    private async Task<Dictionary<Guid, Guid>> ResolveDefaultVariantUuidsAsync(IEnumerable<Guid> productUuids)
    {
        var ids = productUuids.Distinct().ToList();
        if (_inv is null || ids.Count == 0) return new Dictionary<Guid, Guid>();

        return await _inv.ProductVariants
            .Where(v => v.IsDefault && ids.Contains(v.Product.Uuid))
            .Select(v => new { ProductUuid = v.Product.Uuid, v.Uuid })
            .ToDictionaryAsync(x => x.ProductUuid, x => x.Uuid);
    }

    private async Task<Dictionary<Guid, decimal>> ResolveVariantPricesAsync(IEnumerable<Guid> variantUuids)
    {
        var ids = variantUuids.Distinct().ToList();
        if (_inv is null || ids.Count == 0) return new Dictionary<Guid, decimal>();

        return await _inv.ProductVariants
            .Where(v => ids.Contains(v.Uuid))
            .Select(v => new { v.Uuid, v.PurchasePrice })
            .ToDictionaryAsync(x => x.Uuid, x => x.PurchasePrice);
    }

    // Unit_price defaults from ProductVariant.purchase_price when the caller doesn't supply one —
    // an explicit 0 or positive price from the caller always wins over the catalogue default.
    private static decimal ResolveLineUnitPrice(decimal requestedUnitPrice, Guid? variantUuid, Dictionary<Guid, decimal> variantPrices)
    {
        if (requestedUnitPrice > 0) return requestedUnitPrice;
        if (variantUuid.HasValue && variantPrices.TryGetValue(variantUuid.Value, out var price)) return price;
        return requestedUnitPrice;
    }

    private sealed record VariantDisplayInfo(string Sku, string VariantName, string ProductName, Guid ProductUuid, string? ImageUrl);

    private async Task<Dictionary<Guid, VariantDisplayInfo>> ResolveVariantDisplayInfoAsync(IEnumerable<Guid> variantUuids)
    {
        var ids = variantUuids.Distinct().ToList();
        if (_inv is null || ids.Count == 0) return new Dictionary<Guid, VariantDisplayInfo>();

        var rows = await _inv.ProductVariants
            .Where(v => ids.Contains(v.Uuid))
            .Select(v => new { v.Uuid, v.Sku, v.VariantName, ProductName = v.Product.Name, ProductUuid = v.Product.Uuid, ImageUrl = v.Product.ImageUrl })
            .ToListAsync();

        return rows.ToDictionary(r => r.Uuid, r => new VariantDisplayInfo(r.Sku, r.VariantName, r.ProductName, r.ProductUuid, r.ImageUrl));
    }

    private static void ValidateQuotationRequirements(IEnumerable<PrLine> lines)
    {
        var blockers = lines
            .Where(l => l.RequiresQuotation && l.QuotationStatus != "AWARDED")
            .Select(l => l.ItemDescription)
            .ToList();
        if (blockers.Count > 0)
            throw new BadRequestException(
                $"The following lines require an awarded quotation before conversion: {string.Join(", ", blockers)}");
    }

    private async Task<string> GeneratePoNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.PurchaseOrders
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return $"PO-{year}-{(count + 1):D5}";
    }

    private async Task<string[]> GeneratePoNumbersAsync(int year, int n)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var existing  = await _db.PurchaseOrders
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return Enumerable.Range(1, n)
            .Select(i => $"PO-{year}-{(existing + i):D5}")
            .ToArray();
    }
}
