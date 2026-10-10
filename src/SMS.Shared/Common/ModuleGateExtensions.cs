using Microsoft.Extensions.Logging;

namespace SMS.Shared.Common;

/// <summary>
/// A37 D-9 (OPSB) — how per-tenant recurring jobs consult <see cref="IModuleGate"/>. The gate is resolved optionally: a
/// host or test without Tenancy registers none, and then every module counts as enabled (no behaviour change). A skipped
/// organization is logged at Debug — switching a module off is not an error.
/// </summary>
public static class ModuleGateExtensions
{
    /// <summary>True when there is no gate, else the gate's answer (a module in grace counts as off).</summary>
    public static async Task<bool> IsOnAsync(this IModuleGate? gate, Guid organizationId, string featureCode, CancellationToken ct = default) =>
        gate is null || await gate.IsEnabledAsync(organizationId, featureCode, ct);

    /// <summary>
    /// The organizations among <paramref name="organizationIds"/> whose <paramref name="featureCode"/> is off — for jobs
    /// that exclude them in their query (<c>!skipped.Contains(x.OrganizationId)</c>) so a switched-off organization's
    /// backlog can never fill a batch and starve the others. Empty without a gate.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> SkippedAmongAsync(
        this IModuleGate? gate, IEnumerable<Guid> organizationIds, string featureCode, ILogger log, string job, CancellationToken ct = default)
    {
        var skipped = new HashSet<Guid>();
        if (gate is null) return skipped;
        foreach (var org in organizationIds.Distinct())
        {
            if (await gate.IsEnabledAsync(org, featureCode, ct)) continue;
            skipped.Add(org);
            log.LogDebug("{Job}: organization {OrganizationId} skipped, {Module} is not enabled.", job, org, featureCode);
        }
        return skipped;
    }

    /// <summary>
    /// For jobs that sweep through the EF tenant filter (no tenant in a bare job = every organization in one pass). When
    /// there is no gate, no directory, or every organization has the module, <paramref name="work"/> runs once unscoped,
    /// exactly as before. Otherwise it runs once per enabled organization inside a <see cref="HangfireTenantScope"/>, so
    /// the tenant filter confines it to that organization. One failing organization is logged and does not stop the rest.
    /// </summary>
    public static async Task RunForEnabledOrganizationsAsync(
        this IModuleGate? gate, IOrganizationDirectory? directory, string featureCode, ILogger log, string job,
        Func<Task> work, CancellationToken ct = default)
    {
        if (gate is null || directory is null)
        {
            await work();
            return;
        }

        var all     = await directory.GetOrganizationIdsAsync(ct);
        var skipped = await gate.SkippedAmongAsync(all, featureCode, log, job, ct);
        if (skipped.Count == 0)
        {
            await work();
            return;
        }

        foreach (var org in all.Where(o => !skipped.Contains(o)))
        {
            HangfireTenantScope.OrganizationId = org;
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Job} failed for organization {OrganizationId}.", job, org);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
    }
}
