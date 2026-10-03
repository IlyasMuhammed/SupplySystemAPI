using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Services.FinanceSetupFormat;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// Tax codes (SAP alignment S-1/S-2/S-3). Every query is limited to the caller's own organization
/// explicitly, not only through the tenant filter: a super admin bypasses that filter, and a code that
/// belongs to another organization must neither collide with nor be changed through this one.
/// <para>
/// An organization has few codes (tens at most), so a save loads all of them: the uniqueness check, the
/// "one default per side" rule and the message about what changed are then decided on one consistent set.
/// Saves of one organization's codes run one at a time (<see cref="OneSaveAtATimeAsync{T}"/>), so that set
/// cannot go stale between the read and the write.
/// </para>
/// </summary>
internal sealed class TaxCodeService : ITaxCodeService
{
    internal const int MaxCodeLength        = 20;
    internal const int MaxNameLength        = 100;
    internal const int MaxDescriptionLength = 300;

    private static readonly Regex CodePattern =
        new("^[A-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FinanceDbContext                       _db;
    private readonly DemandDbContext                        _demand;
    private readonly TimeProvider                           _clock;
    private readonly IReadOnlyList<ITaxCodeReferenceChecker> _referenceCheckers;

    public TaxCodeService(
        FinanceDbContext db, DemandDbContext demand, TimeProvider? clock = null,
        IEnumerable<ITaxCodeReferenceChecker>? referenceCheckers = null)
    {
        _db                = db;
        _demand            = demand;
        _clock             = clock ?? TimeProvider.System;
        _referenceCheckers = referenceCheckers?.ToList() ?? [];
    }

    private Guid     Org => _db.TenantContext.OrganizationId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TaxCodeModel>> ListAsync(string? side, bool includeInactive, CancellationToken ct = default)
    {
        var onSide = NormalizeSide(side);
        var org    = Org;

        var query = _db.TaxCodes.AsNoTracking().Where(t => t.OrganizationId == org);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        if (onSide is not null) query = query.Where(t => t.Usage == onSide || t.Usage == TaxCodeUsages.Both);

        var rows = await query.ToListAsync(ct);

        return rows
            .OrderByDescending(t => t.IsDefault)
            .ThenByDescending(t => t.IsActive)
            .ThenBy(t => t.Code, StringComparer.Ordinal)
            .Select(ToModel)
            .ToList();
    }

    // ── Create / change ──────────────────────────────────────────────────────

    public async Task<TaxCodeSaved> CreateAsync(SaveTaxCodeRequest req, int userId, CancellationToken ct = default)
    {
        var input = Validate(req);
        var org   = Org;
        return await OneSaveAtATimeAsync(org, () => CreateCoreAsync(org, input, req, userId, ct), ct);
    }

    private async Task<TaxCodeSaved> CreateCoreAsync(Guid org, ValidTaxCode input, SaveTaxCodeRequest req, int userId, CancellationToken ct)
    {
        var all = await _db.TaxCodes.Where(t => t.OrganizationId == org).ToListAsync(ct);

        EnsureCodeFree(all, input.Code, except: null);

        var now    = Now;
        var before = SidesWithDefault(all);
        var entity = new TaxCode
        {
            Uuid           = Guid.NewGuid(),
            OrganizationId = org,
            Code           = input.Code,
            Name           = input.Name,
            Description    = input.Description,
            RatePercent    = input.Rate,
            Usage          = input.Usage,
            IsActive       = req.IsActive,
            // An inactive code cannot be picked, so it cannot be what is pre-selected either.
            IsDefault      = req.IsDefault && req.IsActive,
            CreatedBy      = userId,
            CreatedDate    = now
        };

        var dethroned = ClearCompetingDefaults(all, entity, userId, now);
        _db.TaxCodes.Add(entity);
        await SaveAsync(org, entity, ct);

        var notes = new List<string> { $"Tax code {entity.Code} ({Percent(entity.RatePercent)}%) created." };
        if (!entity.IsActive) notes.Add("It is inactive, so it cannot be picked until it is activated.");
        notes.AddRange(DefaultNotes(entity, dethroned, before, SidesWithDefault([.. all, entity])));

        return new TaxCodeSaved(ToModel(entity), string.Join(" ", notes));
    }

    public async Task<TaxCodeSaved> UpdateAsync(Guid uuid, SaveTaxCodeRequest req, int userId, CancellationToken ct = default)
    {
        var input = Validate(req);
        var org   = Org;
        return await OneSaveAtATimeAsync(org, () => UpdateCoreAsync(org, uuid, input, req, userId, ct), ct);
    }

    private async Task<TaxCodeSaved> UpdateCoreAsync(
        Guid org, Guid uuid, ValidTaxCode input, SaveTaxCodeRequest req, int userId, CancellationToken ct)
    {
        var all    = await _db.TaxCodes.Where(t => t.OrganizationId == org).ToListAsync(ct);
        var entity = all.FirstOrDefault(t => t.Uuid == uuid)
                     ?? throw new NotFoundException("That tax code does not exist in this organization.");

        EnsureCodeFree(all, input.Code, except: entity);

        var oldCode   = entity.Code;
        var oldRate   = entity.RatePercent;
        var wasActive = entity.IsActive;
        if (!string.Equals(oldCode, input.Code, StringComparison.Ordinal))
            await EnsureCodeTextMayChangeAsync(entity, input.Code, ct);

        var now    = Now;
        var before = SidesWithDefault(all);

        entity.Code         = input.Code;
        entity.Name         = input.Name;
        entity.Description  = input.Description;
        entity.RatePercent  = input.Rate;
        entity.Usage        = input.Usage;
        entity.IsActive     = req.IsActive;
        // Deactivating a code clears its default: nothing inactive is ever pre-selected.
        entity.IsDefault    = req.IsDefault && req.IsActive;
        entity.ModifiedBy   = userId;
        entity.ModifiedDate = now;

        var dethroned = ClearCompetingDefaults(all, entity, userId, now);
        await SaveAsync(org, entity, ct);

        var notes = new List<string>
        {
            oldCode == entity.Code ? $"Tax code {entity.Code} updated." : $"Tax code {oldCode} is now {entity.Code}."
        };

        // S-3 — documents keep the rate as a snapshot, so a new rate only reaches lines raised from now on.
        if (oldRate != entity.RatePercent)
            notes.Add($"Its rate changed from {Percent(oldRate)}% to {Percent(entity.RatePercent)}%: documents already raised keep "
                    + $"{Percent(oldRate)}%; new lines use {Percent(entity.RatePercent)}%.");

        if (wasActive && !entity.IsActive)
            notes.Add("It is deactivated: it can no longer be picked, and documents that used it keep it.");
        else if (!wasActive && entity.IsActive)
            notes.Add("It is active again.");

        notes.AddRange(DefaultNotes(entity, dethroned, before, SidesWithDefault(all)));

        return new TaxCodeSaved(ToModel(entity), string.Join(" ", notes));
    }

    // ── Codes from the rates already in use ──────────────────────────────────

    public Task<TaxCodesFromRatesOutcome> CreateFromRatesInUseAsync(int userId, CancellationToken ct = default)
    {
        var org = Org;
        // Serialized like any other save, so two clicks at once do not both try to create TAX17.
        return OneSaveAtATimeAsync(org, () => CreateFromRatesInUseCoreAsync(org, userId, ct), ct);
    }

    private async Task<TaxCodesFromRatesOutcome> CreateFromRatesInUseCoreAsync(Guid org, int userId, CancellationToken ct)
    {
        // Sale-order lines of orders that still exist. TaxPercent is decimal(5,2) — the precision a code holds.
        var used = await _demand.SaleOrderLines.AsNoTracking()
            .Where(l => l.OrganizationId == org && !l.SaleOrder.IsDeleted)
            .Select(l => l.TaxPercent)
            .Distinct()
            .ToListAsync(ct);

        // Normalized (17.00 and 17 are one rate) and limited to what a code can hold; the line validator
        // already keeps rates inside 0–100, so nothing real is dropped here.
        var rates = used
            .Select(r => decimal.Round(r, 2) / 1.00m)
            .Where(r => r >= 0m && r <= 100m)
            .Distinct()
            .OrderBy(r => r)
            .ToList();

        var all     = await _db.TaxCodes.Where(t => t.OrganizationId == org).ToListAsync(ct);
        var covered = all
            .Where(t => t.IsActive && (t.Usage == TaxCodeUsages.Sales || t.Usage == TaxCodeUsages.Both))
            .Select(t => t.RatePercent)
            .ToList();

        var result = new TaxCodesFromRatesResult();
        var now    = Now;

        foreach (var rate in rates)
        {
            if (covered.Any(c => c == rate))
            {
                result.SkippedRates.Add(rate);
                continue;
            }

            var entity = new TaxCode
            {
                Uuid           = Guid.NewGuid(),
                OrganizationId = org,
                Code           = FreeGeneratedCode(rate, all),
                Name           = $"Tax {Percent(rate)}%",
                Description    = "Created from a tax rate already used on sale orders.",
                RatePercent    = rate,
                Usage          = TaxCodeUsages.Sales,
                IsActive       = true,
                IsDefault      = false,
                CreatedBy      = userId,
                CreatedDate    = now
            };

            _db.TaxCodes.Add(entity);
            all.Add(entity);
            covered.Add(rate);
            result.Created.Add(ToModel(entity));
        }

        if (result.Created.Count > 0)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Someone created one of these codes at the same moment (the unique index refused it).
                throw new ConflictException(
                    "Another change to this organization's tax codes was saved at the same moment, so nothing was created. Try again.");
            }
        }

