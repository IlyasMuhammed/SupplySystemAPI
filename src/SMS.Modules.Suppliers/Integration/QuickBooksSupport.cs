using Microsoft.Extensions.Logging;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Suppliers.Integration;

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

    /// <summary>True for every exception except a cancellation the caller asked for.</summary>
    internal static bool IsNotCancellation(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
