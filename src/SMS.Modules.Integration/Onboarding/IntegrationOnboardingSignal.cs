using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Integration.Onboarding;

/// <summary>A50 D-28: CONNECT_QB, meaning a QuickBooks connection that completed OAuth (Connected, NeedsSetup or Live).
/// Revoked / expired / never finished does not count, but a step once ticked stays ticked (BR-50-014).</summary>
internal sealed class IntegrationOnboardingSignal : IOnboardingSignal
{
    private readonly IntegrationDbContext _db;
    public IntegrationOnboardingSignal(IntegrationDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.ConnectQb];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var connected = await _db.Connections.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.OrganizationId == org
            && (c.Status == ConnectionStatus.Connected || c.Status == ConnectionStatus.NeedsSetup || c.Status == ConnectionStatus.Live), ct);
        return connected ? new HashSet<string> { OnboardingStepCodes.ConnectQb } : new HashSet<string>();
    }
}
