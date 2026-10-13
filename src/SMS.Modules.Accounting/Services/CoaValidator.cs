using System.Text.RegularExpressions;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// Spec §5.5 R2–R12 (+ D-8 codes, D-9 Opening Balance Equity, D-23 account types) over parsed rows. Every problem is
/// reported (never just the first). R1 is the parser's, R13 the service's (it needs the database).
/// </summary>
internal static partial class CoaValidator
{
    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex CodePattern();

    public static readonly IReadOnlyDictionary<string, string> RootByCode =
        RootTypes.RootCode.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static List<UploadError> Validate(IReadOnlyList<CoaRow> rows)
    {
        var errors = new List<UploadError>();

        // R10
        if (rows.Count > GlLimits.MaxAccounts)
            errors.Add(new UploadError { Rule = "R10", Message = $"Upload contains {rows.Count} accounts. Maximum allowed is {GlLimits.MaxAccounts}." });

        // R3 — the first occurrence wins; later ones are reported (and left out of the parent map).
        var byCode = new Dictionary<string, CoaRow>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.Code.Length == 0) continue;
            if (!byCode.TryAdd(r.Code, r))
                errors.Add(r.Error("R3", r.Line is { } line
                    ? $"Duplicate account code: {r.Code} at line {line}."
                    : $"Duplicate account code: {r.Code} at {r.Path}."));
        }

        foreach (var r in rows)
        {
            // R11 (D-8)
            if (r.Code.Length == 0)
                errors.Add(r.Error("R11", $"Account code is required{Where(r)}."));
            else if (!CodePattern().IsMatch(r.Code))
                errors.Add(r.Error("R11", $"Account code '{r.Code}' contains invalid characters. Use digits only."));
            else if (r.Code.Length > GlLimits.MaxCodeLength)
                errors.Add(r.Error("R11", $"Account code '{r.Code}' is longer than {GlLimits.MaxCodeLength} characters."));

            // R12
            if (r.Name.Length == 0) errors.Add(r.Error("R12", $"Account name is required at code {r.Code}."));
            else if (r.Name.Length > 200) errors.Add(r.Error("R12", $"Account name exceeds 200 characters at code {r.Code}."));
            if (r.Description is { Length: > 500 }) errors.Add(r.Error("R12", $"Description exceeds 500 characters at code {r.Code}."));

            // R4
            if (r.Level is not (>= 1 and <= 4))
            {
                errors.Add(r.Error("R4", $"Invalid level {(string.IsNullOrEmpty(r.LevelText) ? "(empty)" : r.LevelText)} for account {r.Code}. SMS supports levels 1 through 4."));
                continue;
            }
            var level = r.Level.Value;

            // R7 / R8
            if (r.IsGroup is null)
                errors.Add(r.Error(level == 4 ? "R7" : "R8", $"Account {r.Code} has is_group '{r.IsGroupText}'; use true or false."));
            else if (level == 4 && r.IsGroup == true)
                errors.Add(r.Error("R7", $"Account {r.Code} is Level 4 and must not be marked as a group."));
            else if (level < 4 && r.IsGroup == false)
                errors.Add(r.Error("R8", $"Account {r.Code} is Level {level} and must be marked as a group."));

            var rootTypeValid = RootTypes.IsValid(r.RootType);
            if (!rootTypeValid)
                errors.Add(r.Error("R9", $"Invalid root_type '{r.RootType}' for account {r.Code}. Use Asset, Liability, Equity, Revenue or Expense."));

            if (level == 1)
            {
                // R6
                if (!RootByCode.TryGetValue(r.Code, out var expectedRoot) || (rootTypeValid && expectedRoot != r.RootType))
                    errors.Add(r.Error("R6", "Level 1 account code must be 1 (Asset), 2 (Liability), 3 (Equity), 4 (Revenue), or 5 (Expense)."));
                if (r.ParentCode is not null)
                    errors.Add(r.Error("R5", $"Account {r.Code} is Level 1 and must not have a parent."));
            }
            else
            {
                // R5
                if (r.ParentCode is null || !byCode.TryGetValue(r.ParentCode, out var parent) || parent.Level != level - 1)
                    errors.Add(r.Error("R5", $"Account {r.Code} references parent {r.ParentCode ?? "(none)"} which is not found or not at level {level - 1}."));
                else if (ReferenceEquals(parent, r))
                    errors.Add(r.Error("R5", $"Account {r.Code} cannot be its own parent."));

                // R9 — root ancestor's type; D-8 — the code starts with the root's digit.
                var ancestor = RootAncestor(r, byCode);
                if (ancestor is not null && rootTypeValid && ancestor.RootType != r.RootType)
                    errors.Add(r.Error("R9", $"Account {r.Code} has root_type '{r.RootType}' but its root ancestor is '{ancestor.RootType}'."));
                if (rootTypeValid && r.Code.Length > 0 && RootTypes.RootCode.TryGetValue(r.RootType, out var digit) && !r.Code.StartsWith(digit, StringComparison.Ordinal))
                    errors.Add(r.Error("R9", $"Account {r.Code} must start with {digit}, the code of its root type ({r.RootType})."));
            }

            // D-23
            if (r.AccountType is not null)
            {
                if (level < 4)
                    errors.Add(r.Error("R8", $"Account {r.Code} is a group and cannot have an account type."));
                else if (rootTypeValid && !AccountTypes.IsValidFor(r.AccountType, r.RootType))
                    errors.Add(r.Error("R9", $"Account type {r.AccountType} is not valid for {r.RootType} accounts."));
            }

            // Default markers (JSON / templates).
            if (r.IsDefaultAccount)
            {
                if (r.DefaultAccountKey is null || !DefaultKeys.ByKey.ContainsKey(r.DefaultAccountKey))
                    errors.Add(r.Error("R1", $"Unknown default account key '{r.DefaultAccountKey}' at code {r.Code}."));
                else if (level != 4)
                    errors.Add(r.Error("R1", $"Account {r.Code} is a group and cannot be a default account."));
            }
        }

        // R2
        foreach (var root in RootTypes.All)
            if (!rows.Any(r => r.Level == 1 && r.RootType == root))
                errors.Add(new UploadError { Rule = "R2", Message = $"Missing root type: {root}. All five root types (Asset, Liability, Equity, Revenue, Expense) are required." });

        // D-9 — 3900 is Opening Balance Equity; 39 / 390 hold it when it has to be created.
        if (byCode.TryGetValue(SystemAccounts.OpeningBalanceEquityCode, out var obe) && (obe.Level != 4 || obe.RootType != RootTypes.Equity))
            errors.Add(obe.Error("R9", "Account 3900 is reserved for Opening Balance Equity and must be a Level 4 Equity account."));
        else if (obe is null)
        {
            if (byCode.TryGetValue(SystemAccounts.OpeningBalanceEquityGroupCode, out var g3) && (g3.Level != 3 || g3.RootType != RootTypes.Equity))
                errors.Add(g3.Error("R9", "Account 390 is needed for Opening Balance Equity (3900) and must be a Level 3 Equity group."));
            else if (g3 is null && byCode.TryGetValue(SystemAccounts.OpeningBalancesGroupCode, out var g2) && (g2.Level != 2 || g2.RootType != RootTypes.Equity))
                errors.Add(g2.Error("R9", "Account 39 is needed for Opening Balance Equity (3900) and must be a Level 2 Equity group."));
        }

        return errors
            .OrderBy(e => e.Line ?? int.MaxValue).ThenBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => int.TryParse(e.Rule.AsSpan(1), out var n) ? n : 99)
            .ToList();
    }

    private static string Where(CoaRow r) => r.Line is { } line ? $" at line {line}" : r.Path is { } path ? $" at {path}" : "";

    private static CoaRow? RootAncestor(CoaRow r, Dictionary<string, CoaRow> byCode)
    {
        var current = r;
        for (var guard = 0; guard < 5 && current is not null; guard++)
        {
            if (current.Level == 1) return current;
            current = current.ParentCode is not null && byCode.TryGetValue(current.ParentCode, out var p) ? p : null;
        }
        return null;
    }
}
