using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Inventory.Onboarding;

/// <summary>
/// A50 D-17 / D-56 — PRODUCT_CATEGORIES. Payload <c>{ categories: [{ code, name, description?, children?: [{ code, name,
/// description?, children? }] }] }</c> ("subCategories" is accepted for "children"). Inventory has two fixed levels:
/// category → sub-category; anything deeper is flattened into the top category's sub-categories (R-2).
/// <list type="bullet">
/// <item>Natural keys: a category's code (<c>RAW</c>); a sub-category's <c>PARENT/CODE</c>.</item>
/// <item>An existing category with the same code — or, failing that, the same name — is matched (D-17) and left alone
/// unless this seeder created it. Same for sub-categories within their parent.</item>
/// <item>Reset: Overwrite restores seeded names/descriptions and deletes seeded rows the pack dropped; Full deletes every
/// seeded row and re-creates the pack's. A row a product (or a custom sub-category / attribute link) uses is never
/// deleted — it is kept and reported (BR-50-008).</item>
/// </list>
/// </summary>
internal sealed class CategorySetupSeeder : ISetupCategorySeeder
{
    private const int MaxCode = 20, MaxName = 200, MaxDescription = 500;

    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenant;

    public CategorySetupSeeder(InventoryDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public string Category => SetupCategories.ProductCategories;
    public string? RequiresModule => null;

    private Guid Org => _tenant.OrganizationId;

    internal sealed record Node(string Code, string Name, string? Description, string? Parent)
    {
        public string Key => Parent is null ? Code : $"{Parent}/{Code}";
    }

    private static IEnumerable<JsonElement> Children(JsonElement e) =>
        SetupJson.Array(e, "children").Concat(SetupJson.Array(e, "subCategories"));

    /// <summary>Flattened to two levels: every descendant of a top category becomes its sub-category (R-2).</summary>
    internal static List<Node> Read(JsonElement payload)
    {
        var nodes = new List<Node>();
        foreach (var c in SetupJson.Array(payload, "categories"))
        {
            var code = (SetupJson.Text(c, "code") ?? string.Empty).ToUpperInvariant();
            nodes.Add(new Node(code, SetupJson.Text(c, "name") ?? string.Empty, SetupJson.Text(c, "description"), null));
            var stack = new Stack<JsonElement>(Children(c).Reverse());
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                nodes.Add(new Node((SetupJson.Text(s, "code") ?? string.Empty).ToUpperInvariant(),
                    SetupJson.Text(s, "name") ?? string.Empty, SetupJson.Text(s, "description"), code));
                foreach (var g in Children(s).Reverse()) stack.Push(g);
            }
        }
        return nodes;
    }

