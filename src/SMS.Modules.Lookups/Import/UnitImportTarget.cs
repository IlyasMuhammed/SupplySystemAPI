using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Lookups.Import;

/// <summary>
/// A40 D-8 — UNIT (FSD B.5: code, name; category / conversion ratio ✕, R-3). Units of measure are Lookups values of type
/// <c>uom</c> whose display name carries the code in parentheses — "Piece (PCS)" — the code being what Product.UomCode
/// stores. An imported unit is the organization's own value "{name} ({code})". Match = code, among the global catalog and
/// the organization's own units; a global (system) unit is never changed by an import — an UPDATE of one is SKIPPED.
/// </summary>
internal sealed partial class UnitImportTarget : IImportTarget
{
    public const string UomSlug = "uom";
    public const string ListKey = "units";

    private readonly LookupsDbContext _db;

    public UnitImportTarget(LookupsDbContext db) => _db = db;

    [GeneratedRegex(@"\(([^()]+)\)\s*$")]
    private static partial Regex TrailingCode();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-\.]*$")]
    private static partial Regex ValidCode();

    /// <summary>The code of a uom value: the trailing "(CODE)" of its display name, else the whole name, upper-case.</summary>
    internal static string CodeOf(string displayName)
    {
        var m = TrailingCode().Match(displayName);
        return (m.Success ? m.Groups[1].Value : displayName).Trim().ToUpperInvariant();
    }

    internal static string DisplayNameOf(string name, string code) =>
        string.Equals(CodeOf(name), code, StringComparison.OrdinalIgnoreCase) && TrailingCode().IsMatch(name)
            ? name
            : $"{name} ({code})";

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Unit, "Units of measure", "Units of measure (code and name). Conversion ratios are not supported.",
        ImportOrder: 1, IsOpeningBalance: false, MatchFields: ["code"],
        Fields:
        [
            new("code", "Code", "Short unit code, e.g. PCS, KG, BOX — what products refer to.", ImportFieldTypes.Text, true, true, 1, "PCS",
                MaxLength: 20, ValidationRegex: @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*$",
                Aliases: ["unit_code", "uom", "uom_code", "unit of measure", "unit"]),
            new("name", "Name", null, ImportFieldTypes.Text, true, false, 2, "Piece", MaxLength: 150,
                Aliases: ["unit_name", "description", "uom_name"])
        ]);

    private async Task<LookupType?> TypeAsync(CancellationToken ct) =>
        await _db.LookupTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == UomSlug, ct);

    private IQueryable<LookupValue> Units(Guid org, Guid typeId) =>
        _db.LookupValues.IgnoreQueryFilters().Where(v => v.TypeId == typeId && (v.IsGlobal || v.OrganizationId == org));

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var type = await TypeAsync(ct);
        if (type is null) return [new(ListKey, [])];
        var names = await Units(organizationId, type.Id).AsNoTracking().Where(v => v.IsActive)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.DisplayName).Select(v => v.DisplayName).ToListAsync(ct);
        return [new(ListKey, names.Select(CodeOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList())];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct)).Checks;

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        try
        {
            if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
                return await ApplyOnceAsync(organizationId, rows, ct);

            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var result = await ApplyOnceAsync(organizationId, rows, ct);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<IReadOnlyList<ImportRowOutcome>> ApplyOnceAsync(Guid org, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct)
    {
        var check = await CheckAsync(org, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
        var bad = check.Checks.Where(c => c.Errors.Count > 0).ToList();
        if (bad.Count > 0)
            throw new UnprocessableEntityException("The batch was not imported: " + string.Join(" | ",
                bad.Take(5).Select(c => $"Row {c.RowNumber}: {string.Join(" ", c.Errors.Select(e => e.Message))}")));

        var type = check.Type ?? throw new UnprocessableEntityException("The unit of measure lookup type does not exist.");
        var outcomes = new List<ImportRowOutcome>();
        var maxSort = await Units(org, type.Id).Select(v => (int?)v.SortOrder).MaxAsync(ct) ?? 0;

        foreach (var row in rows)
        {
            var code = Value(row.Values, "code")!.ToUpperInvariant();
            var name = Value(row.Values, "name");
            var existing = check.Existing[row.RowNumber];

            if (row.Action == "UPDATE")
            {
                if (existing is null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: there is no unit '{code}' to update.");
                var unit = await _db.LookupValues.IgnoreQueryFilters().FirstAsync(v => v.Id == existing.Value, ct);
                if (unit.IsGlobal)
                {
                    // A system unit is shared by every organization: an import never renames it.
                    outcomes.Add(new ImportRowOutcome(row.RowNumber, "SKIPPED", unit.Id));
                    continue;
                }
                if (name is not null) unit.DisplayName = DisplayNameOf(name, code);
                unit.IsActive = true;
                outcomes.Add(new ImportRowOutcome(row.RowNumber, "UPDATED", unit.Id));
            }
            else
            {
                if (existing is not null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: unit '{code}' already exists.");
                var unit = new LookupValue
                {
                    Id             = Guid.NewGuid(),
                    TypeId         = type.Id,
                    DisplayName    = DisplayNameOf(name!, code),
                    IsActive       = true,
                    SortOrder      = ++maxSort,
                    IsGlobal       = false,
                    OrganizationId = org
                };
                _db.LookupValues.Add(unit);
                outcomes.Add(new ImportRowOutcome(row.RowNumber, "IMPORTED", unit.Id));
            }
        }

        await _db.SaveChangesAsync(ct);
        return outcomes;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string field) =>
        values.TryGetValue(field, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private sealed record Checked(IReadOnlyList<ImportRowCheck> Checks, Dictionary<int, Guid?> Existing, LookupType? Type);

    private async Task<Checked> CheckAsync(Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        var type = await TypeAsync(ct);
        var units = await Units(org, type?.Id ?? Guid.Empty).AsNoTracking()
            .Select(v => new { v.Id, v.DisplayName, v.IsGlobal }).ToListAsync(ct);
        var byCode = units.GroupBy(u => CodeOf(u.DisplayName), StringComparer.OrdinalIgnoreCase)
            // The organization's own unit wins over a global one with the same code.
            .ToDictionary(g => g.Key, g => g.OrderBy(u => u.IsGlobal).First(), StringComparer.OrdinalIgnoreCase);

        var checks = new List<ImportRowCheck>();
        var existing = new Dictionary<int, Guid?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, v) in rows)
        {
            var errors = new List<ImportFieldError>();
            var code = Value(v, "code");
            var name = Value(v, "name");
            Guid? match = null;

            if (type is null)
                errors.Add(new ImportFieldError("code", "MISSING_DEPENDENCY", "The 'uom' lookup type does not exist on this server.", code));
            if (code is null)
                errors.Add(new ImportFieldError("code", "REQUIRED", "Code is required.", null));
            else
            {
                if (code.Length > 20)
                    errors.Add(new ImportFieldError("code", "MAX_LENGTH", "Code is longer than 20 characters.", code));
                else if (!ValidCode().IsMatch(code))
                    errors.Add(new ImportFieldError("code", "PATTERN_MISMATCH", "Code may hold letters, digits, '-', '_' and '.' only.", code));

                if (byCode.TryGetValue(code, out var unit)) match = unit.Id;
                if (!seen.Add(code))
                    errors.Add(new ImportFieldError("code", "DUPLICATE_IN_FILE", $"Unit '{code}' appears more than once in the file.", code));
            }

            if (name is null && match is null)
                errors.Add(new ImportFieldError("name", "REQUIRED", "Name is required for a new unit.", null));
            else if (name is not null && code is not null)
            {
                var display = DisplayNameOf(name, code.ToUpperInvariant());
                if (units.Any(u => u.Id != match && string.Equals(u.DisplayName.Trim(), display, StringComparison.OrdinalIgnoreCase)))
                    errors.Add(new ImportFieldError("name", "DUPLICATE_NAME", $"A unit named '{display}' already exists.", name));
            }

            existing[row] = match;
            checks.Add(new ImportRowCheck(row, match, errors));
        }

        return new Checked(checks, existing, type);
    }
}
