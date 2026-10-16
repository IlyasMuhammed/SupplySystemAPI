using Microsoft.Extensions.Logging;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Integration;

/// <summary>
/// The two small things every QuickBooks caller in this module does the same way: read the ids the
/// gateway hands back, and say what the gateway made of a payload.
/// </summary>
internal static class QuickBooksSupport
{
    /// <summary>How many records a backfill loads and sends at a time.</summary>
    internal const int BatchSize = 200;

    /// <summary>The ids that are GUIDs, once each. Anything else cannot be one of ours and is skipped.</summary>
    internal static List<Guid> ParseIds(IEnumerable<string>? externalIds) =>
        externalIds is null
            ? []
            : externalIds
                .Select(id => Guid.TryParse(id?.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

    /// <summary>
    /// A refusal is logged at Information, not as an error: it is the gateway's validation doing its job,
    /// and the QuickBooks dashboard is where it is shown and fixed. Everything else is routine.
    /// </summary>
    internal static void LogResult(ILogger log, GatewayResult? result, SyncKind kind, string externalId, string? label)
    {
        if (result is null)
        {
            log.LogDebug("QuickBooks gateway returned nothing for {Kind} {Label} ({ExternalId}).", kind, label, externalId);
            return;
        }

        if (result.Outcome == GatewayOutcome.Invalid)
        {
            var reasons = string.Join("; ", (result.Errors ?? []).Select(e => $"{e.Field} [{e.Code}]: {e.Message}"));
            log.LogInformation(
                "QuickBooks refused {Kind} {Label} ({ExternalId}): {Reasons}",
                kind, label, externalId, reasons);
            return;
        }

        log.LogDebug(
            "QuickBooks gateway: {Kind} {Label} ({ExternalId}) -> {Outcome} {State}.",
            kind, label, externalId, result.Outcome, result.State);
    }

    /// <summary>A44 D-13/D-77 — logged only: "{EntityType}:{Action}:{EntityId}:{ticks}".</summary>
    internal static string IdempotencyKey(SyncEntityType type, SyncAction action, Guid entityId, long ticks) =>
        $"{type}:{action}:{entityId}:{ticks}";

    /// <summary>
    /// What the entity sync gateway made of a request. A validation refusal is Information, like the
    /// gateway's own <see cref="GatewayOutcome.Invalid"/>; "no target" / "not licensed" / duplicate / held are
    /// routine; any other failure is a warning — the hourly reconciliation offers the record again.
    /// </summary>
    internal static void LogSyncResult(ILogger log, EntitySyncResult? result, SyncEntityType type, string entityId, string? label)
    {
        if (result is null)
        {
            log.LogDebug("Entity sync gateway returned nothing for {EntityType} {Label} ({EntityId}).", type, label, entityId);
            return;
        }

        if (result.Success || result.Outcome is EntitySyncOutcome.NoTargetConfigured or EntitySyncOutcome.ModuleNotLicensed
                or EntitySyncOutcome.DuplicateSkipped or EntitySyncOutcome.Held)
        {
            log.LogDebug("Entity sync gateway: {EntityType} {Label} ({EntityId}) -> {Outcome}, {Targets} target(s).",
                type, label, entityId, result.Outcome, result.TargetsQueued);
            return;
        }

        if (result.Outcome == EntitySyncOutcome.ValidationFailed)
        {
            log.LogInformation("Accounting refused {EntityType} {Label} ({EntityId}): {Reasons}",
                type, label, entityId, result.ErrorMessage);
            return;
        }

        log.LogWarning("Entity sync gateway could not take {EntityType} {Label} ({EntityId}): {Outcome} {Error}; " +
                       "the hourly reconciliation will offer it again.",
            type, label, entityId, result.Outcome, result.ErrorMessage);
    }

    /// <summary>True for every exception except a cancellation the caller asked for.</summary>
    internal static bool IsNotCancellation(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
