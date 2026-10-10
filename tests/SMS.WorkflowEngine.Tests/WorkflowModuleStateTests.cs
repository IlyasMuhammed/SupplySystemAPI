using FluentAssertions;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>A37 §8 / D-14 — workflow list items carry the module derived from InterfaceCode and whether it is on.</summary>
public class WorkflowModuleStateTests
{
    [Fact]
    public async Task Items_get_module_code_and_enabled_from_their_organizations_snapshot()
    {
        var orgId = Guid.NewGuid();
        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync(new TenantSnapshot(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ModuleCodes.Demand }));
        var items = new List<InterfaceSummaryDto>
        {
            new() { InterfaceCode = "PO" }, new() { InterfaceCode = "GRN" }, new() { InterfaceCode = "CUSTOM_THING" }
        };

        await WorkflowModuleState.ApplyAsync(items, i => i.InterfaceCode, _ => orgId,
            (i, m, e) => { i.ModuleCode = m; i.ModuleEnabled = e; }, snapshots.Object);

        items[0].Should().Match<InterfaceSummaryDto>(i => i.ModuleCode == ModuleCodes.Demand && i.ModuleEnabled);
        items[1].Should().Match<InterfaceSummaryDto>(i => i.ModuleCode == ModuleCodes.Warehouse && !i.ModuleEnabled);
        items[2].Should().Match<InterfaceSummaryDto>(i => i.ModuleCode == null && i.ModuleEnabled);
        snapshots.Verify(s => s.GetSnapshotAsync(orgId), Times.Once);
    }
}
