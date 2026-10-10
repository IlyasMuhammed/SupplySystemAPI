using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A37 D-9 (OPSB) — Finance's recurring jobs skip organizations without MODULE_FINANCE (grace counts as off).</summary>
public class ModuleRegistryFinanceJobTests
{
    private sealed class OffGate(Guid off) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(!(organizationId == off && featureCode == ModuleCodes.Finance));
    }

    [Fact]
    public async Task Exchange_revaluation_skips_an_organization_without_finance()
    {
        Guid on = Guid.NewGuid(), off = Guid.NewGuid();
        var directory = new Mock<IOrganizationDirectory>();
        directory.Setup(d => d.GetOrganizationIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([off, on]);
        var revalued = new List<Guid>();
        var revaluation = new Mock<IExchangeRevaluationService>();
        revaluation.Setup(r => r.RunAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>()))
                   .Callback((Guid org, DateOnly _, int? _, CancellationToken _) => revalued.Add(org))
                   .ReturnsAsync(new ExchangeRevaluationResult(DateOnly.MinValue, 0, 0, 0, 0, [], []));

        await new ExchangeRevaluationJob(revaluation.Object, NullLogger<ExchangeRevaluationJob>.Instance, directory.Object,
            gate: new OffGate(off)).RunAsync();

        revalued.Should().Equal(on);
    }
}