    private static bool Deeper(JsonElement payload) =>
        SetupJson.Array(payload, "categories").SelectMany(Children).Any(s => Children(s).Any());

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        if (SetupJson.Prop(payload, "categories") is not { ValueKind: JsonValueKind.Array })
            return ["categories: an array is required."];
        var errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Read(payload))
        {
            if (n.Code.Length is 0 or > MaxCode) errors.Add($"'{n.Key}': code is required (at most {MaxCode} characters).");
            else if (!keys.Add(n.Key)) errors.Add($"'{n.Key}': appears more than once.");
            if (n.Name.Length is 0 or > MaxName) errors.Add($"'{n.Key}': name is required (at most {MaxName} characters).");
            if (n.Description?.Length > MaxDescription) errors.Add($"'{n.Key}': description is longer than {MaxDescription} characters.");
        }
        return errors;
    }

    private static string CatRef(int id) => "C:" + id.ToString(CultureInfo.InvariantCulture);
    private static string SubRef(int id) => "S:" + id.ToString(CultureInfo.InvariantCulture);

    private static bool TryRef(string entityRef, out bool isCategory, out int id)
    {
        isCategory = entityRef.StartsWith("C:", StringComparison.Ordinal);
        id = 0;
        return (isCategory || entityRef.StartsWith("S:", StringComparison.Ordinal))
               && int.TryParse(entityRef[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static string Fp(string name, string? description, bool active) => SetupFingerprint.Of(name.Trim(), description, active);

    private Task<List<ProductCategory>> CategoriesAsync(CancellationToken ct) =>
        _db.ProductCategories.IgnoreQueryFilters().Where(c => c.OrganizationId == Org).ToListAsync(ct);

    private Task<List<ProductSubCategory>> SubsAsync(CancellationToken ct) =>
        _db.ProductSubCategories.IgnoreQueryFilters().Where(s => s.OrganizationId == Org).ToListAsync(ct);

    private static ProductCategory? MatchCategory(IEnumerable<ProductCategory> all, Node n) =>
        all.FirstOrDefault(c => string.Equals(c.Code.Trim(), n.Code, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(c => string.Equals(c.Name.Trim(), n.Name, StringComparison.OrdinalIgnoreCase));

    private static ProductSubCategory? MatchSub(IEnumerable<ProductSubCategory> all, int categoryId, Node n) =>
        all.FirstOrDefault(s => s.CategoryId == categoryId && string.Equals(s.Code.Trim(), n.Code, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(s => s.CategoryId == categoryId && string.Equals(s.Name.Trim(), n.Name, StringComparison.OrdinalIgnoreCase));

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var nodes = Read(payload);
        var cats = await CategoriesAsync(ct);
        var subs = await SubsAsync(ct);
        var present = 0;
        foreach (var n in nodes)
        {
            if (n.Parent is null) { if (MatchCategory(cats, n) is not null) present++; }
            else if (cats.FirstOrDefault(c => string.Equals(c.Code.Trim(), n.Parent, StringComparison.OrdinalIgnoreCase)) is { } p
                     && MatchSub(subs, p.Id, n) is not null) present++;
        }
        var warnings = new List<string>();
        if (Deeper(payload)) warnings.Add("Levels below the second are flattened into the top category's sub-categories (R-2).");
        return new SetupPreview { Category = Category, InPayload = nodes.Count, AlreadyPresent = present, Warnings = warnings };
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var nodes = Read(payload);
        var packKeys = nodes.Select(n => n.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ours = context.AlreadySeeded.ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (Deeper(payload)) warnings.Add("Levels below the second were flattened into the top category's sub-categories (R-2).");
        var removedKeys = new List<string>();
        var removed = 0;

        // Overwrite deletes the seeded rows the pack dropped; Full deletes every seeded row (re-created below).
        if (mode != SetupResetMode.Merge)
        {
            var refs = await ReferencedAsync(context.AlreadySeeded, ct);
            var cats = await CategoriesAsync(ct);
            var subs = await SubsAsync(ct);
            // Sub-categories first: a category is deleted only once it has none left.
            foreach (var s in context.AlreadySeeded.OrderByDescending(s => s.NaturalKey.Contains('/')))
            {
                if (mode == SetupResetMode.Overwrite && packKeys.Contains(s.NaturalKey)) continue;
                if (!TryRef(s.EntityRef, out var isCat, out var id)) { removedKeys.Add(s.NaturalKey); continue; }
                if (refs.Contains(s.NaturalKey)) { warnings.Add($"{s.NaturalKey}: kept — it is in use (products, sub-categories or attributes)."); continue; }
                if (isCat)
                {
                    var c = cats.FirstOrDefault(x => x.Id == id);
                    if (c is null) { removedKeys.Add(s.NaturalKey); continue; }
                    if (subs.Any(x => x.CategoryId == id && _db.Entry(x).State != EntityState.Deleted))
                    { warnings.Add($"{s.NaturalKey}: kept — it still has sub-categories."); continue; }
                    if (await _db.CategoryAttributes.IgnoreQueryFilters().AnyAsync(a => a.CategoryId == id, ct))
                    { warnings.Add($"{s.NaturalKey}: kept — attributes are linked to it."); continue; }
                    _db.ProductCategories.Remove(c);
                }
                else
                {
                    var sub = subs.FirstOrDefault(x => x.Id == id);
                    if (sub is null) { removedKeys.Add(s.NaturalKey); continue; }
                    _db.ProductSubCategories.Remove(sub);
                }
                removedKeys.Add(s.NaturalKey);
                if (!packKeys.Contains(s.NaturalKey)) removed++;
            }
            await _db.SaveChangesAsync(ct);
            foreach (var k in removedKeys) ours.Remove(k);
        }

        var categories = await CategoriesAsync(ct);
        var subCategories = await SubsAsync(ct);
        var seeded = new List<SeededRecordRef>();
        int created = 0, skipped = 0;
        var restore = mode != SetupResetMode.Merge;
        var newCats = new List<(string Key, ProductCategory Cat)>();
        var newSubs = new List<(string Key, ProductSubCategory Sub)>();

        foreach (var n in nodes.Where(n => n.Parent is null))
        {
            var existing = MatchCategory(categories, n);
            if (existing is not null)
            {
                if (ours.TryGetValue(n.Key, out var mine) && mine.EntityRef == CatRef(existing.Id))
                {
                    if (restore)
                    {
                        existing.Name = n.Name; existing.Description = n.Description; existing.IsActive = true;
                        created++;
                        seeded.Add(new SeededRecordRef(n.Key, CatRef(existing.Id), Fp(n.Name, n.Description, true)));
                    }
                    else { skipped++; seeded.Add(mine); }
                }
                else skipped++; // the organization's own category: custom, left alone
                continue;
            }
            var category = new ProductCategory
            {
                OrganizationId = Org, Code = n.Code, Name = n.Name, Description = n.Description, IsActive = true, CreatedDate = DateTime.UtcNow
            };
            _db.ProductCategories.Add(category);
            categories.Add(category);
            newCats.Add((n.Key, category));
            created++;
        }
        await _db.SaveChangesAsync(ct);
        // Provenance for the categories created above (their ids exist now).
        seeded.AddRange(newCats.Select(x => new SeededRecordRef(x.Key, CatRef(x.Cat.Id), Fp(x.Cat.Name, x.Cat.Description, true))));

        foreach (var n in nodes.Where(n => n.Parent is not null))
        {
            var parent = categories.FirstOrDefault(c => string.Equals(c.Code.Trim(), n.Parent, StringComparison.OrdinalIgnoreCase))
                         ?? MatchCategory(categories, nodes.First(p => p.Parent is null && p.Code == n.Parent));
            if (parent is null) { warnings.Add($"{n.Key}: parent category not found."); skipped++; continue; }
            var existing = MatchSub(subCategories, parent.Id, n);
            if (existing is not null)
            {
                if (ours.TryGetValue(n.Key, out var mine) && mine.EntityRef == SubRef(existing.Id))
                {
                    if (restore)
                    {
                        existing.Name = n.Name; existing.Description = n.Description; existing.IsActive = true;
                        created++;
                        seeded.Add(new SeededRecordRef(n.Key, SubRef(existing.Id), Fp(n.Name, n.Description, true)));
                    }
                    else { skipped++; seeded.Add(mine); }
                }
                else skipped++;
                continue;
            }
            var sub = new ProductSubCategory
            {
                OrganizationId = Org, CategoryId = parent.Id, Code = n.Code, Name = n.Name, Description = n.Description,
                IsActive = true, CreatedDate = DateTime.UtcNow
            };
            _db.ProductSubCategories.Add(sub);
            subCategories.Add(sub);
            newSubs.Add((n.Key, sub));
            created++;
        }
        await _db.SaveChangesAsync(ct);
        seeded.AddRange(newSubs.Select(x => new SeededRecordRef(x.Key, SubRef(x.Sub.Id), Fp(x.Sub.Name, x.Sub.Description, true))));

        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removed,
            Seeded = seeded, RemovedKeys = removedKeys.Where(k => !seeded.Any(s => string.Equals(s.NaturalKey, k, StringComparison.OrdinalIgnoreCase))).ToList(),
            Warnings = warnings
        };
    }

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var cats = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == Org).ToListAsync(ct);
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == Org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            string? fp = null;
            if (TryRef(s.EntityRef, out var isCat, out var id))
                fp = isCat
                    ? cats.FirstOrDefault(c => c.Id == id) is { } c ? Fp(c.Name, c.Description, c.IsActive) : null
                    : subs.FirstOrDefault(x => x.Id == id) is { } x ? Fp(x.Name, x.Description, x.IsActive) : null;
            if (fp is null) missing.Add(s.NaturalKey);
            else if (fp != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = cats.Count + subs.Count,
            ModifiedKeys = modified, MissingKeys = missing
        };
    }

    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var keys = (await ReferencedAsync(context.AlreadySeeded, ct)).ToList();
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }

    /// <summary>Seeded rows that products use (a category also counts when any of its sub-categories is used).</summary>
    private async Task<HashSet<string>> ReferencedAsync(IReadOnlyList<SeededRecordRef> seeded, CancellationToken ct)
    {
        var used = await _db.Products.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == Org)
            .Select(p => new { p.CategoryId, p.SubCategoryId }).Distinct().ToListAsync(ct);
        var usedCats = used.Where(u => u.CategoryId != null).Select(u => u.CategoryId!.Value).ToHashSet();
        var usedSubs = used.Where(u => u.SubCategoryId != null).Select(u => u.SubCategoryId!.Value).ToHashSet();

        // Everything Full would have to keep (REV50-6): a category whose sub-category is used or was not created by this
        // seeder (the category cannot go while it has one), and a category with attribute links.
        var seededSubs = seeded.Select(s => TryRef(s.EntityRef, out var c, out var i) && !c ? i : 0).Where(i => i > 0).ToHashSet();
        var subs = await _db.ProductSubCategories.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrganizationId == Org)
            .Select(x => new { x.Id, x.CategoryId }).ToListAsync(ct);
        usedCats.UnionWith(subs.Where(x => usedSubs.Contains(x.Id) || !seededSubs.Contains(x.Id)).Select(x => x.CategoryId));
        usedCats.UnionWith(await _db.CategoryAttributes.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == Org)
            .Select(a => a.CategoryId).Distinct().ToListAsync(ct));

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeded)
            if (TryRef(s.EntityRef, out var isCat, out var id) && (isCat ? usedCats.Contains(id) : usedSubs.Contains(id)))
                keys.Add(s.NaturalKey);
        return keys;
    }
}
