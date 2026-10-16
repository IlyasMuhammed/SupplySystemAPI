using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Suppliers.Integration;

/// <summary>
/// What the partner services call once their own save has committed: re-reads the partner as it now
/// stands and hands it to the entity sync gateway (A44 D-25) in every role it holds — a Customer request,
/// a Vendor request, or both. The payload is built by <see cref="PartnerQuickBooksSource.BuildRoles"/>,
/// the same code the QuickBooks pull-back uses, so the targets' fingerprints are unchanged.
/// <para>
/// <b>Never throws.</b> The partner is saved whatever the accounting systems make of it; a failure here is
/// logged with the partner's id and the QuickBooks hourly reconciliation offers the partner again. One
/// role's failure does not stop the other.
/// </para>
/// </summary>
internal sealed class PartnerQuickBooksPublisher
{
    private readonly SuppliersDbContext                  _db;
    private readonly PartnerQuickBooksSource             _source;
    private readonly IEntitySyncGateway                  _sync;
    private readonly ILogger<PartnerQuickBooksPublisher> _log;

    public PartnerQuickBooksPublisher(
        SuppliersDbContext db, PartnerQuickBooksSource source, IEntitySyncGateway sync, ILogger<PartnerQuickBooksPublisher> log)
    {
        _db     = db;
        _source = source;
        _sync   = sync;
        _log    = log;
    }

    public async Task PublishAsync(Guid partnerUuid)
    {
        IReadOnlyList<PartyPayload> payloads;
        string? code;
        try
        {
            // Deleted rows included on purpose: a delete is a deactivation the targets should hear about.
            var partner = await _db.BusinessPartners.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UUID == partnerUuid);

            if (partner is null) return;

            code     = partner.SupplierCode;
            payloads = _source.BuildRoles(partner);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Business partner {PartnerUuid} was saved, but could not be handed to the entity sync gateway; " +
                "the hourly reconciliation will offer it again.",
                partnerUuid);
            return;
        }

        var ticks = DateTime.UtcNow.Ticks;
        foreach (var payload in payloads)
            await PushAsync(partnerUuid, code, payload, ticks);
    }

    /// <summary>
    /// The request for one role. Action (D-77): an inactive payload (deleted, blacklisted, rejected,
    /// switched off) is <see cref="SyncAction.Deactivate"/>; everything else <see cref="SyncAction.Update"/> —
    /// targets upsert by the partner's uuid, so a first send needs no separate Create.
    /// </summary>
    internal static EntitySyncRequest BuildRequest(Guid partnerUuid, PartyPayload payload, long ticks)
    {
        var type   = payload is VendorPayload ? SyncEntityType.Vendor : SyncEntityType.Customer;
        var action = payload.IsActive ? SyncAction.Update : SyncAction.Deactivate;

        return new EntitySyncRequest
        {
            EntityType     = type,
            Action         = action,
            EntityId       = partnerUuid,
            IdempotencyKey = QuickBooksSupport.IdempotencyKey(type, action, partnerUuid, ticks),
            Snapshot       = new EntitySnapshot
            {
                DisplayName             = payload.DisplayName,
                Email                   = payload.Email,
                Phone                   = payload.Phone,
                IsActive                = payload.IsActive,
                CompanyName             = payload.CompanyName,
                TaxIdentificationNumber = payload.TaxId,
                CurrencyCode            = payload.CurrencyCode
            },
            Document = payload
        };
    }

    /// <summary>REV-16: true when a real entity sync router is in the host (not the module's Null default).</summary>
    internal static bool RouterPresent(IEntitySyncGateway gateway) => gateway is not NullEntitySyncGateway;

    private async Task PushAsync(Guid partnerUuid, string? code, PartyPayload payload, long ticks)
    {
        // No router (host without Accounting's posting layer): straight to the QuickBooks gateway, exactly as before A44.
        if (!RouterPresent(_sync))
        {
            await _source.SendBuiltAsync(partnerUuid, code, payload);
            return;
        }

        var request = BuildRequest(partnerUuid, payload, ticks);
        try
        {
            var result = await _sync.PushAsync(request);
            QuickBooksSupport.LogSyncResult(_log, result, request.EntityType, partnerUuid, code);
        }
        catch (Exception ex)
        {
            // The gateway promises not to throw; this is the belt to that promise's braces.
            _log.LogWarning(ex,
                "Business partner {PartnerUuid} ({PartnerCode}) could not be handed to the entity sync gateway as a {EntityType}; " +
                "the hourly reconciliation will offer it again.",
                partnerUuid, code, request.EntityType);
        }
    }
}
