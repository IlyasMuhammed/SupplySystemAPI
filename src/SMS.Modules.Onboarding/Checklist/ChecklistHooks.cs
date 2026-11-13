using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.SampleData;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>D-28: the real <see cref="IOnboardingProgress"/> (replaces the Null one): a module ticks a step after its commit. Never throws.</summary>
internal sealed class OnboardingProgress : IOnboardingProgress
{
    // Resolved at call time: the checklist service needs every IOnboardingSignal, and a module that ticks a step may itself
    // be what one of those signals' dependencies needs. Lazy resolution keeps the graph free of cycles.
    private readonly IServiceProvider _services;
    private readonly ITenantContext _tenant;
    private readonly ILogger _log;

    public OnboardingProgress(IServiceProvider services, ITenantContext tenant, ILogger<OnboardingProgress>? log = null)
    {
        _services = services;
        _tenant = tenant;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task MarkCompletedAsync(string stepCode, CancellationToken ct = default)
    {
        try
        {
            if (SampleDataScope.IsLoading) return;   // D-73: a demo record never ticks a step.
            await _services.GetRequiredService<IOnboardingChecklistService>()
                .MarkCompletedAsync(_tenant.OrganizationId, stepCode, auto: true, userId: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Onboarding step {StepCode} could not be ticked for {OrganizationId}; the next read or job run will.", stepCode, _tenant.OrganizationId);
        }
    }
}

/// <summary>
/// D-28: <see cref="IEntitySyncTarget"/> "onboarding". It hears every customer / vendor / product save after commit, through
/// the same gateway QuickBooks hears them on, and ticks ADD_CUSTOMER / ADD_SUPPLIER / ADD_PRODUCT at once. It sends
/// nothing anywhere, and answers NoTargetConfigured so the gateway's combined outcome stays QuickBooks' own.
/// <para>Demo records do not count. During a load the scope flag says so (their DemoRecords rows are written after the
/// save); afterwards their ids are in DemoRecords. An inactive or deleted record does not count either.</para>
/// <para>The gateway calls targets only while MODULE_INTEGRATION is licensed (A44). Without it, the tick comes from the
/// signals on the next read or job run instead.</para>
/// </summary>
internal sealed class OnboardingEntitySyncTarget : IEntitySyncTarget
{
    public const string Code = "onboarding";

    // Resolved at call time (see OnboardingProgress): the product / partner publishers reach this target through the gateway.
    private readonly IServiceProvider _services;
    private readonly OnboardingDbContext _db;
    private readonly ILogger _log;

    public OnboardingEntitySyncTarget(IServiceProvider services, OnboardingDbContext db, ILogger<OnboardingEntitySyncTarget>? log = null)
    {
        _services = services;
        _db = db;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public string TargetCode => Code;

    public Task<bool> IsActiveAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default)
    {
        var result = new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.NoTargetConfigured };
        try
        {
            if (SampleDataScope.IsLoading || request.Action is SyncAction.Deactivate or SyncAction.Delete
                || request.Snapshot is { IsActive: false }) return result;

            var step = StepFor(request.EntityType);
            if (step is null) return result;

            var org = _db.TenantContext.OrganizationId;
            var id = request.EntityId.ToString();
            if (await _db.DemoRecords.IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == org && r.EntityRef == id, ct)) return result;

            await _services.GetRequiredService<IOnboardingChecklistService>().MarkCompletedAsync(org, step, auto: true, userId: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Onboarding target: {EntityType} {EntityId} could not tick its step.", request.EntityType, request.EntityId);
        }
        return result;
    }

    internal static string? StepFor(SyncEntityType type) => type switch
    {
        SyncEntityType.Customer => OnboardingStepCodes.AddCustomer,
        SyncEntityType.Vendor   => OnboardingStepCodes.AddSupplier,
        SyncEntityType.Product  => OnboardingStepCodes.AddProduct,
        _                       => null
    };
}

/// <summary>D-28 / task 284: a new organization gets its checklist at creation. Existing organizations get it on first read.</summary>
internal sealed class ChecklistProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly IOnboardingChecklistService _checklist;
    public ChecklistProvisioningHandler(IOnboardingChecklistService checklist) => _checklist = checklist;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        _checklist.SeedChecklistAsync(organizationId, ct);
}
