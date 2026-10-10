using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A37 D-9 (OPSB) — the scorecard job skips organizations without MODULE_SUPPLIERS: when one is off, it recalculates once
/// per enabled organization under its tenant scope; when all are on (or there is no gate) it runs once, as before.
/// </summary>
public class ModuleRegistrySuppliersJobTests
{
    private sealed class OffGate(params Guid[] off) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(!(off.Contains(organizationId) && featureCode == ModuleCodes.Suppliers));
    }

    private sealed class RecordingService : IScorecardRecalculationService
    {
        public readonly List<Guid?> Scopes = [];

        public Task<int> RecalculateAllAsync(DateTime periodStart, DateTime periodEnd, int triggeredBy)
        {
            Scopes.Add(HangfireTenantScope.OrganizationId);
            return Task.FromResult(0);
        }

        public Task<bool> RecalculateSupplierAsync(Guid supplierId, DateTime periodStart, DateTime periodEnd, int triggeredBy) =>
            Task.FromResult(false);
    }

    private sealed class Directory(Guid[] orgs) : IOrganizationDirectory
    {
        public Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(orgs);
    }

    private static (ScorecardRecalculationJob Job, List<Guid?> Scopes) Job(IModuleGate? gate, params Guid[] orgs)
    {
        var svc = new RecordingService();
        return (new ScorecardRecalculationJob(svc, new ConfigurationBuilder().Build(), gate, new Directory(orgs)), svc.Scopes);
    }

    [Fact]
    public async Task An_organization_without_suppliers_is_skipped()
    {
        Guid a = Guid.NewGuid(), off = Guid.NewGuid(), b = Guid.NewGuid();
        var (job, scopes) = Job(new OffGate(off), a, off, b);

        await job.RunAsync();

        scopes.Should().Equal(a, b);
        HangfireTenantScope.OrganizationId.Should().BeNull();
    }

    [Fact]
    public async Task With_every_organization_on_or_no_gate_it_runs_once_across_all()
    {
        var (allOn, s1) = Job(new OffGate(), Guid.NewGuid(), Guid.NewGuid());
        var (noGate, s2) = Job(null, Guid.NewGuid());

        await allOn.RunAsync();
        await noGate.RunAsync();

        s1.Should().ContainSingle().Which.Should().BeNull();
        s2.Should().ContainSingle().Which.Should().BeNull();
    }
}
