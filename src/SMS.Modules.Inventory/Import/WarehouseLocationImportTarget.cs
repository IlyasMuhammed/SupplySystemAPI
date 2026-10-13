using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 D-8 — WAREHOUSE_LOCATION (warehouse_code, zone_code, zone_name, bin_code, bin_name). A row is a zone of an existing
/// warehouse, or — with bin_code — a bin of that zone (the zone is created when missing). Codes are stored upper-case like
/// the warehouse-structure screens do; bin_name goes to the bin's description (a bin has no name). Match = warehouse +
/// zone (+ bin).
/// </summary>
internal sealed class WarehouseLocationImportTarget : IImportTarget
{
    private readonly InventoryDbContext _db;

    public WarehouseLocationImportTarget(InventoryDbContext db) => _db = db;

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.WarehouseLocation, "Warehouse locations",
        "Zones of a warehouse and, optionally, bins inside a zone. A bin row creates its zone when it is missing.",
        ImportOrder: 5, IsOpeningBalance: false, MatchFields: ["warehouse_code", "zone_code", "bin_code"],
        Fields:
        [
            new("warehouse_code", "Warehouse Code", "An existing warehouse.", ImportFieldTypes.Lookup, true, true, 1, "WH-MAIN",
                MaxLength: 20, LookupList: ImportListKeys.Warehouses, Aliases: ["warehouse", "plant"]),
            new("zone_code", "Zone Code", null, ImportFieldTypes.Text, true, true, 2, "A", MaxLength: 20,
                Aliases: ["zone", "storage type"]),
            new("zone_name", "Zone Name", "Defaults to the zone code for a new zone.", ImportFieldTypes.Text, false, false, 3,
                "Zone A", MaxLength: 200),
            new("bin_code", "Bin Code", "Leave empty for a zone-only row.", ImportFieldTypes.Text, false, true, 4, "A-01-01",
                MaxLength: 20, Aliases: ["bin", "location_code", "storage bin"]),
            new("bin_name", "Bin Name", "Stored as the bin's description.", ImportFieldTypes.Text, false, false, 5, null,
                MaxLength: 500, Aliases: ["bin_description"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && w.IsActive)
            .OrderBy(w => w.Code).Select(w => w.Code).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var zones = await _db.Zones.IgnoreQueryFilters().AsNoTracking()
            .Where(z => z.OrganizationId == organizationId && z.IsActive && z.Code != null)
            .Select(z => z.Code!).Distinct().OrderBy(c => c).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var bins = await _db.Bins.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == organizationId && b.IsActive)
            .Select(b => b.Code).Distinct().OrderBy(c => c).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        return [new(ImportListKeys.Warehouses, warehouses), new(ImportListKeys.Zones, zones), new(ImportListKeys.Bins, bins)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct)).Checks.ToList();

    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default) =>
        ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async _tx =>
        {
            var check = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
            check.Checks.ThrowIfAny();

            var zonesInBatch = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
            var outcomes = new List<(int Row, string Status, object Entity)>();

            foreach (var row in rows)
            {
                var plan = check.Plans[row.RowNumber];
                var v = row.Values;
                var zoneName = Text(v, "zone_name");
                var binName = Text(v, "bin_name");
                var update = row.Action == "UPDATE";
                var exists = plan.BinCode is null ? plan.ZoneId is not null : plan.BinId is not null;
                if (update && !exists)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: location {plan.Describe()} does not exist, so it cannot be updated.");
                if (!update && exists)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: location {plan.Describe()} already exists.");

                // The zone: existing, created earlier in this batch, or new.
                Zone zone;
                var zoneKey = $"{plan.WarehouseId}\u0001{plan.ZoneCode}";
                if (plan.ZoneId is { } zoneId)
                    zone = await _db.Zones.IgnoreQueryFilters().FirstAsync(z => z.OrganizationId == organizationId && z.Id == zoneId, ct);
                else if (!zonesInBatch.TryGetValue(zoneKey, out zone!))
                {
                    zone = new Zone
                    {
                        OrganizationId = organizationId,
                        WarehouseId    = plan.WarehouseId,
                        Code           = plan.ZoneCode,
                        Name           = zoneName ?? plan.ZoneCode,
                        IsActive       = true
                    };
                    _db.Zones.Add(zone);
                    zonesInBatch[zoneKey] = zone;
                }
                if (zoneName is not null) zone.Name = zoneName;

                if (plan.BinCode is null)
                {
                    outcomes.Add((row.RowNumber, update ? "UPDATED" : "IMPORTED", zone));
                    continue;
                }

                Bin bin;
                if (plan.BinId is { } binId)
                {
                    bin = await _db.Bins.IgnoreQueryFilters().FirstAsync(b => b.OrganizationId == organizationId && b.Id == binId, ct);
                    if (binName is not null) bin.Description = binName;
                }
                else
                {
                    bin = new Bin { OrganizationId = organizationId, Code = plan.BinCode, Description = binName, IsActive = true, Zone = zone };
                    _db.Bins.Add(bin);
                }
                outcomes.Add((row.RowNumber, update ? "UPDATED" : "IMPORTED", bin));
            }

            await _db.SaveChangesAsync(ct);
            return outcomes.Select(o => new ImportRowOutcome(o.Row, o.Status, o.Entity switch
            {
                Bin b  => ImportSurrogateIds.For(ImportSurrogateIds.Bin, b.Id),
                Zone z => ImportSurrogateIds.For(ImportSurrogateIds.Zone, z.Id),
                _      => Guid.Empty
            })).ToList();
        }, ct);

    private sealed record Plan(int WarehouseId, string WarehouseCode, string ZoneCode, int? ZoneId, string? BinCode, int? BinId)
    {
        public string Describe() => BinCode is null ? $"{WarehouseCode}/{ZoneCode}" : $"{WarehouseCode}/{ZoneCode}/{BinCode}";
    }

    private sealed record Checked(RowChecks Checks, Dictionary<int, Plan> Plans);

    private async Task<Checked> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == org).Select(w => new { w.Id, w.Code, w.IsActive }).ToListAsync(ct);
        var zones = await _db.Zones.IgnoreQueryFilters().AsNoTracking()
            .Where(z => z.OrganizationId == org).Select(z => new { z.Id, z.WarehouseId, z.Code }).ToListAsync(ct);
        var bins = await _db.Bins.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == org).Select(b => new { b.Id, b.ZoneId, b.Code }).ToListAsync(ct);

        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var plans = new Dictionary<int, Plan>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, v) in rows)
        {
            var warehouseCode = Text(v, "warehouse_code");
            var zoneCode = Code(v, "zone_code");
            var binCode = Code(v, "bin_code");

            if (warehouseCode is null) checks.Error(row, "warehouse_code", ImportErrorCodes.Required, "Warehouse code is required.");
            if (zoneCode is null) checks.Error(row, "zone_code", ImportErrorCodes.Required, "Zone code is required.");
            if (warehouseCode is null || zoneCode is null) continue;

            var warehouse = warehouses.FirstOrDefault(w => string.Equals(w.Code.Trim(), warehouseCode, StringComparison.OrdinalIgnoreCase));
            if (warehouse is null)
            {
                checks.Error(row, "warehouse_code", ImportErrorCodes.LookupNotFound,
                    $"Warehouse '{warehouseCode}' does not exist. Import warehouses first.", warehouseCode);
                continue;
            }

            var zone = zones.FirstOrDefault(z => z.WarehouseId == warehouse.Id
                                              && string.Equals(z.Code?.Trim(), zoneCode, StringComparison.OrdinalIgnoreCase));
            var bin = zone is null || binCode is null ? null
                : bins.FirstOrDefault(b => b.ZoneId == zone.Id && string.Equals(b.Code.Trim(), binCode, StringComparison.OrdinalIgnoreCase));

            plans[row] = new Plan(warehouse.Id, warehouse.Code, zoneCode, zone?.Id, binCode, bin?.Id);
            checks.Existing(row, binCode is null
                ? zone is null ? null : ImportSurrogateIds.For(ImportSurrogateIds.Zone, zone.Id)
                : bin is null ? null : ImportSurrogateIds.For(ImportSurrogateIds.Bin, bin.Id));

            if (!seen.Add($"{warehouse.Id}\u0001{zoneCode}\u0001{binCode}"))
                checks.Error(row, binCode is null ? "zone_code" : "bin_code", ImportErrorCodes.DuplicateInFile,
                    $"Location {plans[row].Describe()} appears more than once in the file.", binCode ?? zoneCode);
        }

        return new Checked(checks, plans);
    }
}
