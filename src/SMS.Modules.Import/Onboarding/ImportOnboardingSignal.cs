using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Import.Onboarding;

/// <summary>A50 D-28: IMPORT_DATA, meaning an import session finished (COMPLETED or COMPLETED_WITH_ERRORS) having created or
/// updated at least one record. A failed, cancelled or all-skipped import does not count.</summary>
internal sealed class ImportOnboardingSignal : IOnboardingSignal
{
    private readonly ImportDbContext _db;
    public ImportOnboardingSignal(ImportDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.ImportData];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var done = await _db.Sessions.IgnoreQueryFilters().AsNoTracking().AnyAsync(s => s.OrganizationId == org
            && (s.Status == ImportSessionStatus.Completed || s.Status == ImportSessionStatus.CompletedWithErrors)
            && s.ImportedRows + s.UpdatedRows > 0, ct);
        return done ? new HashSet<string> { OnboardingStepCodes.ImportData } : new HashSet<string>();
    }
}
