using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 C11/C12 — master data (customers, vendors, products) to every active <see cref="IEntitySyncTarget"/> (QuickBooks
/// today). Licence: MODULE_INTEGRATION (no gate = licensed). Dedupe is the targets' business (their payload fingerprints,
/// D-13): the request's IdempotencyKey is only logged. Never throws; one target's failure does not stop another. An exception
/// is Outcome Failed (D-48); ValidationFailed is kept for refused requests.
/// </summary>
internal sealed class EntitySyncGateway : IEntitySyncGateway
{
    private readonly ITenantContext _tenant;
    private readonly IReadOnlyList<IEntitySyncTarget> _targets;
    private readonly IModuleGate? _gate;
    private readonly ILogger _log;

    public EntitySyncGateway(ITenantContext tenant, IEnumerable<IEntitySyncTarget> targets, IModuleGate? gate = null,
        ILogger<EntitySyncGateway>? log = null)
    {
        _tenant = tenant;
        _targets = targets.GroupBy(t => t.TargetCode, StringComparer.Ordinal).Select(g => g.First()).ToList();
        _gate = gate;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default)
    {
        try
        {
            return await PushCoreAsync(request, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Entity sync of {EntityType} {EntityId} failed unexpectedly.", request?.EntityType, request?.EntityId);
            return new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.Failed, ErrorMessage = ex.Message };
        }
    }

    public async Task<BatchEntitySyncResult> PushBatchAsync(IReadOnlyList<EntitySyncRequest> requests, CancellationToken ct = default)
    {
        var list = requests ?? [];
        int queued = 0, duplicates = 0;
        var errors = new List<string>();
        foreach (var request in list)
        {
            var result = await PushAsync(request, ct);
            if (result.Outcome == EntitySyncOutcome.DuplicateSkipped) duplicates++;
            else if (result.Success && result.TargetsQueued > 0) queued++;
            if (!result.Success) errors.Add($"{request?.EntityType} {request?.EntityId}: {result.ErrorMessage ?? result.Outcome.ToString()}");
        }
        return new BatchEntitySyncResult { TotalRequests = list.Count, Queued = queued, DuplicatesSkipped = duplicates, Errors = errors };
    }

    private async Task<EntitySyncResult> PushCoreAsync(EntitySyncRequest request, CancellationToken ct)
    {
        if (request is null) return Invalid("The request is required.");
        if (request.EntityId == Guid.Empty) return Invalid("The entity id is required.");
        if (request.Document is null) return Invalid("The entity document is required.");
        var org = _tenant.OrganizationId;
        if (org == Guid.Empty) return Invalid("There is no organization to sync for.");
        if (!await _gate.IsOnAsync(org, ModuleCodes.Integration, ct))
            return new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.ModuleNotLicensed,
                ErrorMessage = "No accounting integration is licensed for this organization." };

        _log.LogDebug("Entity sync {Action} {EntityType} {EntityId} (key {Key}, by {By}).",
            request.Action, request.EntityType, request.EntityId, request.IdempotencyKey, request.InitiatedBy);

        var results = new List<(string Target, EntitySyncResult Result)>();
        foreach (var target in _targets)
        {
            bool active;
            try { active = await target.IsActiveAsync(ct); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Entity sync target {Target} could not say whether it is active; treated as inactive.", target.TargetCode);
                active = false;
            }
            if (!active) continue;
            EntitySyncResult r;
            try
            {
                r = await target.PushAsync(request, ct)
                    ?? new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.Failed, ErrorMessage = "The target returned no result." };
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Entity sync of {EntityType} {EntityId} failed at {Target}.", request.EntityType, request.EntityId, target.TargetCode);
                r = new EntitySyncResult { Success = false, Outcome = EntitySyncOutcome.Failed, ErrorMessage = ex.GetBaseException().Message };
            }
            results.Add((target.TargetCode, r));
        }

        if (results.Count == 0)
            return new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.NoTargetConfigured };
        if (results.Count == 1) return results[0].Result;

        var failures = results.Where(x => !x.Result.Success).ToList();
        var queued = results.Sum(x => x.Result.TargetsQueued);
        return new EntitySyncResult
        {
            Success = failures.Count == 0,
            Outcome = failures.Count > 0 ? failures[0].Result.Outcome
                : results.All(x => x.Result.Outcome == EntitySyncOutcome.DuplicateSkipped) ? EntitySyncOutcome.DuplicateSkipped
                : results.Any(x => x.Result.Outcome == EntitySyncOutcome.Queued) ? EntitySyncOutcome.Queued
                : results[0].Result.Outcome,
            TargetsQueued = queued,
            ErrorMessage = failures.Count == 0 ? null : string.Join(" ", failures.Select(f => $"{f.Target}: {f.Result.ErrorMessage}")),
        };
    }

    private static EntitySyncResult Invalid(string message) =>
        new() { Success = false, Outcome = EntitySyncOutcome.ValidationFailed, ErrorMessage = message };
}
