using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Accounting.Onboarding;

/// <summary>
/// A50 D-12 / D-51 — COA (+ ACCOUNT_ROLES): the payload is an A42 template file (snake_case, nested <c>children</c>, the
/// <c>is_default_account</c> / <c>default_account_key</c> markers), validated by the A42 rules (R1–R13, D-8 codes and root
/// digit, levels 1–4 leaf/group, 2000 accounts, 3900). Apply = <see cref="ChartOfAccountsService.ApplySetupChartAsync"/>
/// (empty chart → A42 replace, else merge; defaults only where unassigned). Natural key = account code; EntityRef = account
/// uuid; fingerprint = code + name + account type + parent code.
/// Reset: Merge = apply. Overwrite = seeded accounts that nothing references are put back to the pack's name / type /
/// description (an account no longer in the pack is deleted when A42 allows it), custom accounts kept, then merge. Full = the
/// A42 replace of the whole chart, refused when a seeded account is referenced or A42 refuses a replacement (fiscal year set,
/// opening balances posted, posted journals).
/// </summary>
internal sealed class CoaSetupSeeder : ISetupCategorySeeder
{
    private readonly AccountingDbContext _db;
    private readonly ChartOfAccountsService _chart;

    public CoaSetupSeeder(AccountingDbContext db, ChartOfAccountsService chart)
    {
        _db = db;
        _chart = chart;
    }

    public string Category => SetupCategories.Coa;
    public string? RequiresModule => ModuleCodes.GeneralLedger;

    private Guid Org => _db.TenantContext.OrganizationId is var o && o != Guid.Empty
        ? o : throw new InvalidOperationException("COA seeding needs an ambient organization.");

    private AccountingCaller Caller(SetupSeedContext c) => new(Org, c.UserId ?? 0, AccountingCaller.AllGlCodes, "Setup wizard");

    internal static string Fingerprint(string code, string name, string? accountType, string? parentCode) =>
        SetupFingerprint.Of(code, name, accountType, parentCode);

    internal static string Fingerprint(CoaRow r) => Fingerprint(r.Code, r.Name, r.Level == 4 ? r.AccountType : null, r.ParentCode);

    // ── payload ──────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ValidatePayload(JsonElement payload) => Parse(payload, out _);

    internal static List<string> Parse(JsonElement payload, out List<CoaRow> rows)
    {
        rows = [];
        if (payload.ValueKind != JsonValueKind.Object) return ["The COA payload must be an A42 chart of accounts template object."];
        var parsed = CoaFile.ParseJson(payload.GetRawText(), out _);
        var errors = parsed.Errors.Concat(parsed.Rows.Count > 0 || parsed.Errors.Count == 0 ? CoaValidator.Validate(parsed.Rows) : [])
            .Select(e => $"{e.Rule}{(e.Code is null ? "" : " " + e.Code)}: {e.Message}").ToList();
        if (errors.Count == 0) rows = parsed.Rows;
        return errors;
    }

