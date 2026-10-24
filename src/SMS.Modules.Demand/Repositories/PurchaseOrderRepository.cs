using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal sealed class PurchaseOrderRepository : IPurchaseOrderRepository
{
    private readonly DemandDbContext _db;
    private readonly ILogger<PurchaseOrderRepository> _logger;
    private readonly IDocumentNumberGenerator _numbers;
    // Optional: null in older unit tests that construct this repository directly without an
    // Inventory context — those tests don't exercise variant resolution. Production DI always
    // supplies the real InventoryDbContext since it's registered by the Inventory module.
    private readonly InventoryDbContext? _inv;

    // A35 — optional like the Inventory context: Finance's rate lock (AUTO_SEND POs are created APPROVED), Suppliers' default
    // purchase currency, Tenancy's purchase base, Finance's org currencies and Lookups' codes. Without them a PO takes the
    // currency it is given (or none) and is not locked at creation.
    private readonly ICurrencyService? _currency;
    private readonly IPartnerCurrencyDefaults? _partnerCurrencies;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly IOrgCurrencyLookup? _orgCurrencies;
    private readonly ICurrencyCodeLookup? _currencyCodes;
    // A46 C4/C5 — every line is created by the factory and every change to a line goes through the pricing middleware (BR-26/29);
    // both are built from this repository's own services when not supplied (older tests construct it directly).
    private readonly IPurchaseOrderLineFactory _lines;
    private readonly IPurchaseOrderLinePricingMiddleware _pricing;

    public PurchaseOrderRepository(
        DemandDbContext db, ILogger<PurchaseOrderRepository> logger, IDocumentNumberGenerator numbers, InventoryDbContext? inv = null,
        ICurrencyService? currency = null, IPartnerCurrencyDefaults? partnerCurrencies = null,
        IOrganizationCurrencyService? orgCurrency = null, IOrgCurrencyLookup? orgCurrencies = null,
        ICurrencyCodeLookup? currencyCodes = null, IPurchasePriceResolver? priceResolver = null,
        ICurrencyConverter? currencyConverter = null, IPurchaseOrderLinePricingMiddleware? linePricing = null,
        IPurchaseOrderLineFactory? lineFactory = null)
    {
        _pricing           = linePricing ?? new PurchaseOrderLinePricingMiddleware(
            db, priceResolver ?? new PurchasePriceResolver(db, inv, orgCurrency, currencyCodes, null, currencyConverter), inv);
        _lines             = lineFactory ?? new PurchaseOrderLineFactory(_pricing);
        _currency          = currency;
        _partnerCurrencies = partnerCurrencies;
        _orgCurrency       = orgCurrency;
        _orgCurrencies     = orgCurrencies;
        _currencyCodes     = currencyCodes;
        _db      = db;
        _logger  = logger;
        _numbers = numbers;
        _inv     = inv;
    }

    private Guid Org => _db.TenantContext.OrganizationId;

    /// <summary>
    /// A35 D-14 / BR-C4-03 — a PO's currency: the one asked for (a new choice must be an active org currency), else the PO's
    /// own (an edit), else the supplier's default purchase currency, else the organization's purchase base. Null only when
    /// none of those services is there (module test hosts) — read as the purchase base.
    /// </summary>
    private async Task<Guid?> ResolveCurrencyAsync(Guid? requested, Guid supplierId, Guid? current = null)
    {
        if (requested is { } id && id != Guid.Empty)
        {
            if (id != current)
                await DemandCurrency.RequireActiveOrgCurrencyAsync(_orgCurrencies, _orgCurrency, Org, id, TransactionDomain.Purchase,
                    _currencyCodes is null ? null : await _currencyCodes.GetCodeAsync(id));
            return id;
        }
        if (current is { } existing && existing != Guid.Empty) return existing;
        return await DemandCurrency.DefaultForPartnerAsync(_partnerCurrencies, _orgCurrency, Org, supplierId, TransactionDomain.Purchase);
    }

    // ── Single-vendor conversion: all PR lines → one PO ──────────────────────

    public async Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy) =>
        (await CreateFromPrWithSummaryAsync(prUuid, req, createdBy)).PoUuids[0];

    /// <summary>A46 C5 — as <see cref="CreateFromPrAsync"/>, plus how many of the lines were priced from the pricelist (BR-30).</summary>
    public async Task<PoConversionResult> CreateFromPrWithSummaryAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy)
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
        var poNumber = await GeneratePoNumberAsync(now);

        var defaultVariants = await ResolveDefaultVariantUuidsAsync(
            pr.Lines.Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value));

        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            TraceId      = pr.TraceId,
            PoNumber     = poNumber,
            Title        = pr.PrTitle,
            SupplierId   = req.SupplierId,
            SupplierName = req.SupplierName,
            Status       = "DRAFT",
            CurrencyId   = await ResolveCurrencyAsync(null, req.SupplierId),
            DeliveryDate = req.DeliveryDate,
            OrderDate    = now.Date,
            Notes               = req.Notes,
            DeliveryWarehouseId = pr.WarehouseUuid,
            IsActive            = true,
            CreatedBy    = createdBy,
            CreatedDate  = now,
            PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
        };

        var results = new List<PurchaseOrderLineResult>();
        int lineNo  = 1;
        foreach (var prLine in pr.Lines.OrderBy(l => l.LineNo))
        {
            results.Add(await AddRequisitionLineAsync(po, prLine, lineNo++, defaultVariants));
            prLine.LineStatus = "FULLY_CONVERTED";
        }
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);

        _db.PurchaseOrders.Add(po);

        pr.Status       = "FULLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return new PoConversionResult([po.UUID], PurchaseOrderLineFactory.Summarize(results));
    }

    // ── Split-by-vendor conversion: each vendor gets their own PO ─────────────

    public async Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy) =>
        (await CreateFromPrSplitWithSummaryAsync(prUuid, req, createdBy)).PoUuids.ToList();

    public async Task<PoConversionResult> CreateFromPrSplitWithSummaryAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy)
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
        var poNumbers  = await GeneratePoNumbersAsync(now, grouped.Count);
        var poUuids    = new List<Guid>();
        var results    = new List<PurchaseOrderLineResult>();
        int poIndex    = 0;

        var defaultVariants = await ResolveDefaultVariantUuidsAsync(
            assignments.Where(a => a.PrLine.ProductId.HasValue).Select(a => a.PrLine.ProductId!.Value));

        foreach (var group in grouped)
        {
            var firstAssignment = group.First().Assignment;

            var po = new PurchaseOrder
            {
                UUID         = Guid.NewGuid(),
                TraceId      = pr.TraceId,
                PoNumber     = poNumbers[poIndex++],
                Title        = pr.PrTitle,
                SupplierId   = group.Key,
                SupplierName = firstAssignment.SupplierName,
                Status       = "DRAFT",
                CurrencyId   = await ResolveCurrencyAsync(null, group.Key),
                DeliveryDate = req.DeliveryDate,
                OrderDate    = now.Date,
                Notes        = req.Notes,
                IsActive     = true,
                CreatedBy    = createdBy,
                CreatedDate  = now,
                PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
            };

            int lineNo = 1;
            foreach (var (_, prLine) in group)
            {
                results.Add(await AddRequisitionLineAsync(po, prLine, lineNo++, defaultVariants));
                prLine.LineStatus = "FULLY_CONVERTED";
            }
            po.TotalAmount = po.Lines.Sum(l => l.LineTotal);

            _db.PurchaseOrders.Add(po);
            poUuids.Add(po.UUID);
        }

        pr.Status = pr.Lines.All(l => l.LineStatus == "FULLY_CONVERTED")
            ? "FULLY_CONVERTED"
            : "PARTIALLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return new PoConversionResult(poUuids, PurchaseOrderLineFactory.Summarize(results));
    }

    /// <summary>
    /// A46 C5 - a requisition line becomes a PO line through the factory: an awarded RFQ's price is taken as-is (source RFQ, no
    /// resolver call); every other line is priced from the supplier's pricelist, falling back to the requisition's estimate
    /// (Manual) when none applies - never silently to 0, which would throw away the estimate the requester gave.
    /// </summary>
    private async Task<PurchaseOrderLineResult> AddRequisitionLineAsync(
        PurchaseOrder po, PrLine prLine, int lineNo, Dictionary<Guid, Guid> defaultVariants)
    {
        var awarded = string.Equals(prLine.QuotationStatus, "AWARDED", StringComparison.Ordinal);
        var result = await _lines.CreateLineAsync(po, new CreateLineInput
        {
            Origin              = awarded ? LineOrigin.Rfq : LineOrigin.PurchaseRequest,
            LineNo              = lineNo,
            SourcePrLineUuid    = prLine.UUID,
            VariantUuid         = prLine.ProductId.HasValue && defaultVariants.TryGetValue(prLine.ProductId.Value, out var vUuid) ? vUuid : null,
            ItemDescription     = prLine.ItemDescription,
            Specification       = prLine.Specification,
            UnitOfMeasure       = prLine.UnitOfMeasure,
            Quantity            = prLine.Quantity,
            RequiredDate        = prLine.RequiredDate,
            BudgetCode          = prLine.BudgetCode,
            SuppliedPrice       = awarded ? prLine.EstimatedUnitPrice : null,
            SuppliedPriceSource = awarded ? PriceSource.RFQ : null,
            FallbackPrice       = prLine.EstimatedUnitPrice
        });
        po.Lines.Add(result.Line);
        return result;
    }

    // ── Manual PO creation (with optional multi-PR consolidation) ─────────────

    public async Task<Guid> CreateAsync(CreatePoRequest req, int createdBy)
    {
        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now);

        var po = new PurchaseOrder
        {
            UUID                  = req.PoUuid is { } gid && gid != Guid.Empty ? gid : Guid.NewGuid(),
            TraceId               = Guid.NewGuid(),
            PoNumber              = poNumber,
            Title                 = req.Title,
            SupplierId            = req.SupplierId,
            SupplierName          = req.SupplierName,
            Status                = "DRAFT",
            CurrencyId            = await ResolveCurrencyAsync(req.CurrencyId, req.SupplierId),
            Source                = string.IsNullOrWhiteSpace(req.Source) ? PurchaseOrderSources.Manual : req.Source.Trim().ToUpperInvariant(),
            DeliveryDate          = req.DeliveryDate,
            // A46 C1 / BR-20 - today (UTC) unless the caller dated the order.
            OrderDate             = ResolveOrderDate(req.OrderDate, now),
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
                    await AddRequisitionLineAsync(po, prLine, lineNo++, defaultVariants);
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
                // A45 D-10 / A46 C4 - what the client says about the price's source is verified, a line with no price is priced by
                // the server; both inside the factory.
                var result = await _lines.CreateLineAsync(po, RequestLine(po, line, lineNo++, variantPrices));
                po.Lines.Add(result.Line);
            }
        }

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();
        return po.UUID;
    }

    /// <summary>
    /// A client-sent line as factory input. A PO the system raised (production shortage, back-to-back) has no buyer typing prices:
    /// a price it sends with no source claim is only the fallback, and the pricelist is tried first.
    /// </summary>
    private static CreateLineInput RequestLine(
        PurchaseOrder po, CreatePoLineRequest line, int lineNo, Dictionary<Guid, decimal> variantPrices, PreviousLineState? previous = null)
    {
        // The variant's catalogue purchase price stands in for a line sent with no price (it was always so).
        var fallback = line.UnitPrice > 0m ? line.UnitPrice
            : line.VariantUuid is { } v && variantPrices.TryGetValue(v, out var catalogue) ? catalogue : (decimal?)null;
        return CreateLineInput.From(line, lineNo, fallback, previous, systemRaised: po.Source != PurchaseOrderSources.Manual);
    }

    /// <summary>A46 C1 - the order date a request names (date part), or today when it names none.</summary>
    private static DateTime ResolveOrderDate(DateTime? requested, DateTime utcNow)
    {
        if (requested is not { } d || d == default) return utcNow.Date;
        if (d.Year < 1990 || d.Year > 2200) throw new BadRequestException("The order date is not a valid date.");
        return d.Date;
    }

    // ── A31-C3/BR-C3-07 — consolidate onto an existing PRODUCTION-sourced draft ──

    /// <summary>
    /// One line, from the caller (the one shortage being acted on). Appends it to whatever open
    /// PRODUCTION-sourced Draft PO already exists for this supplier — bumping a matching variant's
    /// own line in place (same UUID, so an earlier shortage's own SupplySourceLineUuid link stays
    /// valid) or adding a new one — rather than raising a second PO for the same supplier every time
    /// a new shortage happens to need one. Falls back to <see cref="CreateAsync"/> when nothing open
    /// exists yet. Deliberately never touches <see cref="UpdateAsync"/>'s own Lines-replace path:
    /// that regenerates every line's UUID on every save, which would silently orphan any other
    /// shortage's own link to a line still on the same PO.
    /// A46 C4: the bump goes through the pricing middleware (a bigger quantity can reach a better tier of the line's pricelist
    /// price), a new line through the factory.
    /// </summary>
    public async Task<PoConsolidationResult> AddOrIncreaseProductionLineAsync(CreatePoRequest req, int createdBy)
    {
        var newLine = req.Lines?.SingleOrDefault()
            ?? throw new BadRequestException("Exactly one line is expected when consolidating onto a production draft.");
        ValidateLineQuantities([newLine.Quantity]);

        var existing = await _db.PurchaseOrders.Include(p => p.Lines)
            .Where(p => p.SupplierId == req.SupplierId && p.Source == PurchaseOrderSources.Production &&
                        p.Status == "DRAFT" && !p.IsDelete)
            .OrderByDescending(p => p.CreatedDate)
            .FirstOrDefaultAsync();

        if (existing is null)
        {
            var createReq = new CreatePoRequest
            {
                SupplierId = req.SupplierId, SupplierName = req.SupplierName, Lines = [newLine],
                DeliveryDate = req.DeliveryDate, DeliveryWarehouseId = req.DeliveryWarehouseId,
                DeliveryWarehouseName = req.DeliveryWarehouseName, Title = req.Title, Notes = req.Notes,
                OrderDate = req.OrderDate, Source = PurchaseOrderSources.Production
            };
            var poUuid = await CreateAsync(createReq, createdBy);
            var created = await _db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.UUID == poUuid);
            var createdLine = created.Lines.Single();
            return new PoConsolidationResult(created.UUID, created.PoNumber, createdLine.UUID, createdLine.Quantity, IsNewPo: true);
        }

        var matching = existing.Lines.FirstOrDefault(l => l.VariantUuid == newLine.VariantUuid);
        if (matching is not null)
        {
            var before = matching.Quantity;
            matching.Quantity += newLine.Quantity;
            await _pricing.ApplyPricingAsync(matching, existing, new LineChangeContext
            {
                QuantityChanged = true, PreviousQuantity = before, PreviousDeliveryDate = matching.RequiredDate
            });
            matching.LineTotal = PurchaseOrderLinePricingMiddleware.LineTotal(matching.Quantity, matching.UnitPrice);
        }
        else
        {
            var variantPrices = await ResolveVariantPricesAsync(newLine.VariantUuid is { } vu ? [vu] : []);
            var result = await _lines.CreateLineAsync(existing, RequestLine(existing, newLine, existing.Lines.Count + 1, variantPrices));
            matching = result.Line;
            existing.Lines.Add(matching);
        }
        existing.TotalAmount   = existing.Lines.Sum(l => l.LineTotal);
        existing.ModifiedBy    = createdBy;
        existing.ModifiedDate  = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new PoConsolidationResult(existing.UUID, existing.PoNumber, matching.UUID, matching.Quantity, IsNewPo: false);
    }



    // A31-C3 §5.4 tier 2 — Draft doesn't count as "used" (it may never be sent), Cancelled/Rejected
    // don't either (the order never actually happened).
    public async Task<Guid?> GetLastSupplierForVariantAsync(Guid variantUuid)
    {
        var excludedStatuses = new[] { "DRAFT", "CANCELLED", "REJECTED" };
        return await _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.VariantUuid == variantUuid && !l.PurchaseOrder.IsDelete && !excludedStatuses.Contains(l.PurchaseOrder.Status))
            .OrderByDescending(l => l.PurchaseOrder.CreatedDate)
            .Select(l => (Guid?)l.PurchaseOrder.SupplierId)
            .FirstOrDefaultAsync();
    }

    // ── A29-P5-03 §6.1/§6.3 — PO generated from a sale order line's deficit ──

    // Always exactly one line, for that variant — §6.2's later edits (extra items, splits) happen on
    // the PO itself afterwards. Numbered by the same generator as every other PO: a second scheme
    // would collide with it on the (OrganizationId, PoNumber) unique index.
    public async Task<CreatedPurchaseOrder> CreateFromSaleOrderDeficitAsync(SaleOrderDeficitPo spec, int createdBy)
    {
        ValidateLineQuantities([spec.Quantity]);

        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now);

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
            CurrencyId                = await ResolveCurrencyAsync(null, spec.SupplierId),
            OrderDate                 = now.Date,
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

        // A46 C5 (BR-27) - the supplier's pricelist price wins when there is one; the price the caller selected is the fallback.
        var lineResult = await _lines.CreateLineAsync(po, new CreateLineInput
        {
            Origin          = LineOrigin.AutoGenerated,
            LineNo          = 1,
            VariantUuid     = spec.VariantUuid,
            ItemDescription = description,
            UnitOfMeasure   = variant?.UomCode,
            Quantity        = spec.Quantity,
            FallbackPrice   = spec.UnitPrice
        });
        po.Lines.Add(lineResult.Line);
        // What the system chose is what it priced the line at (the margin of the sale order line is measured against it).
        po.AutoGeneratedPrice = lineResult.Line.UnitPrice;

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);

        // A35 D-12 — an AUTO_SEND PO is APPROVED from birth, so its rate is locked here. With no rate on file (D-5) it is
        // not refused outright — this runs in a retried job and the deficit still needs a PO — but created as a DRAFT for a
        // person to approve once a rate is entered (approval then locks it).
        if (spec.Status == "APPROVED" && po.CurrencyId is { } currencyId)
        {
            try
            {
                var now2 = DateTime.UtcNow;
                if (await DemandCurrency.LockAsync(_currency, _orgCurrency, Org, currencyId, DemandCurrency.Today(now2), TransactionDomain.Purchase) is { } rate)
                    DemandCurrency.Apply(po, rate, now2);
            }
            catch (CurrencyRateNotFoundException ex)
            {
                _logger.LogWarning("Auto-created PO {PoNumber} was left as a DRAFT: {Reason}", po.PoNumber, ex.Message);
                po.Status = "DRAFT";
            }
        }

        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();

        return new CreatedPurchaseOrder(po.UUID, po.Id, po.PoNumber, variant?.Sku, po.Status, lineResult.Line.UnitPrice);
    }

    // ── Update DRAFT PO ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PoFieldChange>> UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy) =>
        (await UpdateWithPricingAsync(uuid, req, modifiedBy)).Changes;

    // A46-0152 - the same update, also reporting what pricing did to each rebuilt line.
    public async Task<PoUpdateResult> UpdateWithPricingAsync(Guid uuid, PatchPoRequest req, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
        {
            // A46 C1 / BR-20 - the order date is frozen once the PO leaves DRAFT; asking for another one is a 400 of its own.
            if (req.OrderDate is { } requestedDate && requestedDate != default && requestedDate.Date != po.OrderDate.Date)
                throw new BadRequestException("The order date can only be changed while the purchase order is a draft.");
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be edited. Current status: {po.Status}.");
        }

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
        var oldOrderDate = po.OrderDate;
        var supplierChanged = (req.SupplierId.HasValue && req.SupplierId.Value != po.SupplierId)
                           || (req.SupplierName is not null && req.SupplierName != po.SupplierName);

        if (req.SupplierName is not null)          po.SupplierName          = req.SupplierName;
        if (req.SupplierId.HasValue)               po.SupplierId            = req.SupplierId.Value;
        if (req.Title is not null)                 po.Title                 = req.Title;
        if (req.DeliveryDate.HasValue)             po.DeliveryDate          = req.DeliveryDate;
        if (req.OrderDate is { } orderDate && orderDate != default) po.OrderDate = ResolveOrderDate(orderDate, DateTime.UtcNow);
        if (req.DeliveryWarehouseId.HasValue)      po.DeliveryWarehouseId   = req.DeliveryWarehouseId;
        if (req.DeliveryWarehouseName is not null) po.DeliveryWarehouseName = req.DeliveryWarehouseName;
        if (req.Notes is not null)                 po.Notes                 = req.Notes;
        // A35 — DRAFT only (checked above), so the rate is never locked yet; omitted keeps the PO's currency.
        if (req.CurrencyId is { } newCurrency && newCurrency != Guid.Empty && newCurrency != po.CurrencyId)
        {
            if (po.ExchangeRate is not null)
                throw new BadRequestException(DemandCurrency.LockedMessage);
            po.CurrencyId = await ResolveCurrencyAsync(newCurrency, po.SupplierId, po.CurrencyId);
        }

        var pricedLines = new List<(PurchaseOrderLine Line, PurchaseOrderLineResult? Result, PreviousPriceState? Previous)>();
        if (req.Lines is not null)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            var variantPrices = await ResolveVariantPricesAsync(
                req.Lines.Where(l => l.VariantUuid.HasValue).Select(l => l.VariantUuid!.Value));

            // A46 C4 - every line is rebuilt below, so which stored line each sent line stands for is worked out first: that is how
            // the server knows a quantity, product or date changed and re-prices accordingly.
            var stored = po.Lines.OrderBy(l => l.LineNo).ToList();
            var previous = new List<PreviousLineState?>();
            var previousPrices = new List<PreviousPriceState?>();
            foreach (var l in req.Lines)
            {
                var match = (l.Uuid is { } lineUuid ? stored.FirstOrDefault(s => s.UUID == lineUuid) : null)
                    ?? (l.VariantUuid is { } variant
                        ? stored.FirstOrDefault(s => s.VariantUuid == variant)
                        : stored.FirstOrDefault(s => s.VariantUuid == null && s.ItemDescription == l.ItemDescription));
                if (match is not null) stored.Remove(match);
                previous.Add(match is null ? null : new PreviousLineState(match.VariantUuid, match.Quantity, match.RequiredDate));
                previousPrices.Add(match is null ? null : new PreviousPriceState(match.UnitPrice, match.SystemResolvedPrice, match.PriceSource));
            }

            // Cleared as well as removed: RemoveRange only marks the old lines Deleted, they stay in
            // the collection until the save, and the total below is summed from that collection — so
            // without the Clear every edit of a PO's lines left TotalAmount at old + new.
            _db.PurchaseOrderLines.RemoveRange(po.Lines);
            po.Lines.Clear();
            int lineNo = 1;
            for (var i = 0; i < req.Lines.Count; i++)
            {
                // A45 D-10 - the claimed price source is verified, not stored as sent; A46 C4 - the change rules run on it.
                var result = await _lines.CreateLineAsync(po, RequestLine(po, req.Lines[i], lineNo++, variantPrices, previous[i]));
                po.Lines.Add(result.Line);
                pricedLines.Add((result.Line, result, previousPrices[i]));
            }
            po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        }

        if (audited)
        {
            if (supplierChanged)                       changes.Add(new("Supplier", oldSupplier, po.SupplierName));
            AddIfDifferent(changes, "Title", oldTitle, po.Title);
            AddIfDifferent(changes, "Delivery date", FormatDate(oldDelivery), FormatDate(po.DeliveryDate));
            AddIfDifferent(changes, "Order date", FormatDate(oldOrderDate), FormatDate(po.OrderDate));
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

        // A lines-less edit re-prices nothing: report the stored lines as they stand.
        var pricing = PoPricingResultBuilder.Build(req.Lines is not null
            ? pricedLines
            : po.Lines.OrderBy(l => l.LineNo).Select(l => (l, (PurchaseOrderLineResult?)null, (PreviousPriceState?)null)));
        return new PoUpdateResult(changes, pricing);
    }

    // ── A29-P5-11 §6.2 — split a DRAFT PO raised for a sale order line ────────

    // Moves part of one line's quantity onto a new DRAFT PO for another supplier. The new PO inherits
    // everything that ties it to the sale order (source, both links, the customer address for a drop
    // ship, and the trace_id), so the order's timeline, cancellation and the GRN auto-reservation all
    // treat it as the order's own. It is not "auto-generated": the system chose nothing for it, so
    // its audit columns stay empty and the original PO keeps the record of what the system chose.
    // A46 C5: the moved quantity is a new line made by the factory - a price typed for the new supplier is kept (Manual), otherwise
    // the new supplier's pricelist is tried and the original line's price is the fallback; the quantity left on the original line
    // goes through the pricing middleware (a lower quantity can fall to another tier of a pricelist price).
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
        var poNumber = await GeneratePoNumberAsync(now);
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
            CurrencyId                = po.CurrencyId, // A35 — the moved quantity keeps the price's currency
            DeliveryDate              = po.DeliveryDate,
            OrderDate                 = po.OrderDate,  // A46 - the same order, so the same pricing date
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
        var moved = await _lines.CreateLineAsync(newPo, new CreateLineInput
        {
            Origin             = LineOrigin.Split,
            LineNo             = 1,
            VariantUuid        = line.VariantUuid,
            ItemDescription    = line.ItemDescription,
            Specification      = line.Specification,
            UnitOfMeasure      = line.UnitOfMeasure,
            Quantity           = req.Quantity,
            UnitPrice          = req.UnitPrice is > 0m ? req.UnitPrice.Value : 0m,
            FallbackPrice      = line.UnitPrice,
            LineDiscountPct    = line.LineDiscountPct,
            RequiredDate       = line.RequiredDate,
            LineNotes          = line.LineNotes,
            BudgetCode         = line.BudgetCode,
            WarehouseId        = line.WarehouseId,
            WarehouseName      = line.WarehouseName,
            RequiresInspection = line.RequiresInspection
        });
        newPo.Lines.Add(moved.Line);
        newPo.TotalAmount = newPo.Lines.Sum(l => l.LineTotal);

        line.Quantity  = oldQty - req.Quantity;
        await _pricing.ApplyPricingAsync(line, po, new LineChangeContext { QuantityChanged = true, PreviousQuantity = oldQty });
        line.LineTotal = PurchaseOrderLinePricingMiddleware.LineTotal(line.Quantity, line.UnitPrice);
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
                CurrencyId      = p.CurrencyId,
                TotalAmountBase = p.TotalAmountBase,
                DeliveryDate = p.DeliveryDate,
                OrderDate    = p.OrderDate.ToString("yyyy-MM-dd"),
                CreatedDate  = p.CreatedDate
            })
            .ToListAsync();

        if (_currencyCodes is not null)
            foreach (var group in items.Where(i => i.CurrencyId is not null).GroupBy(i => i.CurrencyId!.Value))
            {
                var code = (await _currencyCodes.GetCodeAsync(group.Key))?.Trim();
                foreach (var item in group) item.CurrencyCode = code;
            }

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

        // A45 — the date basis of each line's pricelist row (soft-deleted rows included, BR-16).
        var pricelistUuids = po.Lines.Where(l => l.SupplierPricelistUuid != null).Select(l => l.SupplierPricelistUuid!.Value).Distinct().ToList();
        var dateBasis = pricelistUuids.Count == 0
            ? new Dictionary<Guid, PricingDateBasis>()
            : await _db.SupplierPricelists.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == po.OrganizationId && pricelistUuids.Contains(r.UUID))
                .ToDictionaryAsync(r => r.UUID, r => r.PricingDateBasis);

        // A46 C10 - the code of each currency a line's price was converted from.
        var originalCodes = new Dictionary<Guid, string?>();
        if (_currencyCodes is not null)
            foreach (var id in po.Lines.Where(l => l.OriginalCurrencyId != null).Select(l => l.OriginalCurrencyId!.Value).Distinct())
                originalCodes[id] = (await _currencyCodes.GetCodeAsync(id))?.Trim();

        return new PoDetailModel
        {
            OrderDate                 = po.OrderDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
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
            CurrencyId            = po.CurrencyId,
            CurrencyCode          = po.CurrencyId is { } cid && _currencyCodes is not null ? (await _currencyCodes.GetCodeAsync(cid))?.Trim() : null,
            ExchangeRate          = po.ExchangeRate,
            BaseCurrencyId        = po.BaseCurrencyId,
            BaseCurrencyCode      = po.BaseCurrencyId is { } bid && _currencyCodes is not null ? (await _currencyCodes.GetCodeAsync(bid))?.Trim() : null,
            RateLockedAt          = po.RateLockedAt,
            TotalAmountBase       = po.TotalAmountBase,
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
                    UnitPriceBase         = l.UnitPriceBase,
                    LineTotalBase         = l.LineTotalBase,
                    QtyReceived           = l.QtyReceived,
                    QtyInvoiced           = l.QtyInvoiced,
                    RequiredDate          = l.RequiredDate,
                    LineNotes             = l.LineNotes,
                    BudgetCode            = l.BudgetCode,
                    WarehouseId           = l.WarehouseId,
                    WarehouseName         = l.WarehouseName,
                    EffectiveWarehouseId  = l.EffectiveWarehouseId,
                    EffectiveWarehouseName = l.EffectiveWarehouseName,
                    RequiresInspection    = l.RequiresInspection,
                    PriceSource           = (int)l.PriceSource,
                    SystemResolvedPrice   = l.SystemResolvedPrice,
                    SupplierPricelistUuid = l.SupplierPricelistUuid,
                    PriceOverrideReason   = l.PriceOverrideReason,
                    PricingDateBasis      = l.SupplierPricelistUuid is { } pu && dateBasis.TryGetValue(pu, out var basis) ? basis.ToString() : null,
                    OriginalCurrencyId    = l.OriginalCurrencyId,
                    OriginalCurrencyCode  = l.OriginalCurrencyId is { } oc && originalCodes.TryGetValue(oc, out var ocCode) ? ocCode : null,
                    OriginalCurrencyPrice = l.OriginalCurrencyPrice,
                    ExchangeRateUsed      = l.ExchangeRateUsed,
                    ExchangeRateDate      = l.ExchangeRateDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
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

    /// <summary>
    /// A31 hotfix — this used to be <c>COUNT(*) + 1</c>, which two concurrent callers can both read
    /// before either commits, handing out the same number twice (confirmed live: two real purchase
    /// orders sharing "PO-2026-00033", one from the automatic Supply Requirement engine and one from
    /// a person, created close enough together to race). <see cref="IDocumentNumberGenerator"/> is
    /// the shared, already-safe counter this codebase's own Sale Order numbering already uses for
    /// exactly this reason (see its own doc comment) — reused here instead of fixing the same bug
    /// a second time.
    /// </summary>
    private Task<string> GeneratePoNumberAsync(DateTime utcNow) => _numbers.NextAsync("PO", utcNow);

    private async Task<string[]> GeneratePoNumbersAsync(DateTime utcNow, int n)
    {
        var numbers = new string[n];
        for (var i = 0; i < n; i++) numbers[i] = await _numbers.NextAsync("PO", utcNow);
        return numbers;
    }
}
