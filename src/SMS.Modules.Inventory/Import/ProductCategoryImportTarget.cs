using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using static SMS.Modules.Inventory.Import.ImportValues;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 D-8 — PRODUCT_CATEGORY (FSD B.4: code, name, parent_code, description, is_active). This system has two fixed levels:
/// a row without parent_code is a category; a row with parent_code is a sub-category of that category. The parent may be
/// an existing category or a row earlier in the same file (Validate treats it as existing); a parent that is itself a
/// sub-category is refused (MISSING_DEPENDENCY — only two levels exist). Match = code (within the parent for sub-categories).
/// </summary>
internal sealed class ProductCategoryImportTarget : IImportTarget
{
    private readonly InventoryDbContext _db;

    public ProductCategoryImportTarget(InventoryDbContext db) => _db = db;

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.ProductCategory, "Product categories",
        "Categories and sub-categories. A row with a parent code becomes a sub-category of that category (two levels).",
        ImportOrder: 2, IsOpeningBalance: false, MatchFields: ["code", "parent_code"],
        Fields:
        [
            new("code", "Code", "Category code, unique per organization (sub-categories: unique within their parent).",
                ImportFieldTypes.Text, true, true, 1, "ELEC", MaxLength: 20, Aliases: ["category_code", "category code", "cat_code"]),
            new("name", "Name", null, ImportFieldTypes.Text, true, false, 2, "Electronics", MaxLength: 200,
                Aliases: ["category_name", "category name", "description1"]),
            new("parent_code", "Parent Code", "Leave empty for a category; a category code makes this row a sub-category of it.",
                ImportFieldTypes.Lookup, false, false, 3, null, MaxLength: 20, LookupList: ImportListKeys.Categories,
                Aliases: ["parent", "parent category", "parent_category_code"]),
            new("description", "Description", null, ImportFieldTypes.Text, false, false, 4, null, MaxLength: 500),
            new("is_active", "Active", null, ImportFieldTypes.Boolean, false, false, 5, "true", DefaultValue: "true",
                Aliases: ["active", "status"])
        ]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var categories = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.IsActive)
            .OrderBy(c => c.Code).Select(c => c.Code).Take(ImportListKeys.MaxValues).ToListAsync(ct);
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.IsActive)
            .OrderBy(s => s.Code).Select(s => s.Code).Distinct().Take(ImportListKeys.MaxValues).ToListAsync(ct);
        return [new(ImportListKeys.Categories, categories), new(ImportListKeys.SubCategories, subs)];
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        (await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct)).Checks.ToList();

    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default) =>
        ImportUnitOfWork.RunAsync<IReadOnlyList<ImportRowOutcome>>(_db, async _tx =>
        {
            var check = await CheckAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values, r.ExistingEntityUuid)).ToList(), ct);
            check.Checks.ThrowIfAny();

            var createdCategories = new Dictionary<string, ProductCategory>(StringComparer.OrdinalIgnoreCase);
            var touched = new List<(int Row, string Status, object Entity)>();

            foreach (var row in rows)
            {
                var plan = check.Plans[row.RowNumber];
                var v = row.Values;
                var update = row.Action == "UPDATE";
                if (update && plan.ExistingId is null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: there is no existing category '{plan.Code}' to update.");
                if (!update && plan.ExistingId is not null)
                    throw new UnprocessableEntityException($"Row {row.RowNumber}: category code '{plan.Code}' already exists.");

                var isActive = Bool(v, "is_active", out _);
                if (plan.ParentCode is null)
                {
                    ProductCategory category;
                    if (update)
                    {
                        category = await _db.ProductCategories.IgnoreQueryFilters()
                            .FirstAsync(c => c.OrganizationId == organizationId && c.Id == plan.ExistingId, ct);
                        if (Text(v, "name") is { } name) category.Name = name;
                        if (Text(v, "description") is { } d) category.Description = d;
                        if (isActive is { } a) category.IsActive = a;
                    }
                    else
                    {
                        category = new ProductCategory
                        {
                            OrganizationId = organizationId,
                            Code           = Text(v, "code")!,
                            Name           = Text(v, "name")!,
                            Description    = Text(v, "description"),
                            IsActive       = isActive ?? true,
                            CreatedDate    = DateTime.UtcNow
                        };
                        _db.ProductCategories.Add(category);
                        createdCategories[category.Code] = category;
                    }
                    touched.Add((row.RowNumber, update ? "UPDATED" : "IMPORTED", category));
                }
                else
                {
                    ProductSubCategory sub;
                    if (update)
                    {
                        sub = await _db.ProductSubCategories.IgnoreQueryFilters()
                            .FirstAsync(s => s.OrganizationId == organizationId && s.Id == plan.ExistingId, ct);
                        if (Text(v, "name") is { } name) sub.Name = name;
                        if (Text(v, "description") is { } d) sub.Description = d;
                        if (isActive is { } a) sub.IsActive = a;
                    }
                    else
                    {
                        sub = new ProductSubCategory
                        {
                            OrganizationId = organizationId,
                            Code           = Text(v, "code")!,
                            Name           = Text(v, "name")!,
                            Description    = Text(v, "description"),
                            IsActive       = isActive ?? true,
                            CreatedDate    = DateTime.UtcNow
                        };
                        if (plan.ParentId is { } parentId) sub.CategoryId = parentId;
                        else if (createdCategories.TryGetValue(plan.ParentCode, out var parent)) sub.Category = parent;
                        else throw new UnprocessableEntityException($"Row {row.RowNumber}: parent category '{plan.ParentCode}' is not in this batch or the database.");
                        _db.ProductSubCategories.Add(sub);
                    }
                    touched.Add((row.RowNumber, update ? "UPDATED" : "IMPORTED", sub));
                }
            }

            await _db.SaveChangesAsync(ct);

            return touched.Select(t => new ImportRowOutcome(t.Row, t.Status, t.Entity switch
            {
                ProductCategory c    => ImportSurrogateIds.For(ImportSurrogateIds.Category, c.Id),
                ProductSubCategory s => ImportSurrogateIds.For(ImportSurrogateIds.SubCategory, s.Id),
                _                    => Guid.Empty
            })).ToList();
        }, ct);

    // ── Checks ──────────────────────────────────────────────────────────────────

    private sealed record Plan(string Code, string? ParentCode, int? ParentId, int? ExistingId);

    private sealed record Checked(RowChecks Checks, Dictionary<int, Plan> Plans);

    private async Task<Checked> CheckAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values, Guid? Existing)> rows, CancellationToken ct)
    {
        var categories = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org).Select(c => new { c.Id, c.Code, c.Name, c.IsActive }).ToListAsync(ct);
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => new { s.Id, s.CategoryId, s.Code, s.Name, s.IsActive }).ToListAsync(ct);

        var categoryByCode = categories.GroupBy(c => c.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var subCodes = subs.Select(s => s.Code.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var checks = new RowChecks(rows.Select(r => r.RowNumber));
        var plans = new Dictionary<int, Plan>();

        // Every top-level / child row of the batch, by code, with its row number (for in-file parents and duplicates).
        var topRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var childRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var code = Text(r.Values, "code");
            if (code is null) continue;
            (Text(r.Values, "parent_code") is null ? topRows : childRows).TryAdd(code, r.RowNumber);
        }

        var seenTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenChild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchTopNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var batchChildNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rowNumber, v, inputExisting) in rows)
        {
            var code = Text(v, "code");
            var name = Text(v, "name");
            var parentCode = Text(v, "parent_code");
            Bool(v, "is_active", out var activeOk);
            if (!activeOk) checks.Error(rowNumber, "is_active", ImportErrorCodes.InvalidType, "Active must be true or false.", Text(v, "is_active"));

            if (code is null)
            {
                checks.Error(rowNumber, "code", ImportErrorCodes.Required, "Code is required.");
                continue;
            }

            if (parentCode is null)
            {
                var existing = categoryByCode.GetValueOrDefault(code);
                int? existingId = existing?.Id;
                if (existingId is null && inputExisting is { } u && ImportSurrogateIds.TryRead(ImportSurrogateIds.Category, u, out var id)
                    && categories.Any(c => c.Id == id))
                    existingId = id;

                plans[rowNumber] = new Plan(code, null, null, existingId);
                checks.Existing(rowNumber, existingId is { } e ? ImportSurrogateIds.For(ImportSurrogateIds.Category, e) : null);

                if (!seenTop.Add(code))
                    checks.Error(rowNumber, "code", ImportErrorCodes.DuplicateInFile, $"Category '{code}' appears more than once in the file.", code);
                if (existingId is null && name is null)
                    checks.Error(rowNumber, "name", ImportErrorCodes.Required, "Name is required for a new category.");
                if (name is not null)
                {
                    if (categories.Any(c => c.Id != existingId && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                        checks.Error(rowNumber, "name", ImportErrorCodes.DuplicateName, $"A category named '{name}' already exists.", name);
                    else if (!batchTopNames.TryAdd(name, rowNumber))
                        checks.Error(rowNumber, "name", ImportErrorCodes.DuplicateName, $"Category name '{name}' appears more than once in the file.", name);
                }
                continue;
            }

            if (string.Equals(parentCode, code, StringComparison.OrdinalIgnoreCase))
            {
                checks.Error(rowNumber, "parent_code", ImportErrorCodes.RuleViolation, "A category cannot be its own parent.", parentCode);
                plans[rowNumber] = new Plan(code, parentCode, null, null);
                continue;
            }

            // Resolve the parent: an existing category, or a category row earlier in this file.
            int? parentId = categoryByCode.GetValueOrDefault(parentCode)?.Id;
            if (parentId is null)
            {
                if (topRows.TryGetValue(parentCode, out var parentRow))
                {
                    if (parentRow > rowNumber)
                        checks.Error(rowNumber, "parent_code", ImportErrorCodes.MissingDependency,
                            $"Parent category '{parentCode}' is further down the file (row {parentRow}); put it before its sub-categories.", parentCode);
                    else if (checks.HasErrors(parentRow))
                        checks.Error(rowNumber, "parent_code", ImportErrorCodes.MissingDependency,
                            $"Parent category '{parentCode}' (row {parentRow}) has errors, so this sub-category cannot be imported.", parentCode);
                }
                else if (subCodes.Contains(parentCode) || childRows.ContainsKey(parentCode))
                {
                    checks.Error(rowNumber, "parent_code", ImportErrorCodes.MissingDependency,
                        $"'{parentCode}' is a sub-category. Only two category levels exist, so a sub-category cannot have sub-categories of its own.", parentCode);
                }
                else
                {
                    checks.Error(rowNumber, "parent_code", ImportErrorCodes.MissingDependency,
                        $"Parent category '{parentCode}' does not exist. Import it first or put it earlier in this file.", parentCode);
                }
            }

            int? existingSubId = parentId is { } pid
                ? subs.FirstOrDefault(s => s.CategoryId == pid && string.Equals(s.Code.Trim(), code, StringComparison.OrdinalIgnoreCase))?.Id
                : null;
            if (existingSubId is null && inputExisting is { } su && ImportSurrogateIds.TryRead(ImportSurrogateIds.SubCategory, su, out var sid)
                && subs.Any(s => s.Id == sid))
                existingSubId = sid;

            plans[rowNumber] = new Plan(code, parentCode, parentId, existingSubId);
            checks.Existing(rowNumber, existingSubId is { } es ? ImportSurrogateIds.For(ImportSurrogateIds.SubCategory, es) : null);

            if (!seenChild.Add($"{parentCode}\u0001{code}"))
                checks.Error(rowNumber, "code", ImportErrorCodes.DuplicateInFile, $"Sub-category '{code}' of '{parentCode}' appears more than once in the file.", code);
            if (existingSubId is null && name is null)
                checks.Error(rowNumber, "name", ImportErrorCodes.Required, "Name is required for a new sub-category.");
            if (name is not null)
            {
                if (parentId is { } p && subs.Any(s => s.CategoryId == p && s.IsActive && s.Id != existingSubId
                                                 && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    checks.Error(rowNumber, "name", ImportErrorCodes.DuplicateName,
                        $"A sub-category named '{name}' already exists under '{parentCode}'.", name);
                else if (!batchChildNames.TryAdd($"{parentCode}\u0001{name}", rowNumber))
                    checks.Error(rowNumber, "name", ImportErrorCodes.DuplicateName,
                        $"Sub-category name '{name}' appears more than once under '{parentCode}' in the file.", name);
            }
        }

        return new Checked(checks, plans);
    }
}
