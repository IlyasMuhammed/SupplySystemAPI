using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>
/// A34 PC-03 (D-12, D-13, BR-C4-02..04) — the BOM-aware manufacturing lead time of one variant:
/// <c>total = level days + the longest wait among its short inputs</c>, where a short manufactured input is recursed
/// into for its shortfall, a short purchased one waits its supplier lead (D-11), and an input covered by free stock in
/// the parent's production warehouse (else the best single warehouse) waits 0.
/// <para>
/// Breadth-first, one level at a time: one <see cref="IBomStructureReader"/> call, one variant/supplier load and one
/// stock read per level, whatever the BOM's width. Depth is capped at <see cref="MaxDepth"/> levels; a variant that uses
/// one of its own ancestors is a cycle: it waits 0 and the tree gets a warning (T-C4-05). Ancestors are tracked per
/// path, so the same sub-assembly in two branches (a diamond) is not a cycle.
/// </para>
/// </summary>
internal sealed class ManufacturingLeadTimeTree
{
    /// <summary>Levels 0 … 9 (the BOM save and the supply engine use the same limit).</summary>
    public const int MaxDepth = 10;

    private readonly InventoryDbContext _db;
    private readonly LeadTimeInputsLoader _loader;
    private readonly IBomStructureReader? _boms;

    public ManufacturingLeadTimeTree(InventoryDbContext db, LeadTimeInputsLoader loader, IBomStructureReader? boms)
    {
        _db     = db;
        _loader = loader;
        _boms   = boms;
    }

    /// <summary>The tree for <paramref name="root"/> making <paramref name="quantity"/>. Root warnings = every warning of the tree.</summary>
    public async Task<ManufacturingLeadTimeNodeModel> BuildAsync(
        Guid organizationId, VariantLeadTimeFacts root, decimal quantity, LeadTimeDefaultValues defaults, CancellationToken ct)
    {
        var allWarnings = new List<string>();
        var rootNode = NewNode(root, quantity, depth: 0, ancestors: []);
        var level = new List<Node> { rootNode };

        if (_boms is null)
            Warn(rootNode, allWarnings, "BOM structures are not available in this system: only this level's days are counted.");

        while (level.Count > 0 && _boms is not null)
        {
            var boms = await _boms.GetActiveBomsAsync(organizationId, level.Select(n => n.Facts.VariantUuid).Distinct().ToList(), ct);

            var pending = new List<(Node Parent, BomInput Input, decimal Required)>();
            foreach (var node in level)
            {
                if (!boms.TryGetValue(node.Facts.VariantUuid, out var bom))
                {
                    Warn(node, allWarnings, $"No active BOM for {node.Facts.DisplayName}");
                    continue;
                }
                node.Bom = bom;
                var baseQuantity = bom.BaseQuantity > 0 ? bom.BaseQuantity : 1m;
                foreach (var input in bom.Inputs)
                {
                    // The PO-create rule: per base quantity, plus the line's scrap allowance (a percentage).
                    var required = node.Quantity * input.Quantity / baseQuantity * (1m + input.ScrapPercentage / 100m);
                    pending.Add((node, input, decimal.Round(required, 6)));
                }
            }
            if (pending.Count == 0) break;

            var facts = await _loader.LoadAsync(
                organizationId, pending.Select(p => p.Input.MaterialVariantUuid).Distinct().ToList(), defaults, ct);
            var stock = await FreeStockAsync(organizationId, facts.Values.Select(f => f.VariantId).Distinct().ToList(), ct);

            var next = new List<Node>();
            foreach (var (parent, bomInput, required) in pending)
            {
                if (!facts.TryGetValue(bomInput.MaterialVariantUuid, out var f))
                {
                    var missing = new Input
                    {
                        VariantUuid = bomInput.MaterialVariantUuid, DisplayName = bomInput.MaterialVariantUuid.ToString(),
                        Required = required, Shortfall = required, Source = LeadTimeSource.SystemDefault,
                        Detail = "Not found in your organization"
                    };
                    parent.Inputs.Add(missing);
                    Warn(parent, allWarnings, $"A component of {parent.Facts.DisplayName} was not found in your organization");
                    continue;
                }

                var free = stock.FreeFor(f.VariantId, parent.Facts.DefaultProductionWarehouseId);
                var input = new Input
                {
                    VariantUuid = f.VariantUuid, DisplayName = f.DisplayName, Required = required, Free = free,
                    Shortfall = Math.Max(0m, required - free), IsManufactured = f.IsManufactured
                };
                parent.Inputs.Add(input);

                if (input.Shortfall == 0m)
                {
                    // BR-C4-04: covered by free stock, no wait.
                    input.Source = LeadTimeSource.InStock;
                    input.Detail = "Enough free stock";
                }
                else if (f.IsManufactured)
                {
                    input.Source = LeadTimeSource.Bom;
                    if (f.VariantUuid == parent.Facts.VariantUuid || parent.Ancestors.Contains(f.VariantUuid))
                    {
                        input.Detail = "Cycle";
                        Warn(parent, allWarnings, $"Cycle: {f.DisplayName} uses itself");
                    }
                    else if (parent.Depth + 1 >= MaxDepth)
                    {
                        input.Detail = "Depth limit reached";
                        Warn(parent, allWarnings, $"Depth limit {MaxDepth} reached at {f.DisplayName}");
                    }
                    else
                    {
                        var ancestors = new HashSet<Guid>(parent.Ancestors) { parent.Facts.VariantUuid };
                        input.Child = NewNode(f, input.Shortfall, parent.Depth + 1, ancestors);
                        next.Add(input.Child);
                    }
                }
                else
                {
                    // D-13: a purchased (or transferred) input waits its supplier lead (D-11).
                    var lead = LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier, f.Inputs);
                    input.Wait   = lead.Days;
                    input.Source = lead.Source;
                    input.Detail = lead.Detail;
                }
            }
            level = next;
        }