        return new TaxCodesFromRatesOutcome(result, FromRatesMessage(result, rates.Count));
    }

    private static string FromRatesMessage(TaxCodesFromRatesResult result, int ratesInUse)
    {
        if (ratesInUse == 0)
            return "No sale order uses a tax rate yet, so there was nothing to create.";

        if (result.Created.Count == 0)
            return ratesInUse == 1
                ? "The tax rate used on sale orders already has a code; nothing was created."
                : $"All {ratesInUse} tax rates used on sale orders already have a code; nothing was created.";

        var created = string.Join(", ", result.Created.Select(c => $"{c.Code} ({Percent(c.RatePercent)}%)"));
        var message = result.Created.Count == 1
            ? $"Created 1 sales tax code: {created}."
            : $"Created {result.Created.Count} sales tax codes: {created}.";

        if (result.SkippedRates.Count > 0)
            message += result.SkippedRates.Count == 1
                ? " 1 rate already had a code."
                : $" {result.SkippedRates.Count} rates already had a code.";

        return message;
    }

    /// <summary>TAX17, TAX7_5, TAX0 — and TAX17-2 if an (inactive, or purchase-only) TAX17 is already taken.</summary>
    private static string FreeGeneratedCode(decimal rate, IReadOnlyCollection<TaxCode> all)
    {
        var stem      = "TAX" + Percent(rate).Replace('.', '_');
        var candidate = stem;
        for (var n = 2; all.Any(t => string.Equals(t.Code, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}";
        return candidate;
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    private sealed record ValidTaxCode(string Code, string Name, string? Description, decimal Rate, string Usage);

    private static ValidTaxCode Validate(SaveTaxCodeRequest? req)
    {
        if (req is null) throw new BadRequestException("Send the tax code to save.");

        var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
            throw new BadRequestException("Give the tax code a code, e.g. GST17.");
        if (code.Length > MaxCodeLength)
            throw new BadRequestException($"A tax code can be at most {MaxCodeLength} characters; '{code}' has {code.Length}.");
        if (!CodePattern.IsMatch(code))
            throw new BadRequestException($"A tax code may contain only the letters A–Z, the digits 0–9, '_' and '-'; '{code}' does not.");

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new BadRequestException("Give the tax code a name, e.g. \"GST 17%\".");
        if (name.Length > MaxNameLength)
            throw new BadRequestException($"A tax code's name can be at most {MaxNameLength} characters.");

        var description = Clean(req.Description);
        if (description is not null && description.Length > MaxDescriptionLength)
            throw new BadRequestException($"A tax code's description can be at most {MaxDescriptionLength} characters.");

        ValidateRate(req.RatePercent);

        var usage = (req.Usage ?? string.Empty).Trim().ToUpperInvariant();
        if (!TaxCodeUsages.All.Contains(usage))
            throw new BadRequestException("Usage must be SALES, PURCHASE or BOTH.");

        return new ValidTaxCode(code, name, description, req.RatePercent, usage);
    }

    internal static void ValidateRate(decimal rate)
    {
        if (rate < 0m || rate > 100m)
            throw new BadRequestException(
                $"A tax rate must be between 0 and 100%; {rate.ToString(CultureInfo.InvariantCulture)}% is not.");
        if (decimal.Round(rate, 2) != rate)
            throw new BadRequestException(
                $"A tax rate can have at most two decimals, like 17.25; {rate.ToString(CultureInfo.InvariantCulture)} has more.");
    }

    /// <summary>SALES or PURCHASE (any case), or null for "every side". BOTH is a code's usage, not a side.</summary>
    internal static string? NormalizeSide(string? side)
    {
        if (string.IsNullOrWhiteSpace(side)) return null;

        var normalized = side.Trim().ToUpperInvariant();
        return normalized is TaxCodeUsages.Sales or TaxCodeUsages.Purchase
            ? normalized
            : throw new BadRequestException("side must be SALES or PURCHASE.");
    }

    private static void EnsureCodeFree(IEnumerable<TaxCode> all, string code, TaxCode? except)
    {
        var other = all.FirstOrDefault(t => !ReferenceEquals(t, except) && string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
        if (other is null) return;

        throw new ConflictException(other.IsActive
            ? $"There is already a tax code {other.Code}. Codes are unique within an organization."
            : $"There is already a tax code {other.Code}, deactivated. Codes are unique within an organization — reactivate {other.Code} instead.");
    }

    /// <summary>
    /// A code's text is printed on documents and matched to QuickBooks tax codes by text (S-11), so once any
    /// document line carries it, or another module keeps it by its text (<see cref="ITaxCodeReferenceChecker"/>:
    /// a QuickBooks code mapping), it stays. Until then a typo can still be fixed.
    /// </summary>
    private async Task EnsureCodeTextMayChangeAsync(TaxCode entity, string newCode, CancellationToken ct)
    {
        var uuid      = entity.Uuid;
        var documents = new List<string>();

        if (await _demand.SaleOrderLines.AnyAsync(l => l.TaxCodeUuid == uuid, ct))  documents.Add("sale orders");
        if (await _db.SalesInvoiceLines.AnyAsync(l => l.TaxCodeUuid == uuid, ct))    documents.Add("sales invoices");
        if (await _db.Invoices.AnyAsync(i => i.TaxCodeUuid == uuid, ct))             documents.Add("supplier invoices");

        var mappings = new List<string>();
        foreach (var checker in _referenceCheckers)
            if (await checker.FindCodeReferenceAsync(entity.OrganizationId, entity.Code, ct) is { Length: > 0 } where)
                mappings.Add(where);

        if (documents.Count == 0 && mappings.Count == 0) return;

        var reasons = new List<string>();
        if (documents.Count > 0) reasons.Add($"is already used on {JoinWords(documents)}, which keep and print the code");
        if (mappings.Count > 0)  reasons.Add($"is mapped in {JoinWords(mappings)}, which find it by its text");

        throw new ConflictException(
            $"{entity.Code} {string.Join(", and ", reasons)}, so it cannot be renamed to {newCode}. "
          + $"Create {newCode} as a new code{(mappings.Count > 0 ? ", map it" : "")} and deactivate {entity.Code} instead.");
    }

    /// <summary>
    /// "At most one default per side": a SALES default competes with SALES and BOTH defaults, a PURCHASE
    /// default with PURCHASE and BOTH, and a BOTH default with every other default. Returns the codes
    /// that stopped being a default.
    /// </summary>
    private static List<TaxCode> ClearCompetingDefaults(IEnumerable<TaxCode> all, TaxCode saved, int userId, DateTime now)
    {
        var cleared = new List<TaxCode>();
        if (!saved.IsDefault) return cleared;

        foreach (var other in all)
        {
            if (ReferenceEquals(other, saved) || !other.IsDefault) continue;
            if (!Overlaps(other.Usage, saved.Usage)) continue;

            other.IsDefault    = false;
            other.ModifiedBy   = userId;
            other.ModifiedDate = now;
            cleared.Add(other);
        }

        return cleared;
    }

    private static bool Overlaps(string a, string b) =>
        a == TaxCodeUsages.Both || b == TaxCodeUsages.Both || a == b;

    /// <summary>Which sides (SALES, PURCHASE) have an active default code.</summary>
    private static HashSet<string> SidesWithDefault(IEnumerable<TaxCode> codes)
    {
        var sides = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes.Where(c => c.IsDefault && c.IsActive))
        {
            if (code.Usage is TaxCodeUsages.Sales or TaxCodeUsages.Both)    sides.Add(TaxCodeUsages.Sales);
            if (code.Usage is TaxCodeUsages.Purchase or TaxCodeUsages.Both) sides.Add(TaxCodeUsages.Purchase);
        }
        return sides;
    }

    private static IEnumerable<string> DefaultNotes(TaxCode saved, List<TaxCode> dethroned, HashSet<string> before, HashSet<string> after)
    {
        if (saved.IsDefault)
            yield return $"It is now the default for {SideLabel(saved.Usage)}.";

        if (dethroned.Count == 1)
            yield return $"{dethroned[0].Code} is no longer the default.";
        else if (dethroned.Count > 1)
            yield return $"{JoinWords(dethroned.Select(d => d.Code).ToList())} are no longer defaults.";

        var lost = before.Except(after).OrderByDescending(s => s == TaxCodeUsages.Sales).ToList();
        if (lost.Count == 2)
            yield return "No code is the default for sales or purchases now.";
        else if (lost.Count == 1)
            yield return $"No code is the default for {SideLabel(lost[0])} now.";
    }

    private static string JoinWords(IReadOnlyList<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1]
    };

    // ── One save at a time per organization ──────────────────────────────────

    /// <summary>How long a save waits for another save of the same organization's codes before it gives up with a 409.</summary>
    internal int LockTimeoutMilliseconds { get; init; } = 15_000;

    /// <summary>
    /// Runs one save of the organization's tax codes so that no other save of the same organization's codes
    /// runs alongside it. "At most one default per side" is decided by reading every code and clearing the
    /// other defaults; no index can hold it (a BOTH code counts for two sides), and two saves that read at the
    /// same moment would each keep their own default. On SQL Server the work therefore runs in a transaction
    /// holding an exclusive application lock named after the organization: the second save waits until the
    /// first has committed and then reads what it wrote. Other organizations never wait. The lock goes with
    /// the transaction — a caller's own, when there is one, or one opened here (inside the context's execution
    /// strategy, which a retrying strategy requires; a retry starts again from what is committed). The
    /// in-memory provider has no transactions or locks and runs the work as it is.
    /// </summary>
    private async Task<T> OneSaveAtATimeAsync<T>(Guid org, Func<Task<T>> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return await work();

        if (_db.Database.CurrentTransaction is not null)
        {
            await LockOrganizationsCodesAsync(org, ct);
            return await work();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await LockOrganizationsCodesAsync(org, ct);
            var result = await work();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>The application lock one organization's tax-code saves share.</summary>
    internal static string LockResource(Guid org) => $"finance.tax_codes/{org:N}";

    private async Task LockOrganizationsCodesAsync(Guid org, CancellationToken ct)
    {
        // sp_getapplock reports through its return value rather than an error: 0 or 1 granted, below 0 not
        // (timed out, chosen as a deadlock victim, cancelled).
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await _db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource(org) },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds },
                outcome
            ],
            ct);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException(
                "Someone else is saving this organization's tax codes right now, so nothing was saved. Try again in a moment.");
    }

    private async Task SaveAsync(Guid org, TaxCode entity, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index (OrganizationId, Code) refused it: another request took the code between this
            // one's check and its save. Anything else is not ours to explain.
            var code  = entity.Code;
            var uuid  = entity.Uuid;
            var taken = await _db.TaxCodes.AsNoTracking()
                .AnyAsync(t => t.OrganizationId == org && t.Code == code && t.Uuid != uuid, ct);
            if (taken)
                throw new ConflictException($"There is already a tax code {code}. Codes are unique within an organization.");
            throw;
        }
    }
}
