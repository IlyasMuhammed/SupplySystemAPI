using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>A40 — WAREHOUSE (FSD B.6: code, name, address, city, country_code, is_active). Match = code (unique per organization).</summary>
internal sealed class WarehouseImportTarget : IImportTarget
{
    private readonly InventoryDbContext _db;

    public WarehouseImportTarget(InventoryDbContext db) => _db = db;

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Warehouse, "Warehouses", "Warehouses (stores, depots). Zones and bins are a separate import.",
        ImportOrder: 4, IsOpeningBalance: false, MatchFields: ["code"],
        Fields:
        [
            new("code", "Code", "Warehouse code, unique per organization.", ImportFieldTypes.Text, true, true, 1, "WH-MAIN",
                MaxLength: 20, Aliases: ["warehouse_code", "warehouse code", "plant", "storage location"]),
            new("name", "Name", null, ImportFieldTypes.Text, true, false, 2, "Main Warehouse", MaxLength: 200,
                Aliases: ["warehouse_name", "warehouse name"]),
            new("address", "Address", null, ImportFieldTypes.Text, false, false, 3, null, MaxLength: 500, Aliases: ["street"]),
            new("city", "City", null, ImportFieldTypes.Text, false, false, 4, "Lahore", MaxLength: 100),
            new("country_code", "Country", "Country code or name, stored as given.", ImportFieldTypes.Text, false, false, 5, "PK",
                MaxLength: 100, Aliases: ["country"]),
            new("is_active", "Active", null, ImportFieldTypes.Boolean, false, false, 6, "true", DefaultValue: "true",
                Aliases: ["active", "status"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var codes = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && w.IsActive)
            .OrderBy(w => w.Code).Select(w => w.Code).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        return [new(ImportListKeys.Warehouses, codes)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct)).ToList();

    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default) =>
        ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async _tx =>
        {
            var checks = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct);
            checks.ThrowIfAny();
            var existing = checks.ToList().ToDictionary(c => c.RowNumber, c => c.ExistingEntityUuid);

            var outcomes = new List<(int Row, string Status, Domain.Warehouse Entity)>();
            foreach (var row in rows)
            {
                var v = row.Values;
                var match = existing[row.RowNumber];
                var active = Bool(v, "is_active", out _);
                if (row.Action == "UPDATE")
                {
                    if (match is null)
                        throw new UnprocessableEntityException($"Row {row.RowNumber}: there is no existing warehouse '{Text(v, "code")}' to update.");
                    var w = await _db.Warehouses.IgnoreQueryFilters()
                        .FirstAsync(x => x.OrganizationId == organizationId && x.Uuid == match.Value, ct);
                    if (Text(v, "name") is { } name)        w.Name    = name;
                    if (Text(v, "address") is { } address)  w.Address = address;
                    if (Text(v, "city") is { } city)        w.City    = city;
                    if (Text(v, "country_code") is { } cc)  w.Country = cc;
                    if (active is { } a)                    w.IsActive = a;
                    outcomes.Add((row.RowNumber, "UPDATED", w));
                }
                else
                {
                    if (match is not null)
                        throw new UnprocessableEntityException($"Row {row.RowNumber}: warehouse code '{Text(v, "code")}' already exists.");
                    var w = new Domain.Warehouse
                    {
                        Uuid           = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        Code           = Text(v, "code")!,
                        Name           = Text(v, "name")!,
                        Address        = Text(v, "address"),
                        City           = Text(v, "city"),
                        Country        = Text(v, "country_code"),
                        IsActive       = active ?? true,
                        CreatedDate    = DateTime.UtcNow,
                        CreatedBy      = userId
                    };
                    _db.Warehouses.Add(w);
                    outcomes.Add((row.RowNumber, "IMPORTED", w));
                }
            }

            await _db.SaveChangesAsync(ct);
            return outcomes.Select(o => new ImportRowOutcome(o.Row, o.Status, o.Entity.Uuid)).ToList();
        }, ct);

    private async Task<RowChecks> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values, Guid? Existing)> rows, CancellationToken ct)
    {
        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == org).Select(w => new { w.Uuid, w.Code }).ToListAsync(ct);
        var byCode = warehouses.GroupBy(w => w.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Uuid, StringComparer.OrdinalIgnoreCase);

        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, v, inputExisting) in rows)
        {
            Bool(v, "is_active", out var activeOk);
            if (!activeOk) checks.Error(row, "is_active", ImportErrorCodes.InvalidType, "Active must be true or false.", Text(v, "is_active"));

            var code = Text(v, "code");
            if (code is null)
            {
                checks.Error(row, "code", ImportErrorCodes.Required, "Code is required.");
                continue;
            }
            Guid? match = byCode.TryGetValue(code, out var uuid) ? uuid
                : inputExisting is { } given && warehouses.Any(w => w.Uuid == given) ? given : null;
            checks.Existing(row, match);

            if (!seen.Add(code))
                checks.Error(row, "code", ImportErrorCodes.DuplicateInFile, $"Warehouse '{code}' appears more than once in the file.", code);
            if (match is null && Text(v, "name") is null)
                checks.Error(row, "name", ImportErrorCodes.Required, "Name is required for a new warehouse.");
        }
        return checks;
    }
}
