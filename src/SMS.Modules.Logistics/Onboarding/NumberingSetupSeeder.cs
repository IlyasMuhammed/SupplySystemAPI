using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Logistics.Onboarding;

/// <summary>
/// A50 D-19 / D-57 — DOC_NUMBERING. Payload <c>{ sequences: [{ documentType, prefix, defaultPrefix?, pattern?, startNumber?,
/// resetYearly?, sharedGenerator? }] }</c> (PAYLOAD-SCHEMAS.md). Writes <c>logistics.document_number_formats</c>; the shared
/// <see cref="IDocumentNumberGenerator"/> then maps the default prefix to the organization's.
/// <list type="bullet">
/// <item>Only documents in <see cref="SharedGeneratorPrefixes"/> are live (this list, not the pack's sharedGenerator flag,
/// decides). Others are stored with IsApplied = false and reported inert (R-3).</item>
/// <item>Only the prefix is used: year and five-digit padding never change; startNumber ≠ 1 and resetYearly = false are
/// reported and ignored (every counter restarts in January).</item>
/// <item>A prefix that is another live document's default prefix, or that two documents share, is refused (the two kinds
/// would draw from one counter).</item>
/// <item>Natural key = documentType. An existing row this seeder did not write is custom and kept.</item>
/// </list>
/// </summary>
internal sealed partial class NumberingSetupSeeder : ISetupCategorySeeder
{
    /// <summary>Document type → the prefix its code passes to the shared generator today. JE is absent: journal numbering
    /// has its own per-journal-type prefixes (A47).</summary>
    internal static readonly IReadOnlyDictionary<string, string> SharedGeneratorPrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PurchaseOrder"]        = "PO",
            ["SaleOrder"]            = "SO",
            ["Quotation"]            = "SQ",
            ["SaleInquiry"]          = "INQ",
            ["DeliveryNote"]         = DocumentNumberPrefix.Delivery,
            ["Shipment"]             = DocumentNumberPrefix.Consignment,
            ["PickList"]             = DocumentNumberPrefix.PickList,
            ["HandlingUnit"]         = DocumentNumberPrefix.HandlingUnit,
            ["Lead"]                 = "LD",
            ["Opportunity"]          = "OP",
            ["DunningRun"]           = "DUN",
            ["PaymentRun"]           = "PR",
            ["ServiceOrder"]         = "SVC",
            ["ServiceIssue"]         = "SMI",
            ["BillOfMaterials"]      = ManufacturingDocumentPrefix.BillOfMaterials,
            ["ProductionOrder"]      = ManufacturingDocumentPrefix.ProductionOrder,
            ["SupplyRequirement"]    = ManufacturingDocumentPrefix.SupplyRequirement,
            ["ProductionIssue"]      = ManufacturingDocumentPrefix.ProductionIssue,
            ["QualityInspection"]    = ManufacturingDocumentPrefix.QualityInspection,
            ["FinishedGoodsReceipt"] = ManufacturingDocumentPrefix.FinishedGoodsReceipt,
        };

    /// <summary>
    /// Prefixes other callers pass to the shared generator that are not configurable here (REV50-5): an organization prefix
    /// equal to one would share its counter. JE = Accounting's JournalNumbers; REC = its reconciliation sequence.
    /// </summary>
    internal static readonly IReadOnlySet<string> ReservedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "JE", "REC" };

    private readonly LogisticsDbContext _db;
    private readonly ITenantContext _tenant;

    public NumberingSetupSeeder(LogisticsDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public string Category => SetupCategories.DocNumbering;
    public string? RequiresModule => null;

    private Guid Org => _tenant.OrganizationId;

    [GeneratedRegex(@"^[A-Z0-9]{1,10}$")]
    private static partial Regex ValidPrefix();

    [GeneratedRegex(@"^([A-Za-z0-9]{1,10})-")]
    private static partial Regex PatternPrefix();

    internal sealed record Sequence(string DocumentType, string Prefix, string DefaultPrefix, string? Pattern, int? StartNumber, bool? ResetYearly, bool Live);

    internal static List<Sequence> Read(JsonElement payload) =>
        SetupJson.Array(payload, "sequences").Select(s =>
        {
            var type = SetupJson.Text(s, "documentType") ?? string.Empty;
            var pattern = SetupJson.Text(s, "pattern");
            var prefix = (SetupJson.Text(s, "prefix")
                          ?? (pattern is not null && PatternPrefix().Match(pattern) is { Success: true } m ? m.Groups[1].Value : null)
                          ?? string.Empty).ToUpperInvariant();
            var live = SharedGeneratorPrefixes.TryGetValue(type, out var known);
            var defaultPrefix = (known ?? SetupJson.Text(s, "defaultPrefix") ?? prefix).ToUpperInvariant();
            var canonical = live ? SharedGeneratorPrefixes.Keys.First(k => string.Equals(k, type, StringComparison.OrdinalIgnoreCase)) : type;
            return new Sequence(canonical, prefix, defaultPrefix, pattern, SetupJson.Int(s, "startNumber"), SetupJson.Bool(s, "resetYearly"), live);
        }).ToList();

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        if (SetupJson.Prop(payload, "sequences") is not { ValueKind: JsonValueKind.Array })
            return ["sequences: an array is required."];
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequences = Read(payload);
        foreach (var s in sequences)
        {
            if (s.DocumentType.Length is 0 or > 50) errors.Add("sequences: documentType is required (at most 50 characters).");
            else if (!seen.Add(s.DocumentType)) errors.Add($"{s.DocumentType}: appears more than once.");
            if (!ValidPrefix().IsMatch(s.Prefix)) errors.Add($"{s.DocumentType}: prefix must be 1–10 capital letters or digits.");
        }
        errors.AddRange(Clashes(sequences));
        return errors;
    }

    /// <summary>A live prefix that is another live document's default prefix, or used by two live documents.</summary>
    private static IEnumerable<string> Clashes(IReadOnlyList<Sequence> sequences)
    {
        var live = sequences.Where(s => s.Live).ToList();
        foreach (var s in live)
        {
            if (SharedGeneratorPrefixes.FirstOrDefault(kv => kv.Value == s.Prefix && !string.Equals(kv.Key, s.DocumentType, StringComparison.OrdinalIgnoreCase))
                is { Key: { } other })
                yield return $"{s.DocumentType}: prefix '{s.Prefix}' is the {other} prefix; the two would share one counter.";
            else if (ReservedPrefixes.Contains(s.Prefix))
                yield return $"{s.DocumentType}: prefix '{s.Prefix}' is reserved by another document numbering; the two would share one counter.";
            if (live.Any(o => o != s && o.Prefix == s.Prefix && string.CompareOrdinal(o.DocumentType, s.DocumentType) < 0))
                yield return $"{s.DocumentType}: prefix '{s.Prefix}' is used by another document type.";
        }
    }

    private static string Fp(string prefix, string? pattern, bool applied) => SetupFingerprint.Of(prefix, pattern, applied);
    private static string Ref(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static List<string> Notes(IEnumerable<Sequence> sequences)
    {
        var warnings = new List<string>();
        foreach (var s in sequences)
        {
            if (!s.Live) warnings.Add($"{s.DocumentType}: stored, not applied — this document does not use the shared numbering (R-3).");
            if (s.StartNumber is { } n && n != 1) warnings.Add($"{s.DocumentType}: startNumber {n} is ignored; numbering starts at 1 each year.");
            if (s.ResetYearly == false) warnings.Add($"{s.DocumentType}: resetYearly=false is ignored; the counter restarts each January.");
        }
        return warnings;
    }

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var sequences = Read(payload);
        var existing = (await _db.DocumentNumberFormats.IgnoreQueryFilters().AsNoTracking().Where(f => f.OrganizationId == Org)
            .Select(f => f.DocumentType).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = Notes(sequences);
        warnings.AddRange(Clashes(sequences));
        return new SetupPreview
        {
            Category = Category, InPayload = sequences.Count, AlreadyPresent = sequences.Count(s => existing.Contains(s.DocumentType)), Warnings = warnings
        };
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var sequences = Read(payload);
        var warnings = Notes(sequences);
        var clashes = Clashes(sequences).ToList();
        warnings.AddRange(clashes.Select(c => c + " Not applied."));
        var clashing = sequences.Where(s => clashes.Any(c => c.StartsWith(s.DocumentType + ":", StringComparison.Ordinal))).Select(s => s.DocumentType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = await _db.DocumentNumberFormats.IgnoreQueryFilters().Where(f => f.OrganizationId == Org).ToListAsync(ct);
        var ours = context.AlreadySeeded.ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var packTypes = sequences.Select(s => s.DocumentType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seeded = new List<SeededRecordRef>();
        var removedKeys = new List<string>();
        int created = 0, skipped = 0, removed = 0;

        // Overwrite drops seeded formats the pack no longer has; Full drops every seeded format (re-written below).
        // A dropped format just means the default prefix again: counters are independent of formats.
        if (mode != SetupResetMode.Merge)
        {
            foreach (var s in context.AlreadySeeded)
            {
                if (mode == SetupResetMode.Overwrite && packTypes.Contains(s.NaturalKey)) continue;
                var row = rows.FirstOrDefault(r => Ref(r.Id) == s.EntityRef);
                if (row is not null) { _db.DocumentNumberFormats.Remove(row); rows.Remove(row); }
                removedKeys.Add(s.NaturalKey);
                ours.Remove(s.NaturalKey);
                if (!packTypes.Contains(s.NaturalKey)) removed++;
            }
            await _db.SaveChangesAsync(ct);
        }

        var added = new List<(string Key, DocumentNumberFormat Row)>();
        foreach (var s in sequences)
        {
            if (clashing.Contains(s.DocumentType)) { skipped++; continue; }
            var row = rows.FirstOrDefault(r => string.Equals(r.DocumentType, s.DocumentType, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                if (ours.TryGetValue(s.DocumentType, out var mine) && mine.EntityRef == Ref(row.Id))
                {
                    if (mode != SetupResetMode.Merge)
                    {
                        row.Prefix = s.Prefix; row.DefaultPrefix = s.DefaultPrefix; row.Pattern = s.Pattern; row.IsApplied = s.Live;
                        row.ModifiedAt = DateTime.UtcNow;
                        created++;
                        seeded.Add(new SeededRecordRef(s.DocumentType, Ref(row.Id), Fp(row.Prefix, row.Pattern, row.IsApplied)));
                    }
                    else { skipped++; seeded.Add(mine); }
                }
                else skipped++; // the organization's own format
                continue;
            }
            var format = new DocumentNumberFormat
            {
                OrganizationId = Org, DocumentType = s.DocumentType, DefaultPrefix = s.DefaultPrefix, Prefix = s.Prefix,
                Pattern = s.Pattern, IsApplied = s.Live, CreatedAt = DateTime.UtcNow
            };
            _db.DocumentNumberFormats.Add(format);
            added.Add((s.DocumentType, format));
            created++;
        }

        await _db.SaveChangesAsync(ct);
        seeded.AddRange(added.Select(a => new SeededRecordRef(a.Key, Ref(a.Row.Id), Fp(a.Row.Prefix, a.Row.Pattern, a.Row.IsApplied))));
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removed, Seeded = seeded,
            RemovedKeys = removedKeys.Where(k => seeded.All(s => !string.Equals(s.NaturalKey, k, StringComparison.OrdinalIgnoreCase))).ToList(),
            Warnings = warnings
        };
    }

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var rows = await _db.DocumentNumberFormats.IgnoreQueryFilters().AsNoTracking().Where(f => f.OrganizationId == Org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var row = rows.FirstOrDefault(r => Ref(r.Id) == s.EntityRef);
            if (row is null) missing.Add(s.NaturalKey);
            else if (Fp(row.Prefix, row.Pattern, row.IsApplied) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = rows.Count, ModifiedKeys = modified, MissingKeys = missing
        };
    }

    /// <summary>Never blocks: issued numbers keep their counter whatever the format says.</summary>
    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default) =>
        Task.FromResult(new SetupReferenceCheck());
}
