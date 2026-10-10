using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

internal sealed class BomService : IBomService
{
    private readonly IBomRepository _repo;

    public BomService(IBomRepository repo) => _repo = repo;

    public Task<Guid>                                CreateAsync(CreateBomRequest req, int userId)             => _repo.CreateAsync(req, userId);
    public Task<PaginatedResponse<BomListItemModel>> GetListAsync(BomListFilter filter)                        => _repo.GetListAsync(filter);
    public Task<BomDetailModel?>                     GetByUuidAsync(Guid uuid)                                 => _repo.GetByUuidAsync(uuid);
    public Task<IReadOnlyList<BomVersionModel>>      GetVersionsAsync(Guid productUuid, Guid? variantUuid)     => _repo.GetVersionsAsync(productUuid, variantUuid);
    public Task                                      UpdateAsync(Guid uuid, UpdateBomRequest req, int userId)  => _repo.UpdateAsync(uuid, req, userId);
    public Task                                      DeleteAsync(Guid uuid, int userId)                        => _repo.DeleteAsync(uuid, userId);
    public Task                                      SubmitAsync(Guid uuid, int userId)                        => _repo.SubmitAsync(uuid, userId);
    public Task                                      ApproveAsync(Guid uuid, int userId)                       => _repo.ApproveAsync(uuid, userId);
    public Task                                      RejectAsync(Guid uuid, int userId, string reason)         => _repo.RejectAsync(uuid, userId, reason);
    public Task                                      ActivateAsync(Guid uuid, int userId)                      => _repo.ActivateAsync(uuid, userId);
    public Task                                      ObsoleteAsync(Guid uuid, int userId, string? reason)      => _repo.ObsoleteAsync(uuid, userId, reason);
    public Task<Guid>                                NewVersionAsync(Guid uuid, int userId)                    => _repo.NewVersionAsync(uuid, userId);
    public Task<BomComparisonModel>                  CompareAsync(Guid leftUuid, Guid rightUuid)               => _repo.CompareAsync(leftUuid, rightUuid);
    public Task                                      SetUsageAsync(Guid uuid, string bomUsage, int userId)     => _repo.SetUsageAsync(uuid, bomUsage, userId);
}

/// <summary>
/// Material cost of a recipe. A purchased input costs what it was last bought at (falling back to
/// its list purchase price); a manufactured input costs whatever its own active recipe rolls up to,
/// so a chain of recipes prices from the raw materials upward. Nothing is stored — costs move.
/// </summary>
internal sealed class BomCostService : IBomCostService
{
    private const int MaxDepth = 10;

    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;

    public BomCostService(MaterialDbContext db, InventoryDbContext inv)
    {
        _db  = db;
        _inv = inv;
    }

    public Task<BomCostModel> CalculateAsync(Guid bomUuid) => CalculateAsync(bomUuid, 0, []);

    private async Task<BomCostModel> CalculateAsync(Guid bomUuid, int depth, HashSet<Guid> productsOnPath)
    {
        var bom = await _db.BillsOfMaterials.AsNoTracking().Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.UUID == bomUuid)
            ?? throw new NotFoundException("BOM", bomUuid);

        var result = new BomCostModel { BomUuid = bom.UUID, Version = bom.Version, BaseQuantity = bom.BaseQuantity };
        if (!productsOnPath.Add(bom.ProductUuid))
        {
            result.Warnings.Add($"BOM {bom.BomNumber} is part of a cycle; its cost was not rolled up.");
            return result;
        }

        var variantUuids = bom.Lines.Select(l => l.MaterialVariantUuid).Distinct().ToList();
        var materials = variantUuids.Count == 0
            ? new Dictionary<Guid, MaterialCostFacts>()
            : await _inv.ProductVariants.AsNoTracking()
                .Where(v => variantUuids.Contains(v.Uuid))
                .Select(v => new MaterialCostFacts(v.Uuid, v.Product.Name + " – " + v.VariantName, v.Product.Uuid,
                    v.Product.SupplyMethod, v.PurchasePrice, v.LastPurchasePrice))
                .ToDictionaryAsync(m => m.Uuid);

        foreach (var line in bom.Lines.OrderBy(l => l.Sequence))
        {
            var gross = decimal.Round(line.Quantity * (1 + line.ScrapPercentage / 100m), 6);
            var cost  = new BomLineCostModel
            {
                LineUuid = line.UUID, MaterialVariantUuid = line.MaterialVariantUuid,
                Quantity = line.Quantity, ScrapPercentage = line.ScrapPercentage, GrossQuantity = gross
            };

            if (!materials.TryGetValue(line.MaterialVariantUuid, out var material))
            {
                cost.CostSource = "NONE";
                result.Warnings.Add($"Line {line.Sequence}: the material variant no longer exists.");
                result.Lines.Add(cost);
                continue;
            }
            cost.MaterialName = material.Name;

            var nested = material.SupplyMethod == SupplyMethod.Manufacture && depth < MaxDepth
                ? await ActiveBomAsync(material.ProductUuid, line.MaterialVariantUuid)
                : null;

            if (nested is not null)
            {
                var rollup = await CalculateAsync(nested.Value, depth + 1, [.. productsOnPath]);
                cost.UnitCost      = rollup.CostPerUnit;
                cost.CostSource    = "BOM_ROLLUP";
                cost.NestedBomUuid = nested;
                result.Warnings.AddRange(rollup.Warnings);
            }
            else if (material.LastPurchasePrice is { } last && last > 0)
            {
                cost.UnitCost   = last;
                cost.CostSource = "LAST_PURCHASE_PRICE";
            }
            else if (material.PurchasePrice > 0)
            {
                cost.UnitCost   = material.PurchasePrice;
                cost.CostSource = "PURCHASE_PRICE";
            }
            else
            {
                cost.CostSource = "NONE";
                result.Warnings.Add($"{material.Name}: no cost is recorded, so it counts as zero.");
            }

            cost.LineCost = decimal.Round(gross * cost.UnitCost, 4);
            result.Lines.Add(cost);
        }

        result.TotalCost   = result.Lines.Sum(l => l.LineCost);
        result.CostPerUnit = bom.BaseQuantity > 0 ? decimal.Round(result.TotalCost / bom.BaseQuantity, 4) : 0m;
        return result;
    }

    /// <summary>The live recipe for an input: one for its exact variant if there is one, else the product's.</summary>
    private async Task<Guid?> ActiveBomAsync(Guid productUuid, Guid variantUuid)
    {
        var candidates = await _db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.ProductUuid == productUuid && b.Status == BomStatus.Active &&
                        (b.ProductVariantUuid == null || b.ProductVariantUuid == variantUuid))
            .Select(b => new { b.UUID, b.ProductVariantUuid })
            .ToListAsync();

        return candidates.FirstOrDefault(c => c.ProductVariantUuid == variantUuid)?.UUID
            ?? candidates.FirstOrDefault(c => c.ProductVariantUuid == null)?.UUID;
    }

    private sealed record MaterialCostFacts(Guid Uuid, string Name, Guid ProductUuid, string SupplyMethod, decimal PurchasePrice, decimal? LastPurchasePrice);
}
