using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// What happens to a stored payload once it has been validated (plan §2.6 steps 4–7), shared by the gateway —
/// a payload just received — and <see cref="IBlockedRecordRevalidator"/>, which re-checks Blocked records after
/// what blocked them may have been fixed. One implementation, so a re-validated record is stored and queued
/// <b>exactly</b> as a fresh upsert of the same payload would be: partner/item scope, dependencies, dry run.
/// <para>Both methods save.</para>
/// </summary>
internal interface ISyncAdmission
{
    /// <summary>Validation refused the payload: the record is Blocked with the reasons, and its pending entry too.</summary>
    Task<GatewayResult> RefuseAsync(EntityMap map, PayloadValidationResult validation, CancellationToken ct = default);

    /// <summary>
    /// A valid payload: errors cleared, then customers/vendors/items go when in scope (D-6) and documents when
    /// what they reference is in QuickBooks — otherwise they wait for it, and it is requested.
    /// </summary>
    Task<GatewayResult> AdmitAsync(
        SyncKind kind, object payload, EntityMap map, IntegrationSettings settings, DateTime now, CancellationToken ct = default);
}

internal sealed class SyncAdmission : ISyncAdmission
{
    private readonly IntegrationDbContext _db;
    private readonly ISyncOutbox          _outbox;
    private readonly ISyncDependencies    _dependencies;

    public SyncAdmission(IntegrationDbContext db, ISyncOutbox outbox, ISyncDependencies dependencies)
    {
        _db           = db;
        _outbox       = outbox;
        _dependencies = dependencies;
    }

    /// <summary>The code a refused record is filed under: the one error's own, or VALIDATION_FAILED for several.</summary>
    public static string ErrorCodeOf(PayloadValidationResult validation) =>
        validation.Errors.Count == 1 ? validation.Errors[0].Code : "VALIDATION_FAILED";

    public static string MessageOf(PayloadValidationResult validation) =>
        string.Join(" ", validation.Errors.Select(e => e.Message));

    public async Task<GatewayResult> RefuseAsync(EntityMap map, PayloadValidationResult validation, CancellationToken ct = default)
    {
        var message = MessageOf(validation);
        map.State         = SyncState.Blocked;
        map.LastErrorCode = ErrorCodeOf(validation);
        map.LastError     = SyncText.Clip(message, 2000);
        await _outbox.BlockPendingAsync(map, message, ct);
        await _db.SaveChangesAsync(ct);
        return GatewayResult.Invalid(validation.Errors);
    }

    public async Task<GatewayResult> AdmitAsync(
        SyncKind kind, object payload, EntityMap map, IntegrationSettings settings, DateTime now, CancellationToken ct = default)
    {
        // A valid payload: whatever went wrong before was about the previous one (or is fixed now).
        map.LastErrorCode = null;
        map.LastError     = null;

        if (SyncPayloads.IsMasterData(kind))
        {
            // Plan D-6: customers, vendors and items go only when something needs them, unless the
            // scope is "all active" — or they are already in QuickBooks, which must be kept current.
            var inScope = map.RemoteId is not null
                       || map.RequestedAt is not null
                       || (settings.PartnerScope == PartnerScope.AllActive && IsActive(payload));

            if (!inScope)
            {
                // Held, validated, ready for matching — just not sent. Anything still queued from when it
                // was in scope (e.g. before it was deactivated) is dropped.
                await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Out of the partner/item scope; not sent.", now, ct);
                if (!await _outbox.HasRunningEntryAsync(map, ct)) map.State = SyncState.NotSynced;
                await _db.SaveChangesAsync(ct);
                return GatewayResult.Accepted(SyncState.NotSynced);
            }

            await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
            await _db.SaveChangesAsync(ct);
            return GatewayResult.Accepted(SyncState.Pending);
        }

        // Documents: everything they reference must be in QuickBooks first.
        var dependencies = SyncPayloads.DependenciesOf(kind, payload);
        var missing      = await _dependencies.FindMissingAsync(map, dependencies, settings.Mode == SyncMode.DryRun, ct);

        if (missing.Count > 0)
        {
            await _dependencies.RequestAsync(map, missing, now, ct);
            await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, missing, ct);
            await _db.SaveChangesAsync(ct);
            return GatewayResult.Waiting(missing);
        }

        await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
        await _db.SaveChangesAsync(ct);
        return GatewayResult.Accepted(SyncState.Pending);
    }

    private static bool IsActive(object payload) => payload switch
    {
        PartyPayload p => p.IsActive,
        ItemPayload i  => i.IsActive,
        _              => true
    };
}
