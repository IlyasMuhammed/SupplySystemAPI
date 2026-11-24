using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// A51 P7 — what applying a plan to ONE existing organization does, in one place for "Reset to Plan Defaults" (mode full) and
/// "Apply to organizations" (addOnly or full). <see cref="Plan"/> decides (pure, nothing written); <see cref="Apply"/> writes the
/// decision through <see cref="OrgFeatureWriter"/>, so every change gets the same history row the per-organization reset always wrote.
/// <list type="bullet">
/// <item>Adding: a feature the plan includes and the organization does not hold. Skipped when it is ComingSoon / not visible (unless
/// the organization is grandfathered for it), not available, or its module / dependency would not be on. addOnly never re-licenses
/// what the organization's own admin switched off.</item>
/// <item>Removing (full only): a feature the plan does not include. Never core features, grandfathered rows, hand-licensed features
/// (HRM, QuickBooks own app) or anything another remaining feature needs.</item>
/// </list>
/// </summary>
internal static class PlanApplier
{
    public const string ModeAddOnly = "addOnly";
    public const string ModeFull = "full";

    public static string? NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) || mode.Trim().Equals(ModeAddOnly, StringComparison.OrdinalIgnoreCase) ? ModeAddOnly
        : mode.Trim().Equals(ModeFull, StringComparison.OrdinalIgnoreCase) ? ModeFull : null;

    internal sealed record Decision(PlanApplyOrgResult Result, List<string> AddCodes, List<string> RemoveCodes);

    public static Decision Plan(IReadOnlyList<OrgFeatureState> states, ISet<string> included, bool full, string planCode)
    {
        var result = new PlanApplyOrgResult { Plan = planCode };
        var byCode = states.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var adds = new Dictionary<string, OrgFeatureState>(StringComparer.OrdinalIgnoreCase);
        var removes = new Dictionary<string, OrgFeatureState>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<PlanApplyFeatureItem>();

        void Skip(OrgFeatureState s, string reason, string detail) =>
            skipped.Add(new PlanApplyFeatureItem { FeatureCode = s.Code, FeatureName = s.Name, Reason = reason, Detail = detail });

        foreach (var s in states)
        {
            var target = included.Contains(s.Code);
            var isOn = s.Row is { IsLicensed: true, IsEnabled: true };
            if (target)
            {
                if (isOn) continue;
                if (!full && s.Row?.IsLicensed == true) { Skip(s, "SwitchedOffByOrganization", "Licensed already; its organization switched it off."); continue; }
                if (s.IsStatusRestricted)
                {
                    var comingSoon = string.Equals(s.Definition.Status, FeatureStatuses.ComingSoon, StringComparison.OrdinalIgnoreCase);
                    Skip(s, comingSoon ? "ComingSoon" : "NotVisible", comingSoon ? "Not licensed because it is Coming Soon." : "Not licensed because it is not visible yet.");
                    continue;
                }
                if (!s.Definition.IsAvailable) { Skip(s, "NotAvailable", "Not available yet."); continue; }
                adds[s.Code] = s;
            }
            else if (full)
            {
                if (s.Row is not { } row || (!row.IsLicensed && !row.IsEnabled)) continue;
                if (s.Definition.IsCore) { Skip(s, "Core", "Core features are never removed."); continue; }
                if (row.IsGrandfathered) { Skip(s, "Grandfathered", "The organization is grandfathered for it."); continue; }
                if (TenancyFeatureCatalog.LicensedByHandOnly.Contains(s.Code)) { Skip(s, "LicensedByHandOnly", "Licensed by hand; a plan never removes it."); continue; }
                removes[s.Code] = s;
            }
        }

        // The organization after the change: what is on, minus the removals, plus the additions - iterate until the rules hold.
        bool On(string code) =>
            adds.ContainsKey(code) || (byCode.TryGetValue(code, out var st) && st.Row is { IsLicensed: true, IsEnabled: true } && !removes.ContainsKey(code));

        bool changed;
        do
        {
            changed = false;
            foreach (var (code, s) in adds.ToList())
            {
                string? needs = null;
                if (s.Definition.ParentModuleCode is { } parent && !On(parent)) needs = parent;
                foreach (var (dependent, requiredBy) in TenancyFeatureCatalog.Dependencies)
                    if (dependent.Equals(code, StringComparison.OrdinalIgnoreCase) && !On(requiredBy)) needs = requiredBy;
                if (needs is null) continue;
                adds.Remove(code);
                var needed = byCode.GetValueOrDefault(needs);
                var neededComingSoon = needed is not null && needed.IsStatusRestricted
                    && string.Equals(needed.Definition.Status, FeatureStatuses.ComingSoon, StringComparison.OrdinalIgnoreCase);
                Skip(s, neededComingSoon ? "ComingSoon" : "NeedsModule", $"Needs {needed?.Name ?? needs}, which is not licensed.");
                changed = true;
            }
            foreach (var (code, s) in removes.ToList())
            {
                var users = byCode.Values.FirstOrDefault(o => On(o.Code) && !removes.ContainsKey(o.Code)
                    && (string.Equals(o.Definition.ParentModuleCode, code, StringComparison.OrdinalIgnoreCase)
                        || TenancyFeatureCatalog.Dependencies.Any(d => d.Dependent.Equals(o.Code, StringComparison.OrdinalIgnoreCase)
                                                                      && d.RequiredBy.Equals(code, StringComparison.OrdinalIgnoreCase))));
                if (users is null) continue;
                removes.Remove(code);
                Skip(s, "InUse", $"{users.Name} still needs it.");
                changed = true;
            }
        } while (changed);

        result.Added = adds.Values.Select(s => new PlanApplyFeatureItem { FeatureCode = s.Code, FeatureName = s.Name }).ToList();
        result.Removed = removes.Values.Select(s => new PlanApplyFeatureItem { FeatureCode = s.Code, FeatureName = s.Name }).ToList();
        result.Skipped = skipped;
        return new Decision(result, adds.Keys.ToList(), removes.Keys.ToList());
    }

    /// <summary>Writes the decision (tracked on the writer's context; the caller saves).</summary>
    public static void Apply(OrgFeatureWriter writer, Decision decision, int by, string note)
    {
        foreach (var code in decision.RemoveCodes) writer.SetLicence(code, false, by, note);
        foreach (var code in decision.AddCodes)
        {
            writer.SetLicence(code, true, by, note);
            if (code.Equals(ModuleCodes.BomManagement, StringComparison.OrdinalIgnoreCase)) writer.Row(code).IsSystemManaged = true;
        }
    }
}