    // ── preview / apply ──────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var rows);
        var org = Org;
        var codes = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.IsActive).Select(a => a.AccountCode).ToListAsync(ct);
        var have = codes.ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>(errors);
        if (have.Count > 0) warnings.Add($"The organization already has {have.Count} accounts: only missing accounts are added, existing ones are kept as they are.");
        return new SetupPreview { Category = Category, InPayload = rows.Count, AlreadyPresent = rows.Count(r => have.Contains(r.Code)), Warnings = warnings };
    }

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var rows);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var outcome = await _chart.ApplySetupChartAsync(Caller(context), rows, ct);
        return await ResultAsync(outcome, rows, context, [], 0, ct);
    }

    private async Task<SetupApplyResult> ResultAsync(CoaSetupOutcome outcome, List<CoaRow> rows, SetupSeedContext context,
        List<string> removedKeys, int removed, CancellationToken ct, List<string>? extraWarnings = null)
    {
        var org = Org;
        var parentCodes = await ParentCodesAsync(org, ct);
        var known = context.AlreadySeeded.Select(s => s.NaturalKey).ToHashSet(StringComparer.Ordinal);
        var packCodes = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var seeded = new List<SeededRecordRef>();
        foreach (var a in outcome.Created)
            seeded.Add(new SeededRecordRef(a.AccountCode, a.Uuid.ToString(), Fingerprint(a.AccountCode, a.AccountName, a.AccountType, parentCodes.GetValueOrDefault(a.Id))));
        foreach (var a in outcome.Existing.Where(a => packCodes.Contains(a.AccountCode) && !known.Contains(a.AccountCode)))
            seeded.Add(new SeededRecordRef(a.AccountCode, a.Uuid.ToString(), Fingerprint(a.AccountCode, a.AccountName, a.AccountType, parentCodes.GetValueOrDefault(a.Id)), Reused: true));
        var warnings = new List<string>(extraWarnings ?? []);
        warnings.AddRange(outcome.Warnings);
        if (outcome.DefaultsAssigned > 0) warnings.Add($"{outcome.DefaultsAssigned} account role(s) (accounting defaults) assigned.");
        return new SetupApplyResult
        {
            Category = Category, Created = outcome.Created.Count, Skipped = outcome.Existing.Count, Seeded = seeded, Warnings = warnings,
            Removed = removed, RemovedKeys = removedKeys,
        };
    }

    /// <summary>Account id → parent account code.</summary>
    private async Task<Dictionary<int, string?>> ParentCodesAsync(Guid org, CancellationToken ct)
    {
        var all = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == org)
            .Select(a => new { a.Id, a.ParentAccountId, a.AccountCode }).ToListAsync(ct);
        var code = all.ToDictionary(a => a.Id, a => a.AccountCode);
        return all.ToDictionary(a => a.Id, a => a.ParentAccountId is { } p ? code.GetValueOrDefault(p) : null);
    }

    // ── status / references / reset ─────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var org = Org;
        var all = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == org).ToListAsync(ct);
        var parentCodes = await ParentCodesAsync(org, ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var a = all.FirstOrDefault(x => x.Uuid.ToString() == s.EntityRef);
            if (a is null || !a.IsActive) missing.Add(s.NaturalKey);
            else if (Fingerprint(a.AccountCode, a.AccountName, a.AccountType, parentCodes.GetValueOrDefault(a.Id)) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = all.Count(a => a.IsActive), ModifiedKeys = modified, MissingKeys = missing,
        };
    }

    private async Task<List<(SeededRecordRef Ref, ChartOfAccount Account)>> SeededAccountsAsync(SetupSeedContext context, CancellationToken ct, bool track)
    {
        var all = track ? await _chart.AllAccountsAsync(Org, ct)
            : await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == Org).ToListAsync(ct);
        return context.AlreadySeeded.Where(s => !s.Reused)
            .Select(s => (s, all.FirstOrDefault(a => a.Uuid.ToString() == s.EntityRef && a.IsActive)))
            .Where(x => x.Item2 is not null).Select(x => (x.s, x.Item2!)).ToList();
    }

    /// <summary>
    /// REV50-6 — exactly what Full would replace: EVERY active account (custom ones included). When A42 refuses any chart
    /// replacement now (fiscal year set, opening balances posted, posted journals) that reason is listed first, so the
    /// orchestrator refuses Full with 409 before calling <see cref="ResetAsync"/>.
    /// </summary>
    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var org = Org;
        var all = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == org && a.IsActive).ToListAsync(ct);
        var used = await _chart.ReferencedAccountIdsAsync(org, all.Select(a => a.Id).ToList(), ct);
        var keys = all.Where(a => used.Contains(a.Id)).Select(a => a.AccountCode).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (all.Count > 0 && await _chart.ReplaceRefusalAsync(Caller(context), ct) is { } why) keys.Insert(0, why);
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        if (mode == SetupResetMode.Merge) return await ApplyAsync(payload, context, ct);
        var errors = Parse(payload, out var rows);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var caller = Caller(context);

        if (mode == SetupResetMode.Full)
        {
            // Backstop: the orchestrator refuses Full (409) on CheckReferencesAsync before calling this.
            if (await _chart.ReplaceRefusalAsync(caller, ct) is { } why)
                throw new SMS.Shared.Exceptions.ConflictException($"Full reset refused: {why}");
            var refs = await CheckReferencesAsync(context, ct);
            if (refs.ReferencedCount > 0)
                throw new SMS.Shared.Exceptions.ConflictException(
                    $"Full reset refused: {refs.ReferencedCount} account(s) are referenced ({string.Join(", ", refs.ReferencedKeys.Take(20))}). Use Overwrite instead.");
            var full = await _chart.ReplaceForSetupAsync(caller, rows, ct);
            return await ResultAsync(full, rows, SetupFingerprint.Fresh(context), context.AlreadySeeded.Select(s => s.NaturalKey).ToList(),
                context.AlreadySeeded.Count, ct);
        }

        // Overwrite.
        var byCode = rows.ToDictionary(r => r.Code, StringComparer.Ordinal);
        var seeded = await SeededAccountsAsync(context, ct, track: true);
        var used = await _chart.ReferencedAccountIdsAsync(Org, seeded.Select(x => x.Account.Id).ToList(), ct);
        var parentCodes = await ParentCodesAsync(Org, ct);
        var warnings = new List<string>();
        var removedKeys = new List<string>();
        var reverted = new List<ChartOfAccount>();
        var toDelete = new List<(SeededRecordRef Ref, ChartOfAccount Account)>();
        foreach (var (s, a) in seeded)
        {
            if (!byCode.TryGetValue(a.AccountCode, out var r)) { toDelete.Add((s, a)); continue; }
            var type = a.Level == 4 ? r.AccountType : null;
            if (a.AccountName == r.Name && a.AccountType == type && a.Description == r.Description) continue;
            if (used.Contains(a.Id)) { warnings.Add($"Account {a.AccountCode} is in use and was not reverted."); continue; }
            if (a.IsSystemAccount && a.AccountCode == SystemAccounts.OpeningBalanceEquityCode) continue;
            if (parentCodes.GetValueOrDefault(a.Id) != r.ParentCode)
                warnings.Add($"Account {a.AccountCode} has another parent than in the defaults; it is reverted in place, not moved.");
            a.AccountName = r.Name;
            a.AccountType = type;
            a.Description = r.Description;
            a.ModifiedBy = caller.UserIdOrNull;
            reverted.Add(a);
        }
        // Deepest first, so a group goes after its (seeded, removable) children.
        var reasons = await _chart.DeleteBlockedReasonsAsync(Org, toDelete.Select(x => x.Account).ToList(), ct);
        var tracked = await _chart.AllAccountsAsync(Org, ct); // same context: the instances above, with this pass's changes
        foreach (var (s, a) in toDelete.OrderByDescending(x => x.Account.Level))
        {
            var reason = used.Contains(a.Id) ? "it is in use" : reasons[a.Id];
            if (tracked.Any(c => c.ParentAccountId == a.Id && c.IsActive)) reason = "it has child accounts";
            else if (reason == "Cannot delete an account that has child accounts.") reason = null; // its children were removed just above
            if (reason is not null) { warnings.Add($"Account {a.AccountCode} is no longer in the defaults but was kept: {reason.TrimEnd('.')}."); continue; }
            a.IsActive = false;
            a.ModifiedBy = caller.UserIdOrNull;
            removedKeys.Add(s.NaturalKey);
        }
        await _chart.SaveAsync(ct);

        var merge = await _chart.ApplySetupChartAsync(caller, rows, ct);
        var result = await ResultAsync(merge, rows, context, removedKeys, removedKeys.Count, ct, warnings);
        parentCodes = await ParentCodesAsync(Org, ct);
        foreach (var a in reverted)
            result.Seeded.Add(new SeededRecordRef(a.AccountCode, a.Uuid.ToString(), Fingerprint(a.AccountCode, a.AccountName, a.AccountType, parentCodes.GetValueOrDefault(a.Id))));
        return result;
    }
}