        Total(rootNode);
        var model = ToModel(rootNode);
        model.Warnings = allWarnings;
        return model;
    }

    /// <summary>The number of levels the tree reached (1 = no recursion).</summary>
    public static int Depth(ManufacturingLeadTimeNodeModel node) =>
        1 + node.Inputs.Where(i => i.Node is not null).Select(i => Depth(i.Node!)).DefaultIfEmpty(0).Max();

    private static Node NewNode(VariantLeadTimeFacts facts, decimal quantity, int depth, HashSet<Guid> ancestors)
    {
        var level = LeadTimeResolver.Resolve(LeadTimeComponentCode.Manufacturing, facts.Inputs);
        return new Node(facts, quantity, depth, ancestors) { LevelDays = level.Days, LevelSource = level.Source };
    }

    private static void Warn(Node node, List<string> all, string warning)
    {
        node.Warnings.Add(warning);
        if (!all.Contains(warning)) all.Add(warning);
    }

    /// <summary>Bottom-up: a recursed input waits its child's total; the level adds the longest wait (BR-C4-03).</summary>
    private static int Total(Node node)
    {
        foreach (var input in node.Inputs.Where(i => i.Child is not null))
            input.Wait = Total(input.Child!);
        node.TotalDays = node.LevelDays + node.Inputs.Select(i => i.Wait).DefaultIfEmpty(0).Max();
        return node.TotalDays;
    }

    private static ManufacturingLeadTimeNodeModel ToModel(Node node) => new()
    {
        VariantUuid     = node.Facts.VariantUuid,
        DisplayName     = node.Facts.DisplayName,
        Quantity        = node.Quantity,
        LevelDays       = node.LevelDays,
        LevelDaysSource = node.LevelSource,
        BomUuid         = node.Bom?.BomUuid,
        BomNumber       = node.Bom?.BomNumber,
        BomVersion      = node.Bom?.Version,
        TotalDays       = node.TotalDays,
        Warnings        = node.Warnings,
        Inputs = node.Inputs.Select(i => new ManufacturingLeadTimeInputModel
        {
            VariantUuid    = i.VariantUuid,
            DisplayName    = i.DisplayName,
            RequiredQty    = i.Required,
            FreeQty        = i.Free,
            ShortfallQty   = i.Shortfall,
            IsManufactured = i.IsManufactured,
            WaitDays       = i.Wait,
            Source         = i.Source,
            Detail         = i.Detail,
            Node           = i.Child is null ? null : ToModel(i.Child)
        }).ToList()
    };

    /// <summary>
    /// Free stock (on hand − reserved, never below 0 per row) per variant and warehouse, for the organization given —
    /// one read per level. The tenant filter is set aside: the organization is explicit.
    /// </summary>
    internal async Task<FreeStock> FreeStockAsync(Guid organizationId, IReadOnlyCollection<int> variantIds, CancellationToken ct)
    {
        if (variantIds.Count == 0) return new FreeStock([]);
        var ids = variantIds.ToList();
        var rows = await _db.InventoryItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && ids.Contains(i.VariantId))
            .Select(i => new { i.VariantId, i.WarehouseId, i.QtyOnHand, i.QtyReserved })
            .ToListAsync(ct);
        return new FreeStock(rows
            .GroupBy(r => (r.VariantId, r.WarehouseId))
            .ToDictionary(g => g.Key, g => g.Sum(r => Math.Max(0m, r.QtyOnHand - r.QtyReserved))));
    }

    internal sealed class FreeStock(Dictionary<(int VariantId, int WarehouseId), decimal> byWarehouse)
    {
        /// <summary>
        /// In <paramref name="warehouseId"/> when given (the parent's production warehouse, D-13); otherwise the best
        /// single warehouse — the rule a reservation is measured against (D-14).
        /// </summary>
        public decimal FreeFor(int variantId, int? warehouseId) => warehouseId is int wh
            ? byWarehouse.GetValueOrDefault((variantId, wh))
            : byWarehouse.Where(e => e.Key.VariantId == variantId).Select(e => e.Value).DefaultIfEmpty(0m).Max();
    }

    private sealed class Node(VariantLeadTimeFacts facts, decimal quantity, int depth, HashSet<Guid> ancestors)
    {
        public VariantLeadTimeFacts Facts { get; } = facts;
        public decimal Quantity { get; } = quantity;
        public int Depth { get; } = depth;
        public HashSet<Guid> Ancestors { get; } = ancestors;
        public int LevelDays { get; init; }
        public string LevelSource { get; init; } = string.Empty;
        public BomStructure? Bom { get; set; }
        public List<Input> Inputs { get; } = [];
        public List<string> Warnings { get; } = [];
        public int TotalDays { get; set; }
    }

    private sealed class Input
    {
        public Guid VariantUuid { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public decimal Required { get; init; }
        public decimal Free { get; init; }
        public decimal Shortfall { get; init; }
        public bool IsManufactured { get; init; }
        public int Wait { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public Node? Child { get; set; }
    }
}
