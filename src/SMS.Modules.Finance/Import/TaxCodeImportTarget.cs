using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Import.FinanceImport;

namespace SMS.Modules.Finance.Import;

/// <summary>
/// A40 (TFIN) — TAX_CODE: Finance tax codes (code, name, rate, usage, default). Matched by code; an UPDATE leaves the fields
/// a row leaves blank as they are. The rules of <see cref="TaxCodeService"/> hold: code A–Z 0–9 _ - (≤ 20, stored upper-case),
/// rate 0–100 with two decimals, usage SALES / PURCHASE / BOTH, at most one default per side — a default imported clears
/// the competing ones, and two rows of one file may not both claim the same side. The batch runs under the same per-
/// organization application lock as the tax-code screen's saves.
/// </summary>
internal sealed class TaxCodeImportTarget : IImportTarget
{
    internal const string FCode = "code", FName = "name", FRate = "rate_percent", FUsage = "usage", FDefault = "is_default",
        FDescription = "description", FActive = "is_active";

    internal const string UsageList = "tax_usages";

    private static readonly Regex CodePattern = new("^[A-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FinanceDbContext _db;
    private readonly TimeProvider     _clock;

    public TaxCodeImportTarget(FinanceDbContext db, TimeProvider? clock = null)
    {
        _db    = db;
        _clock = clock ?? TimeProvider.System;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.TaxCode, "Tax codes",
        "Sales and purchase tax codes (code, rate, usage). Documents keep the rate they were raised with.",
        ImportOrder: 6, IsOpeningBalance: false, MatchFields: [FCode],
        Fields:
        [
            new(FCode, "Tax Code", "Unique per organization; letters, digits, '_' and '-'. Stored upper-case.", ImportFieldTypes.Text,
                IsRequired: true, IsKey: true, SortOrder: 1, SampleValue: "GST17", MaxLength: TaxCodeService.MaxCodeLength,
                ValidationRegex: "^[A-Za-z0-9_-]+$", Aliases: ["tax_code", "taxcode", "tax code", "code"]),
            new(FName, "Name", null, ImportFieldTypes.Text, IsRequired: true, IsKey: false, SortOrder: 2, SampleValue: "GST 17%",
                MaxLength: TaxCodeService.MaxNameLength, Aliases: ["tax_name", "description_short"]),
            new(FRate, "Rate %", "0 to 100, at most two decimals.", ImportFieldTypes.Decimal, IsRequired: true, IsKey: false,
                SortOrder: 3, SampleValue: "17", MinValue: 0m, MaxValue: 100m, DecimalPlaces: 2, Aliases: ["rate", "tax_rate", "percent"]),
            new(FUsage, "Usage", "SALES, PURCHASE or BOTH (blank = BOTH on a new code).", ImportFieldTypes.Lookup, IsRequired: false,
                IsKey: false, SortOrder: 4, SampleValue: TaxCodeUsages.Both, MaxLength: 10, DefaultValue: TaxCodeUsages.Both,
                LookupList: UsageList, Aliases: ["tax_usage", "type"]),
            new(FDefault, "Default", "Pre-selected on new documents of its side. At most one default per side.", ImportFieldTypes.Boolean,
                IsRequired: false, IsKey: false, SortOrder: 5, SampleValue: "false", DefaultValue: "false", Aliases: ["default"]),
            new(FDescription, "Description", null, ImportFieldTypes.Text, IsRequired: false, IsKey: false, SortOrder: 6,
                SampleValue: null, MaxLength: TaxCodeService.MaxDescriptionLength),
            new(FActive, "Active", "Blank = active on a new code.", ImportFieldTypes.Boolean, IsRequired: false, IsKey: false,
                SortOrder: 7, SampleValue: "true", DefaultValue: "true", Aliases: ["active"])
        ],
        AllowUpdate: true);

    public Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportTemplateList>>([new ImportTemplateList(UsageList, TaxCodeUsages.All)]);

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(
        Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        EnsureOrganization(_db, organizationId);
        var existing = await LoadAsync(organizationId, tracked: false, ct);
        return Check(rows.Select(r => (r.RowNumber, r.Values)).ToList(), existing).Select(p => p.Check).ToList();
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(
        Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        EnsureOrganization(_db, organizationId);
        if (rows.Count == 0) return [];

        try
        {
            return await InTransactionAsync(_db, TaxCodeService.LockResource(organizationId),
                () => ApplyCoreAsync(organizationId, userId, rows, ct), ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            throw new ConflictException(
                "Another change to this organization's tax codes was saved at the same moment, so nothing was imported. Try again.");
        }
    }

    private async Task<IReadOnlyList<ImportRowOutcome>> ApplyCoreAsync(
        Guid org, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct)
    {
        var all   = await LoadAsync(org, tracked: true, ct);
        var plans = Check(rows.Select(r => (r.RowNumber, r.Values)).ToList(), all);
        if (FirstError(plans.Select(p => p.Check)) is { } error) throw error;

        var now      = _clock.GetUtcNow().UtcDateTime;
        var outcomes = new List<ImportRowOutcome>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var row  = rows[i];
            var plan = plans[i];
            var update = string.Equals(row.Action, "UPDATE", StringComparison.OrdinalIgnoreCase);

            TaxCode entity;
            if (update)
            {
                entity = all.FirstOrDefault(t => t.Uuid == (row.ExistingEntityUuid ?? plan.Existing?.Uuid))
                         ?? throw RowError(row.RowNumber, new(FCode, "IMPORT_ERROR", "The tax code to update no longer exists.", plan.Code));

                if (plan.Name is not null)        entity.Name        = plan.Name;
                if (plan.Description is not null) entity.Description = plan.Description;
                if (plan.Rate is { } rate)        entity.RatePercent = rate;
                if (plan.Usage is not null)       entity.Usage       = plan.Usage;
                if (plan.Active is { } active)    entity.IsActive    = active;
                if (plan.Default is { } isDefault) entity.IsDefault  = isDefault;
                if (!entity.IsActive)             entity.IsDefault   = false;
                entity.ModifiedBy   = userId;
                entity.ModifiedDate = now;
                outcomes.Add(new ImportRowOutcome(row.RowNumber, "UPDATED", entity.Uuid));
            }
            else
            {
                if (plan.Existing is not null)
                    throw RowError(row.RowNumber, new(FCode, "DUPLICATE_IN_DB", $"There is already a tax code {plan.Code}.", plan.Code));

                var active = plan.Active ?? true;
                entity = new TaxCode
                {
                    Uuid           = Guid.NewGuid(),
                    OrganizationId = org,
                    Code           = plan.Code,
                    Name           = plan.Name!,
                    Description    = plan.Description,
                    RatePercent    = plan.Rate!.Value,
                    Usage          = plan.Usage ?? TaxCodeUsages.Both,
                    IsActive       = active,
                    IsDefault      = (plan.Default ?? false) && active,
                    CreatedBy      = userId,
                    CreatedDate    = now
                };
                _db.TaxCodes.Add(entity);
                all.Add(entity);
                outcomes.Add(new ImportRowOutcome(row.RowNumber, "IMPORTED", entity.Uuid));
            }

            // At most one default per side: the imported default dethrones the others (TaxCodeService's rule).
            if (entity.IsDefault)
                foreach (var other in all.Where(o => !ReferenceEquals(o, entity) && o.IsDefault && Overlaps(o.Usage, entity.Usage)))
                {
                    other.IsDefault    = false;
                    other.ModifiedBy   = userId;
                    other.ModifiedDate = now;
                }
        }

        StampOrganization(_db, org);
        await _db.SaveChangesAsync(ct);
        return outcomes;
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    private sealed record Plan(
        ImportRowCheck Check, string Code, TaxCode? Existing, string? Name, string? Description, decimal? Rate, string? Usage,
        bool? Default, bool? Active);

    private static List<Plan> Check(IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, List<TaxCode> existing)
    {
        var plans    = new List<Plan>(rows.Count);
        var seen     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // The defaults the file itself claims: side → first row claiming it.
        var defaults = new List<(string Usage, int Row)>();

        foreach (var (rowNumber, values) in rows)
        {
            var errors = new List<ImportFieldError>();

            var rawCode = Value(values, FCode);
            var code    = rawCode?.ToUpperInvariant() ?? string.Empty;
            if (code.Length == 0)
                errors.Add(new(FCode, "REQUIRED", "The tax code is required.", rawCode));
            else if (code.Length > TaxCodeService.MaxCodeLength)
                errors.Add(new(FCode, "MAX_LENGTH", $"A tax code can be at most {TaxCodeService.MaxCodeLength} characters.", rawCode));
            else if (!CodePattern.IsMatch(code))
                errors.Add(new(FCode, "PATTERN_MISMATCH", "A tax code may contain only the letters A–Z, the digits 0–9, '_' and '-'.", rawCode));

            var match = code.Length == 0 ? null : existing.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

            if (code.Length > 0)
            {
                if (seen.TryGetValue(code, out var first))
                    errors.Add(new(FCode, "DUPLICATE_IN_FILE", $"Tax code {code} is also on row {first}.", rawCode));
                else
                    seen[code] = rowNumber;
            }

            var name = Value(values, FName);
            if (name is null && match is null)
                errors.Add(new(FName, "REQUIRED", "A new tax code needs a name.", null));
            else if (name is { Length: > TaxCodeService.MaxNameLength })
                errors.Add(new(FName, "MAX_LENGTH", $"A tax code's name can be at most {TaxCodeService.MaxNameLength} characters.", name));

            var description = Value(values, FDescription);
            if (description is { Length: > TaxCodeService.MaxDescriptionLength })
                errors.Add(new(FDescription, "MAX_LENGTH", $"A description can be at most {TaxCodeService.MaxDescriptionLength} characters.", description));

            decimal? rate = null;
            var rawRate = Value(values, FRate);
            if (rawRate is null)
            {
                if (match is null) errors.Add(new(FRate, "REQUIRED", "A new tax code needs a rate.", null));
            }
            else if (!TryDecimal(rawRate, out var r))
                errors.Add(new(FRate, "INVALID_TYPE", $"'{rawRate}' is not a number.", rawRate));
            else if (r < 0m || r > 100m)
                errors.Add(new(FRate, "OUT_OF_RANGE", "A tax rate must be between 0 and 100%.", rawRate));
            else if (decimal.Round(r, 2) != r)
                errors.Add(new(FRate, "INVALID_PRECISION", "A tax rate can have at most two decimals.", rawRate));
            else
                rate = r;

            string? usage = null;
            var rawUsage = Value(values, FUsage);
            if (rawUsage is not null)
            {
                usage = rawUsage.ToUpperInvariant();
                if (!TaxCodeUsages.All.Contains(usage))
                {
                    errors.Add(new(FUsage, "LOOKUP_NOT_FOUND", "Usage must be SALES, PURCHASE or BOTH.", rawUsage));
                    usage = null;
                }
            }

            bool? isDefault = null, active = null;
            var rawDefault = Value(values, FDefault);
            if (rawDefault is not null)
            {
                if (TryBool(rawDefault, out var b)) isDefault = b;
                else errors.Add(new(FDefault, "INVALID_TYPE", $"'{rawDefault}' is not true or false.", rawDefault));
            }
            var rawActive = Value(values, FActive);
            if (rawActive is not null)
            {
                if (TryBool(rawActive, out var b)) active = b;
                else errors.Add(new(FActive, "INVALID_TYPE", $"'{rawActive}' is not true or false.", rawActive));
            }

            // Two rows of one file may not both be the default of one side: which would win is not the file's to leave open.
            var effectiveUsage   = usage ?? match?.Usage ?? TaxCodeUsages.Both;
            var effectiveActive  = active ?? match?.IsActive ?? true;
            if (isDefault == true && effectiveActive)
            {
                var clash = defaults.FirstOrDefault(d => Overlaps(d.Usage, effectiveUsage));
                if (clash.Row != 0)
                    errors.Add(new(FDefault, MultipleDefaults,
                        $"Row {clash.Row} already makes a {clash.Usage} code the default; there is at most one default per side.", rawDefault));
                else
                    defaults.Add((effectiveUsage, rowNumber));
            }

            plans.Add(new Plan(new ImportRowCheck(rowNumber, match?.Uuid, errors), code, match, name, description, rate, usage, isDefault, active));
        }

        return plans;
    }

    private static bool Overlaps(string a, string b) =>
        a == TaxCodeUsages.Both || b == TaxCodeUsages.Both || a == b;

    private Task<List<TaxCode>> LoadAsync(Guid org, bool tracked, CancellationToken ct)
    {
        var q = _db.TaxCodes.IgnoreQueryFilters().Where(t => t.OrganizationId == org);
        return (tracked ? q : q.AsNoTracking()).ToListAsync(ct);
    }
}
