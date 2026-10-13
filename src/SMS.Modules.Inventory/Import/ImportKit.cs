using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Import;

// A40 (TINV) — helpers shared by Inventory's IImportTarget implementations (docs/data-import/ADDENDUM-40-ANALYSIS.md).

/// <summary>Error codes a target reports (API-CONTRACT codes + target-specific UPPER_SNAKE ones).</summary>
internal static class ImportErrorCodes
{
    public const string Required          = "REQUIRED";
    public const string InvalidType       = "INVALID_TYPE";
    public const string OutOfRange        = "OUT_OF_RANGE";
    public const string LookupNotFound    = "LOOKUP_NOT_FOUND";
    public const string DuplicateInFile   = "DUPLICATE_IN_FILE";
    public const string DuplicateInDb     = "DUPLICATE_IN_DB";
    public const string MissingDependency = "MISSING_DEPENDENCY";
    public const string InvalidDate       = "INVALID_DATE";
    // Target-specific.
    public const string DuplicateName       = "DUPLICATE_NAME";
    public const string SkuInUse            = "SKU_IN_USE";
    public const string TypeChangeRefused   = "TYPE_CHANGE_REFUSED";
    public const string AmbiguousCode       = "AMBIGUOUS_CODE";
    public const string RuleViolation       = "RULE_VIOLATION";
    public const string NotStockable        = "NOT_STOCKABLE";
    public const string SerialTracked       = "SERIAL_TRACKED";
    public const string BatchNotTracked     = "BATCH_NOT_TRACKED";
    public const string OpeningDateNotSet   = "OPENING_DATE_NOT_SET";
}

/// <summary>The rows of a batch with their errors, built while validating.</summary>
internal sealed class RowChecks
{
    private readonly Dictionary<int, List<ImportFieldError>> _errors = new();
    private readonly Dictionary<int, Guid?> _existing = new();
    private readonly List<int> _order = new();

    public RowChecks(IEnumerable<int> rowNumbers)
    {
        foreach (var n in rowNumbers)
        {
            if (_errors.ContainsKey(n)) continue;
            _errors[n] = new List<ImportFieldError>();
            _existing[n] = null;
            _order.Add(n);
        }
    }

    public void Error(int row, string field, string code, string message, string? value = null) =>
        _errors[row].Add(new ImportFieldError(field, code, message, value));

    public void Existing(int row, Guid? uuid) => _existing[row] = uuid;

    public bool HasErrors(int row) => _errors[row].Count > 0;

    public IReadOnlyList<ImportRowCheck> ToList() =>
        _order.Select(n => new ImportRowCheck(n, _existing[n], _errors[n])).ToList();

    /// <summary>Apply refuses the whole batch when any row has an error (IMP-EXC-02).</summary>
    public void ThrowIfAny()
    {
        var bad = _order.Where(n => _errors[n].Count > 0).ToList();
        if (bad.Count == 0) return;
        var first = bad.Take(5).Select(n => $"Row {n}: {string.Join(" ", _errors[n].Select(e => e.Message))}");
        throw new UnprocessableEntityException(
            $"The batch was not imported: {string.Join(" | ", first)}{(bad.Count > 5 ? $" (+{bad.Count - 5} more rows)" : string.Empty)}");
    }
}

/// <summary>Reads the generically parsed values (trimmed, empty → null, booleans "true"/"false", dates yyyy-MM-dd, invariant decimals).</summary>
internal static class ImportValues
{
    public static string? Text(IReadOnlyDictionary<string, string?> values, string field) =>
        values.TryGetValue(field, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public static string? Code(IReadOnlyDictionary<string, string?> values, string field) =>
        Text(values, field)?.ToUpperInvariant();

    /// <summary>Null when absent; <paramref name="ok"/> false when present but not a number.</summary>
    public static decimal? Decimal(IReadOnlyDictionary<string, string?> values, string field, out bool ok)
    {
        ok = true;
        var s = Text(values, field);
        if (s is null) return null;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        ok = false;
        return null;
    }

    public static bool? Bool(IReadOnlyDictionary<string, string?> values, string field, out bool ok)
    {
        ok = true;
        var s = Text(values, field);
        if (s is null) return null;
        switch (s.ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "y": return true;
            case "false": case "0": case "no": case "n": return false;
            default: ok = false; return null;
        }
    }

    public static DateOnly? Date(IReadOnlyDictionary<string, string?> values, string field, out bool ok)
    {
        ok = true;
        var s = Text(values, field);
        if (s is null) return null;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)) return DateOnly.FromDateTime(dt);
        ok = false;
        return null;
    }

    public static string Format(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>
/// Inventory rows that have only an int key (categories, sub-categories, zones, bins) are identified to the Import
/// module by a deterministic surrogate UUID: the id in the first 4 bytes and a hash of the kind in the other 12.
/// No schema change; the same row always gets the same UUID, and a UUID of another kind never decodes.
/// </summary>
internal static class ImportSurrogateIds
{
    public const string Category = "CATEGORY", SubCategory = "SUB_CATEGORY", Zone = "ZONE", Bin = "BIN";

    private static byte[] Tag(string kind) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"SMS.Inventory.Import.{kind}"))[..12];

    public static Guid For(string kind, int id)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(id).CopyTo(bytes, 0);
        Tag(kind).CopyTo(bytes, 4);
        return new Guid(bytes);
    }

    public static bool TryRead(string kind, Guid uuid, out int id)
    {
        var bytes = uuid.ToByteArray();
        id = BitConverter.ToInt32(bytes, 0);
        return bytes.AsSpan(4).SequenceEqual(Tag(kind));
    }
}

internal static class ImportUnitOfWork
{
    /// <summary>
    /// Runs one batch in ONE transaction of this module's DbContext (IMP-EXC-02): its own under the execution strategy,
    /// or the caller's when one is already open. Anything tracked is forgotten on failure, so the scoped context is clean
    /// for the next batch. Off a relational provider (unit tests) there is no transaction.
    /// </summary>
    public static async Task<T> RunAsync<T>(DbContext db, Func<IDbContextTransaction?, Task<T>> work, CancellationToken ct)
    {
        try
        {
            if (!db.Database.IsRelational())
                return await work(null);

            if (db.Database.CurrentTransaction is { } current)
                return await work(current);

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var result = await work(tx);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}

internal static class ImportTargetLists
{
    /// <summary>
    /// A template list of another target (e.g. UNIT's "units", owned by Lookups), resolved lazily so Inventory needs no
    /// reference to that module. Null when that target is not registered in this host.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> OfAsync(
        IServiceProvider? services, string entityType, string key, Guid organizationId, CancellationToken ct)
    {
        var target = services?.GetServices<IImportTarget>().FirstOrDefault(t => t.Describe().EntityType == entityType);
        if (target is null) return null;
        var lists = await target.GetTemplateListsAsync(organizationId, ct);
        return lists.FirstOrDefault(l => l.Key == key)?.Values ?? [];
    }
}

/// <summary>Template list keys (ImportFieldDefinition.LookupList).</summary>
internal static class ImportListKeys
{
    public const string Categories = "categories", SubCategories = "sub_categories", Units = "units", Warehouses = "warehouses",
        Zones = "zones", Bins = "bins", Skus = "skus", PriceTypes = "price_types", Currencies = "currencies",
        ProductTypes = "product_types", SupplyMethods = "supply_methods";

    public const int MaxValues = 1000;
}
