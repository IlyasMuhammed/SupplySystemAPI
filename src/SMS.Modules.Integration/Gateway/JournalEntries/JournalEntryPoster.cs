using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway.JournalEntries;

/// <summary>
/// A49 G-1 / G-12: the target's journal path. Builds, then hands every batch to the same gateway upsert as any other document, so holds,
/// validation, the outbox, the ledger's never-twice rule and the dead letter are the existing ones. A reversal is a new entry per entry
/// sent; what never reached QuickBooks is closed and nothing is sent. The original stays in QuickBooks (BR-G12-002).
/// </summary>
internal interface IJournalEntryPoster
{
    Task<GatewayResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default);

    /// <summary>Null when nothing of this document was ever stored here.</summary>
    Task<GatewayResult?> ReverseAsync(VoidRequest request, CancellationToken ct = default);
}

internal sealed class JournalEntryPoster : IJournalEntryPoster
{
    private readonly IQuickBooksJournalEntryBuilder _builder;
    private readonly IQuickBooksGateway _gateway;
    private readonly IConnectionAccessor _connections;
    private readonly IntegrationDbContext _db;

    public JournalEntryPoster(IQuickBooksJournalEntryBuilder builder, IQuickBooksGateway gateway, IConnectionAccessor connections, IntegrationDbContext db)
    {
        _builder = builder;
        _gateway = gateway;
        _connections = connections;
        _db = db;
    }

    public async Task<GatewayResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable()) return GatewayResult.NotConnected();

        var payloads = await _builder.BuildAsync(connection.OrganizationId, transaction, ct);
        return await SendAsync(payloads, JournalEntryKeys.Root(transaction.SourceDocumentId, transaction.DocumentType), ct);
    }

    public async Task<GatewayResult?> ReverseAsync(VoidRequest request, CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return GatewayResult.NotConnected();

        // QA49C-1: only this document type's entries; a run's reversal never picks up its payment's, nor the reverse.
        var root = JournalEntryKeys.Root(request.SourceDocumentId, request.DocumentType);
        var prefix = root + ":batch";
        var maps = await _db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id && m.Kind == SyncKind.JournalEntry
                        && (m.ExternalId == root || m.ExternalId.StartsWith(prefix)))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        if (maps.Count == 0) return null;

        var reversals = new List<JournalEntryPayload>();
        foreach (var map in maps.Where(m => m.State != SyncState.Voided))
        {
            if (map.RemoteId is not null && map.PayloadJson is not null)
            {
                var original = (JournalEntryPayload)SyncPayloads.Deserialize(SyncKind.JournalEntry, map.PayloadJson);
                reversals.Add(_builder.BuildReversal(original, request.VoidDate));
            }
            else
            {
                // Not in QuickBooks (held, queued): closed here. A call under way ends flagged for the accountant, as for any document.
                var reason = $"{request.SourceDocumentNumber} was reversed in SCM while this journal entry was on its way. Check QuickBooks for it, then mark this resolved.";
                await _gateway.FlagForAccountantAsync(SyncKind.JournalEntry, map.ExternalId, reason, ct);
            }
        }

        return reversals.Count == 0
            ? GatewayResult.Accepted(SyncState.Voided)
            : await SendAsync(reversals, root + ":void", ct);
    }

    private async Task<GatewayResult> SendAsync(IReadOnlyList<JournalEntryPayload> payloads, string root, CancellationToken ct)
    {
        GatewayResult? refused = null;
        GatewayResult last = GatewayResult.Disabled();
        foreach (var payload in payloads)
        {
            last = await _gateway.UpsertJournalEntryAsync(payload, ct);
            if (last.Outcome == GatewayOutcome.Invalid) refused ??= last;
        }
        if (refused is not null) return refused;
        if (last.Outcome is GatewayOutcome.NotConnected or GatewayOutcome.Disabled || payloads.Count == 1) return last;

        // A split entry stands where its furthest-behind batch stands.
        var status = (await _gateway.GetStatusAsync(SyncKind.JournalEntry, [root], ct)).FirstOrDefault();
        return GatewayResult.Accepted(status?.State ?? last.State ?? SyncState.Pending);
    }
}
