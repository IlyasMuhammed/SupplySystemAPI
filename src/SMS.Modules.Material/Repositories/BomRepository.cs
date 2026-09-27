using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Repositories;

/// <summary>
/// Bills of materials (A30 §7–§9): the recipe, its lines, its versions and the six-state approval
/// walk. Product and material facts are read straight from Inventory's context, which this module
/// already holds for MIR/MIV — the same arrangement <see cref="MirRepository"/> uses.
/// </summary>
internal sealed class BomRepository : IBomRepository
{
    /// <summary>§9.4 / A30-P2-07 — how many recipes deep a chain may go before it is refused.</summary>
    private const int MaxChainDepth = 10;

    private static readonly string[] LiveStatuses =
        [BomStatus.Draft, BomStatus.Submitted, BomStatus.Approved, BomStatus.Active];

    private readonly MaterialDbContext        _db;
    private readonly InventoryDbContext       _inv;
    private readonly IDocumentNumberGenerator _numbers;
    // Optional the way every other DI-only-in-production dependency in this codebase is (e.g.
    // PurchaseOrderRepository's own InventoryDbContext): production DI always supplies it, and this
    // repository's own pre-existing unit tests construct it directly without one.
    private readonly IBackgroundJobClient?    _jobs;

    public BomRepository(MaterialDbContext db, InventoryDbContext inv, IDocumentNumberGenerator numbers, IBackgroundJobClient? jobs = null)
    {
        _db      = db;
        _inv     = inv;
        _numbers = numbers;
        _jobs    = jobs;
    }

    // ── Create / update / delete ──────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateBomRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var product     = await ManufacturedProductAsync(req.ProductUuid);
        var variantUuid = await OutputVariantAsync(product.Id, req.ProductVariantUuid);
        // A31-C5 — a new BOM's effective_from defaults to today rather than staying NULL; an
        // existing BOM with a NULL effective_from is left alone (this only applies at creation).
        var effectiveFrom = req.EffectiveFrom ?? DateTime.UtcNow.Date;
        ValidateHeader(req.BaseQuantity, effectiveFrom, req.EffectiveTo);

        var lines = await BuildLinesAsync(req.Lines, product.Uuid, product.Name, currentBomId: null);

        var latest = await _db.BillsOfMaterials
            .Where(b => b.ProductUuid == product.Uuid && b.ProductVariantUuid == variantUuid)
            .MaxAsync(b => (int?)b.Version) ?? 0;

        var now = DateTime.UtcNow;
        var bom = new BillOfMaterial
        {
            BomNumber          = await _numbers.NextAsync(ManufacturingDocumentPrefix.BillOfMaterials, now),
            ProductUuid        = product.Uuid,
            ProductVariantUuid = variantUuid,
            Version            = latest + 1,
            Status             = BomStatus.Draft,
            EffectiveFrom      = effectiveFrom,
            EffectiveTo        = req.EffectiveTo,
            BaseQuantity       = req.BaseQuantity,
            BaseUom            = Uom(req.BaseUom, product.UomCode),
            Notes              = req.Notes?.Trim(),
            CreatedBy          = userId,
            CreatedAt          = now,
            UpdatedAt          = now,
            Lines              = lines
        };

