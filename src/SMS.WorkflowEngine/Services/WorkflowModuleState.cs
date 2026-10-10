using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Services;

/// <summary>
/// A37 §8 / D-14 — a workflow definition's module, derived from its InterfaceCode (no column), and whether that module
/// is switched on for the definition's organization. Read-only: a definition of a switched-off module stays editable,
/// it is just dormant (its documents cannot be created).
/// </summary>
internal static class WorkflowModuleState
{
    internal static async Task ApplyAsync<T>(
        IEnumerable<T> items, Func<T, string> interfaceCode, Func<T, Guid> organizationId, Action<T, string?, bool> set,
        ITenantSnapshotProvider? snapshots)
    {
        var cache = new Dictionary<Guid, TenantSnapshot?>();
        foreach (var item in items)
        {
            var module = ModuleCodeMap.ForWorkflowInterface(interfaceCode(item));
            var enabled = true;
            if (module is not null && snapshots is not null)
            {
                var orgId = organizationId(item);
                if (!cache.TryGetValue(orgId, out var snapshot))
                    cache[orgId] = snapshot = await snapshots.GetSnapshotAsync(orgId);
                enabled = snapshot is null || snapshot.EnabledFeatureCodes.Contains(module);
            }
            set(item, module, enabled);
        }
    }
}