        _db.BillsOfMaterials.Add(bom);
        await _db.SaveChangesAsync();
        return bom.UUID;
    }

    public async Task UpdateAsync(Guid uuid, UpdateBomRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var bom = await LoadAsync(uuid);
        EnsureEditable(bom);

        var product = await ManufacturedProductAsync(bom.ProductUuid);

        var baseQuantity  = req.BaseQuantity ?? bom.BaseQuantity;
        var effectiveFrom = req.ClearEffectiveDates ? null : req.EffectiveFrom ?? bom.EffectiveFrom;
        var effectiveTo   = req.ClearEffectiveDates ? null : req.EffectiveTo   ?? bom.EffectiveTo;
        ValidateHeader(baseQuantity, effectiveFrom, effectiveTo);

        if (req.Lines is not null)
        {
            var lines = await BuildLinesAsync(req.Lines, bom.ProductUuid, product.Name, bom.Id);
            _db.BillOfMaterialLines.RemoveRange(bom.Lines);
            bom.Lines = lines;
        }

        bom.BaseQuantity  = baseQuantity;
        if (!string.IsNullOrWhiteSpace(req.BaseUom)) bom.BaseUom = Uom(req.BaseUom, product.UomCode);
        bom.EffectiveFrom = effectiveFrom;
        bom.EffectiveTo   = effectiveTo;
        if (req.Notes is not null) bom.Notes = req.Notes.Trim();

        // §9.1 — a rejected recipe goes back to the drawing board the moment it is revised.
        bom.Status    = BomStatus.Draft;
        bom.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid uuid, int userId)
    {
        var bom = await LoadAsync(uuid);
        EnsureEditable(bom);

        _db.BillsOfMaterials.Remove(bom);
        await _db.SaveChangesAsync();
    }

    // ── Workflow (§9.1, §9.3) ─────────────────────────────────────────────────

    public async Task SubmitAsync(Guid uuid, int userId)
    {
        var bom = await LoadAsync(uuid);
        if (!BomStatus.IsEditable(bom.Status))
            throw new BadRequestException($"BOM {bom.BomNumber} is {Describe(bom.Status)}; only a draft or rejected recipe can be submitted.");
        if (bom.Lines.Count == 0)
            throw new BadRequestException($"BOM {bom.BomNumber} has no lines. A recipe with nothing in it cannot be submitted.");

        var now = DateTime.UtcNow;
        bom.Status      = BomStatus.Submitted;
        bom.SubmittedBy = userId;
        bom.SubmittedAt = now;
        bom.UpdatedAt   = now;
        await _db.SaveChangesAsync();
    }

    public async Task ApproveAsync(Guid uuid, int userId)
    {
        var bom = await LoadAsync(uuid);
        if (bom.Status != BomStatus.Submitted)
            throw new BadRequestException($"BOM {bom.BomNumber} is {Describe(bom.Status)}; only a submitted recipe can be approved.");
        // Rule 9 — four eyes.
        if (bom.SubmittedBy == userId)
            throw new BadRequestException("The person who submitted a BOM cannot approve it. Ask someone else to review it.");

        var now = DateTime.UtcNow;
        bom.Status     = BomStatus.Approved;
        bom.ApprovedBy = userId;
        bom.ApprovedAt = now;
        bom.UpdatedAt  = now;
        await _db.SaveChangesAsync();
    }

    public async Task RejectAsync(Guid uuid, int userId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required to reject a BOM.");

        var bom = await LoadAsync(uuid);
        if (bom.Status != BomStatus.Submitted)
            throw new BadRequestException($"BOM {bom.BomNumber} is {Describe(bom.Status)}; only a submitted recipe can be rejected.");

        var now = DateTime.UtcNow;
        bom.Status          = BomStatus.Rejected;
        bom.RejectedBy      = userId;
        bom.RejectedAt      = now;
        bom.RejectionReason = reason.Trim();
        bom.UpdatedAt       = now;
        await _db.SaveChangesAsync();
    }

    public async Task ActivateAsync(Guid uuid, int userId)
    {
        var bom = await LoadAsync(uuid);
        if (bom.Status != BomStatus.Approved)
            throw new BadRequestException($"BOM {bom.BomNumber} is {Describe(bom.Status)}; only an approved recipe can be activated.");
        if (bom.Lines.Count == 0)
            throw new BadRequestException($"BOM {bom.BomNumber} has no lines and cannot be activated.");
        if (bom.EffectiveTo is { } to && to.Date < DateTime.UtcNow.Date)
            throw new BadRequestException($"BOM {bom.BomNumber} expired on {to:yyyy-MM-dd}; it cannot be activated.");

        // The chain may have changed since this was approved.
        var product = await ManufacturedProductAsync(bom.ProductUuid);
        await EnsureNoCycleAsync(bom.ProductUuid, product.Name, bom.Lines.Select(l => l.MaterialProductUuid), bom.Id);

        var now = DateTime.UtcNow;

        // Rule 5/6 — one active recipe per product and variant; activating this one retires the
        // other. A31-C6 removed warehouse from this scope: warehouse is a Production Order concern
        // now, not a recipe one, so there is no longer a per-warehouse recipe variant to keep separate.
        var superseded = await _db.BillsOfMaterials
            .Where(b => b.Id != bom.Id && b.Status == BomStatus.Active &&
                        b.ProductUuid == bom.ProductUuid && b.ProductVariantUuid == bom.ProductVariantUuid)
            .ToListAsync();
        foreach (var previous in superseded)
        {
            previous.Status      = BomStatus.Obsolete;
            previous.ObsoletedBy = userId;
            previous.ObsoletedAt = now;
            previous.UpdatedAt   = now;
        }

        bom.Status      = BomStatus.Active;
        bom.ActivatedBy = userId;
        bom.ActivatedAt = now;
        bom.UpdatedAt   = now;
        await _db.SaveChangesAsync();

        // A30-P5-07 — on the recipe's own trace.
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            bom.TraceId,
            new TimelineEvent(ManufacturingTimelineEventTypes.BomActivated, ManufacturingInterfaceCodes.Bom, bom.UUID, bom.BomNumber, DateTime.UtcNow, userId,
                $"Version {bom.Version} for {product.Name}."),
            ManufacturingInterfaceCodes.Bom, bom.BomNumber));
    }

    public async Task ObsoleteAsync(Guid uuid, int userId, string? reason)
    {
        var bom = await LoadAsync(uuid);
        if (bom.Status is not (BomStatus.Active or BomStatus.Approved))
            throw new BadRequestException($"BOM {bom.BomNumber} is {Describe(bom.Status)}; only an active or approved recipe can be made obsolete.");

        var now = DateTime.UtcNow;
        bom.Status      = BomStatus.Obsolete;
        bom.ObsoletedBy = userId;
        bom.ObsoletedAt = now;
        bom.UpdatedAt   = now;
        if (!string.IsNullOrWhiteSpace(reason))
            bom.Notes = string.IsNullOrWhiteSpace(bom.Notes) ? $"Obsoleted: {reason.Trim()}" : $"{bom.Notes}\nObsoleted: {reason.Trim()}";
        await _db.SaveChangesAsync();
    }

    // ── Versioning (§8) ───────────────────────────────────────────────────────

    public async Task<Guid> NewVersionAsync(Guid uuid, int userId)
    {
        var source = await LoadAsync(uuid);
        if (source.Status == BomStatus.Draft)
            throw new BadRequestException($"BOM {source.BomNumber} is still a draft; edit it instead of versioning it.");
        if (source.Status == BomStatus.Submitted)
            throw new BadRequestException($"BOM {source.BomNumber} is under review; wait for the decision before versioning it.");

        var latest = await _db.BillsOfMaterials
            .Where(b => b.ProductUuid == source.ProductUuid && b.ProductVariantUuid == source.ProductVariantUuid)
            .MaxAsync(b => (int?)b.Version) ?? 0;

        var now  = DateTime.UtcNow;
        var next = new BillOfMaterial
        {
            BomNumber          = await _numbers.NextAsync(ManufacturingDocumentPrefix.BillOfMaterials, now),
            TraceId            = source.TraceId,
            ProductUuid        = source.ProductUuid,
            ProductVariantUuid = source.ProductVariantUuid,
            Version            = latest + 1,
            Status             = BomStatus.Draft,
            BaseQuantity       = source.BaseQuantity,
            BaseUom            = source.BaseUom,
            Notes              = source.Notes,
            CreatedBy          = userId,
            CreatedAt          = now,
            UpdatedAt          = now,
            Lines              = source.Lines.OrderBy(l => l.Sequence).Select(l => new BillOfMaterialLine
            {
                Sequence             = l.Sequence,
                MaterialVariantUuid  = l.MaterialVariantUuid,
                MaterialProductUuid  = l.MaterialProductUuid,
                Quantity             = l.Quantity,
                Uom                  = l.Uom,
                ScrapPercentage      = l.ScrapPercentage,
                IsCritical           = l.IsCritical,
                AlternateVariantUuid = l.AlternateVariantUuid,
                Notes                = l.Notes
            }).ToList()
        };

        _db.BillsOfMaterials.Add(next);
        await _db.SaveChangesAsync();
        return next.UUID;
    }

    public async Task<BomComparisonModel> CompareAsync(Guid leftUuid, Guid rightUuid)
    {
        var left  = await LoadAsync(leftUuid);
        var right = await LoadAsync(rightUuid);
        if (left.ProductUuid != right.ProductUuid)
            throw new BadRequestException("Only two versions of the same product's recipe can be compared.");

        var names = await NamesAsync([left, right]);
        var leftLines  = left.Lines.ToDictionary(l => l.MaterialVariantUuid, l => ToLineModel(l, names));
        var rightLines = right.Lines.ToDictionary(l => l.MaterialVariantUuid, l => ToLineModel(l, names));

        var result = new BomComparisonModel
        {
            LeftUuid = left.UUID, LeftVersion = left.Version, LeftStatus = left.Status,
            RightUuid = right.UUID, RightVersion = right.Version, RightStatus = right.Status
        };

        if (left.BaseQuantity != right.BaseQuantity) result.HeaderChanges.Add($"Base quantity {left.BaseQuantity:0.####} → {right.BaseQuantity:0.####}");
        if (left.BaseUom != right.BaseUom)           result.HeaderChanges.Add($"Base UOM {left.BaseUom} → {right.BaseUom}");
        if (left.EffectiveFrom != right.EffectiveFrom || left.EffectiveTo != right.EffectiveTo) result.HeaderChanges.Add("Effective dates changed");

        foreach (var (variantUuid, after) in rightLines)
        {
            if (!leftLines.TryGetValue(variantUuid, out var before)) { result.Added.Add(after); continue; }

            var fields = new List<string>();
            if (before.Quantity != after.Quantity)                       fields.Add("Quantity");
            if (before.Uom != after.Uom)                                 fields.Add("UOM");
            if (before.ScrapPercentage != after.ScrapPercentage)         fields.Add("Scrap %");
            if (before.IsCritical != after.IsCritical)                   fields.Add("Critical");
            if (before.AlternateVariantUuid != after.AlternateVariantUuid) fields.Add("Alternate");
            if (fields.Count > 0)
                result.Changed.Add(new BomLineChangeModel
                {
                    MaterialVariantUuid = variantUuid, MaterialName = after.MaterialProductName + " – " + after.MaterialVariantName,
                    Before = before, After = after, Fields = fields
                });
        }
        foreach (var (variantUuid, before) in leftLines)
            if (!rightLines.ContainsKey(variantUuid)) result.Removed.Add(before);

        result.Added.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        result.Removed.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return result;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<BomListItemModel>> GetListAsync(BomListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.BillsOfMaterials.AsNoTracking().AsQueryable();
        if (filter.ProductUuid is { } p)                query = query.Where(b => b.ProductUuid == p);
        if (!string.IsNullOrWhiteSpace(filter.Status))  query = query.Where(b => b.Status == filter.Status.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            var productUuids = await _inv.Products.AsNoTracking()
                .Where(x => x.Name.ToLower().Contains(term) || x.Sku.ToLower().Contains(term))
                .Select(x => x.Uuid).ToListAsync();
            query = query.Where(b => b.BomNumber.ToLower().Contains(term) || productUuids.Contains(b.ProductUuid));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var boms = await query
            .Include(b => b.Lines)
            .OrderByDescending(b => b.UpdatedAt).ThenByDescending(b => b.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        var names = await NamesAsync(boms);
        return new PaginatedResponse<BomListItemModel>
        {
            Data         = boms.Select(b => Fill(new BomListItemModel(), b, names)).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<BomDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var bom = await _db.BillsOfMaterials.AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.UUID == uuid);
        if (bom is null) return null;

        var names  = await NamesAsync([bom]);
        var detail = Fill(new BomDetailModel(), bom, names);
        detail.TraceId         = bom.TraceId;
        detail.Notes           = bom.Notes;
        detail.CreatedBy       = bom.CreatedBy;
        detail.SubmittedBy     = bom.SubmittedBy;
        detail.SubmittedAt     = bom.SubmittedAt;
        detail.ApprovedBy      = bom.ApprovedBy;
        detail.ApprovedAt      = bom.ApprovedAt;
        detail.RejectedBy      = bom.RejectedBy;
        detail.RejectedAt      = bom.RejectedAt;
        detail.RejectionReason = bom.RejectionReason;
        detail.ActivatedBy     = bom.ActivatedBy;
        detail.ObsoletedBy     = bom.ObsoletedBy;
        detail.ObsoletedAt     = bom.ObsoletedAt;
        detail.Lines           = bom.Lines.OrderBy(l => l.Sequence).ThenBy(l => l.Id).Select(l => ToLineModel(l, names)).ToList();
        return detail;
    }

    public async Task<IReadOnlyList<BomVersionModel>> GetVersionsAsync(Guid productUuid, Guid? productVariantUuid)
    {
        var query = _db.BillsOfMaterials.AsNoTracking().Where(b => b.ProductUuid == productUuid);
        if (productVariantUuid is { } v) query = query.Where(b => b.ProductVariantUuid == v);

        return await query
            .OrderByDescending(b => b.Version)
            .Select(b => new BomVersionModel
            {
                UUID = b.UUID, BomNumber = b.BomNumber, Version = b.Version, Status = b.Status,
                LineCount = b.Lines.Count, CreatedAt = b.CreatedAt, ActivatedAt = b.ActivatedAt, ObsoletedAt = b.ObsoletedAt
            })
            .ToListAsync();
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    private async Task<BillOfMaterial> LoadAsync(Guid uuid) =>
        await _db.BillsOfMaterials.Include(b => b.Lines).FirstOrDefaultAsync(b => b.UUID == uuid)
        ?? throw new NotFoundException("BOM", uuid);

    private static void EnsureEditable(BillOfMaterial bom)
    {
        if (BomStatus.IsEditable(bom.Status)) return;
        throw new BadRequestException(bom.Status == BomStatus.Submitted
            ? $"BOM {bom.BomNumber} is under review and cannot be changed until a decision is made."
            : $"BOM {bom.BomNumber} is {Describe(bom.Status)} and cannot be changed. Create a new version instead.");
    }

    private static string Describe(string status) => status.ToLowerInvariant();

    private static string Uom(string? requested, string? fallback) =>
        string.IsNullOrWhiteSpace(requested) ? (string.IsNullOrWhiteSpace(fallback) ? "PCS" : fallback.Trim().ToUpperInvariant())
                                             : requested.Trim().ToUpperInvariant();

    private static void ValidateHeader(decimal baseQuantity, DateTime? from, DateTime? to)
    {
        if (baseQuantity <= 0)
            throw new BadRequestException("Base quantity must be greater than zero.");
        if (from is { } f && to is { } t && f >= t)
            throw new BadRequestException("Effective from must be before effective to.");
    }

    /// <summary>V-B01 — the output must be something this organization makes.</summary>
    private async Task<ProductFacts> ManufacturedProductAsync(Guid productUuid)
    {
        var product = await _inv.Products.AsNoTracking()
            .Where(p => p.Uuid == productUuid)
            .Select(p => new ProductFacts(p.Id, p.Uuid, p.Name, p.UomCode, p.SupplyMethod, p.IsActive))
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException("Product", productUuid);

        if (!product.IsActive)
            throw new BadRequestException($"Product {product.Name} is inactive.");
        if (product.SupplyMethod != SupplyMethod.Manufacture)
            throw new BadRequestException(
                $"Product {product.Name} is not configured for manufacturing (supply method {product.SupplyMethod}). Set its supply method to MANUFACTURE first.");

        return product;
    }

    private sealed record ProductFacts(int Id, Guid Uuid, string Name, string? UomCode, string SupplyMethod, bool IsActive);

    private async Task<Guid?> OutputVariantAsync(int productId, Guid? variantUuid)
    {
        if (variantUuid is null) return null;
        var ok = await _inv.ProductVariants.AsNoTracking()
            .AnyAsync(v => v.Uuid == variantUuid && v.ProductId == productId && v.IsActive);
        if (!ok) throw new BadRequestException("The variant does not belong to that product or is inactive.");
        return variantUuid;
    }

    private sealed record MaterialFacts(Guid Uuid, string Sku, string VariantName, bool IsActive, bool IsAvailableForProduction,
        Guid ProductUuid, string ProductName, string? UomCode);

    /// <summary>V-B02, V-B03, V-B04 and the duplicates, then the cross-recipe circular walk.</summary>
    private async Task<List<BillOfMaterialLine>> BuildLinesAsync(
        List<BomLineRequest> requests, Guid outputProductUuid, string outputProductName, int? currentBomId)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var duplicate = requests.GroupBy(l => l.MaterialVariantUuid).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException("The same material appears on more than one line. Combine them into one.");

        var wanted = requests.Select(l => l.MaterialVariantUuid)
            .Concat(requests.Where(l => l.AlternateVariantUuid.HasValue).Select(l => l.AlternateVariantUuid!.Value))
            .Distinct().ToList();

        var facts = wanted.Count == 0
            ? new Dictionary<Guid, MaterialFacts>()
            : await _inv.ProductVariants.AsNoTracking()
                .Where(v => wanted.Contains(v.Uuid))
                .Select(v => new MaterialFacts(v.Uuid, v.Sku, v.VariantName, v.IsActive, v.IsAvailableForProduction,
                    v.Product.Uuid, v.Product.Name, v.Product.UomCode))
                .ToDictionaryAsync(f => f.Uuid);

        var lines = new List<BillOfMaterialLine>();
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            if (!facts.TryGetValue(req.MaterialVariantUuid, out var material))
                throw new BadRequestException($"Line {i + 1}: material variant {req.MaterialVariantUuid} does not exist.");

            var name = $"{material.ProductName} – {material.VariantName}";
            if (!material.IsActive)
                throw new BadRequestException($"Material {name} is inactive.");
            if (!material.IsAvailableForProduction)
                throw new BadRequestException(
                    $"Material {name} is not configured as a BOM input. Tick 'Available for production' on the variant first.");
            if (material.ProductUuid == outputProductUuid)
                throw new BadRequestException($"Circular BOM reference detected: {outputProductName} cannot be an input of itself.");
            if (req.Quantity <= 0)
                throw new BadRequestException($"Material {name}: quantity must be greater than zero.");
            if (req.ScrapPercentage < 0 || req.ScrapPercentage >= 100)
                throw new BadRequestException($"Material {name}: scrap percentage must be from 0 up to, but not including, 100.");

            if (req.AlternateVariantUuid is { } alt)
            {
                if (!facts.TryGetValue(alt, out var alternate) || !alternate.IsActive)
                    throw new BadRequestException($"Material {name}: the alternate variant does not exist or is inactive.");
                if (!alternate.IsAvailableForProduction)
                    throw new BadRequestException($"Material {name}: alternate {alternate.ProductName} – {alternate.VariantName} is not available for production.");
                if (alt == req.MaterialVariantUuid)
                    throw new BadRequestException($"Material {name}: the alternate cannot be the material itself.");
            }

            lines.Add(new BillOfMaterialLine
            {
                Sequence             = req.Sequence ?? (i + 1) * 10,
                MaterialVariantUuid  = material.Uuid,
                MaterialProductUuid  = material.ProductUuid,
                Quantity             = req.Quantity,
                Uom                  = Uom(req.Uom, material.UomCode),
                ScrapPercentage      = req.ScrapPercentage,
                IsCritical           = req.IsCritical,
                AlternateVariantUuid = req.AlternateVariantUuid,
                Notes                = req.Notes?.Trim()
            });
        }

        await EnsureNoCycleAsync(outputProductUuid, outputProductName, lines.Select(l => l.MaterialProductUuid), currentBomId);
        return lines;
    }

    /// <summary>
    /// A30-P2-07 — walks every live recipe that makes one of the inputs, and their inputs, and so on.
    /// Reaching the output product again is a cycle; going more than <see cref="MaxChainDepth"/>
    /// recipes deep is refused too. Chained manufacturing (§6.4.1) is exactly this walk not finding
    /// itself.
    /// </summary>
    private async Task EnsureNoCycleAsync(Guid outputProductUuid, string outputProductName, IEnumerable<Guid> inputProductUuids, int? currentBomId)
    {
        var visited = new HashSet<Guid>();
        var frontier = new Queue<(Guid ProductUuid, int Depth)>(inputProductUuids.Distinct().Select(p => (p, 1)));

        while (frontier.Count > 0)
        {
            var (productUuid, depth) = frontier.Dequeue();
            if (!visited.Add(productUuid)) continue;

            var inputs = await _db.BillOfMaterialLines.AsNoTracking()
                .Where(l => l.Bom.ProductUuid == productUuid && LiveStatuses.Contains(l.Bom.Status) &&
                            (currentBomId == null || l.BomId != currentBomId))
                .Select(l => l.MaterialProductUuid)
                .Distinct()
                .ToListAsync();

            foreach (var input in inputs)
            {
                if (input == outputProductUuid)
                    throw new BadRequestException(
                        $"Circular BOM reference detected: {outputProductName} would end up as an input of its own recipe.");
                if (depth + 1 > MaxChainDepth)
                    throw new BadRequestException($"This recipe chain would be more than {MaxChainDepth} levels deep.");
                if (!visited.Contains(input)) frontier.Enqueue((input, depth + 1));
            }
        }
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    private sealed record Names(
        Dictionary<Guid, (string Name, string Sku, string ProductType, string SupplyMethod)> Products,
        Dictionary<Guid, (string Sku, string VariantName, Guid ProductUuid, string? ImageUrl)> Variants);

    private async Task<Names> NamesAsync(IReadOnlyList<BillOfMaterial> boms)
    {
        var productUuids = boms.Select(b => b.ProductUuid)
            .Concat(boms.SelectMany(b => b.Lines).Select(l => l.MaterialProductUuid)).Distinct().ToList();
        var variantUuids = boms.Where(b => b.ProductVariantUuid.HasValue).Select(b => b.ProductVariantUuid!.Value)
            .Concat(boms.SelectMany(b => b.Lines).Select(l => l.MaterialVariantUuid))
            .Concat(boms.SelectMany(b => b.Lines).Where(l => l.AlternateVariantUuid.HasValue).Select(l => l.AlternateVariantUuid!.Value))
            .Distinct().ToList();
        var products = productUuids.Count == 0
            ? new Dictionary<Guid, (string, string, string, string)>()
            : await _inv.Products.AsNoTracking().Where(p => productUuids.Contains(p.Uuid))
                .Select(p => new { p.Uuid, p.Name, p.Sku, p.ProductType, p.SupplyMethod })
                .ToDictionaryAsync(p => p.Uuid, p => (p.Name, p.Sku, p.ProductType, p.SupplyMethod));

        var variants = variantUuids.Count == 0
            ? new Dictionary<Guid, (string, string, Guid, string?)>()
            : await _inv.ProductVariants.AsNoTracking().Where(v => variantUuids.Contains(v.Uuid))
                .Select(v => new { v.Uuid, v.Sku, v.VariantName, ProductUuid = v.Product.Uuid, v.Product.ImageUrl })
                .ToDictionaryAsync(v => v.Uuid, v => (v.Sku, v.VariantName, v.ProductUuid, v.ImageUrl));

        return new Names(products, variants);
    }

    private static T Fill<T>(T model, BillOfMaterial b, Names names) where T : BomListItemModel
    {
        var product = names.Products.GetValueOrDefault(b.ProductUuid);
        model.UUID               = b.UUID;
        model.BomNumber          = b.BomNumber;
        model.ProductUuid        = b.ProductUuid;
        model.ProductName        = product.Name ?? string.Empty;
        model.ProductSku         = product.Sku ?? string.Empty;
        model.ProductVariantUuid = b.ProductVariantUuid;
        model.VariantName        = b.ProductVariantUuid is { } v ? names.Variants.GetValueOrDefault(v).VariantName : null;
        model.Version            = b.Version;
        model.Status             = b.Status;
        model.BaseQuantity       = b.BaseQuantity;
        model.BaseUom            = b.BaseUom;
        model.EffectiveFrom      = b.EffectiveFrom;
        model.EffectiveTo        = b.EffectiveTo;
        model.LineCount          = b.Lines.Count;
        model.CreatedAt          = b.CreatedAt;
        model.UpdatedAt          = b.UpdatedAt;
        model.ActivatedAt        = b.ActivatedAt;
        return model;
    }

    private static BomLineModel ToLineModel(BillOfMaterialLine l, Names names)
    {
        var product = names.Products.GetValueOrDefault(l.MaterialProductUuid);
        var variant = names.Variants.GetValueOrDefault(l.MaterialVariantUuid);
        return new BomLineModel
        {
            UUID                 = l.UUID,
            Sequence             = l.Sequence,
            MaterialProductUuid  = l.MaterialProductUuid,
            MaterialProductName  = product.Name ?? string.Empty,
            MaterialProductType  = product.ProductType ?? string.Empty,
            MaterialSupplyMethod = product.SupplyMethod ?? string.Empty,
            MaterialVariantUuid  = l.MaterialVariantUuid,
            MaterialSku          = variant.Sku ?? string.Empty,
            MaterialVariantName  = variant.VariantName ?? string.Empty,
            MaterialImageUrl     = variant.ImageUrl,
            Quantity             = l.Quantity,
            Uom                  = l.Uom,
            ScrapPercentage      = l.ScrapPercentage,
            GrossQuantity        = decimal.Round(l.Quantity * (1 + l.ScrapPercentage / 100m), 6),
            IsCritical           = l.IsCritical,
            AlternateVariantUuid = l.AlternateVariantUuid,
            AlternateVariantName = l.AlternateVariantUuid is { } a ? names.Variants.GetValueOrDefault(a).VariantName : null,
            Notes                = l.Notes
        };
    }
}
