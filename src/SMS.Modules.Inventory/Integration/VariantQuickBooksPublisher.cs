using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Integration;

/// <summary>A variant as it stood just before a delete, so a hard-deleted one can still be retired in QuickBooks.</summary>
internal sealed record VariantDeletionSnapshot(int ProductId, Guid VariantUuid, ItemPayload Payload);

/// <summary>
/// What the inventory service calls once its own save has committed: re-reads what changed and hands each
/// variant to the entity sync gateway (A44 D-25) as a Product request. The payloads are built by
/// <see cref="VariantQuickBooksSource"/>, the same code the QuickBooks pull-back uses, so the targets'
/// fingerprints are unchanged.
/// <para>
/// <b>Nothing here throws.</b> The product or variant is saved whatever the accounting systems make of it;
/// a failure is logged with the record's id and the QuickBooks hourly reconciliation offers it again. One
/// variant's failure does not stop the next.
/// </para>
/// </summary>
internal sealed class VariantQuickBooksPublisher
{
    private readonly InventoryDbContext                   _db;
    private readonly VariantQuickBooksSource              _source;
    private readonly IEntitySyncGateway                   _sync;
    private readonly ILogger<VariantQuickBooksPublisher>  _log;

    public VariantQuickBooksPublisher(
        InventoryDbContext db, VariantQuickBooksSource source, IEntitySyncGateway sync, ILogger<VariantQuickBooksPublisher> log)
    {
        _db     = db;
        _source = source;
        _sync   = sync;
        _log    = log;
    }

    /// <summary>A product was created or changed: every variant of it.</summary>
    public async Task PublishProductAsync(int productId)
    {
        List<ItemPayload> payloads;
        try
        {
            payloads = await _source.BuildProductAsync(productId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Product {ProductId} was saved, but its variants could not be handed to the entity sync gateway; " +
                "the hourly reconciliation will offer them again.",
                productId);
            return;
        }

        await PushAllAsync(payloads);
    }

    /// <summary>A variant was added to a product: it, and its siblings too if it is the product's second.</summary>
    public async Task PublishVariantAddedAsync(int productId, Guid variantUuid)
    {
        List<ItemPayload> payloads;
        try
        {
            payloads = await _source.BuildAddedVariantAsync(productId, variantUuid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} was added to product {ProductId}, but could not be handed to the entity sync gateway; " +
                "the hourly reconciliation will offer it again.",
                variantUuid, productId);
            return;
        }

        await PushAllAsync(payloads);
    }

    /// <summary>One variant's own fields changed.</summary>
    public async Task PublishVariantAsync(Guid variantUuid)
    {
        List<ItemPayload> payloads;
        try
        {
            payloads = await _source.BuildVariantAsync(variantUuid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} was saved, but could not be handed to the entity sync gateway; " +
                "the hourly reconciliation will offer it again.",
                variantUuid);
            return;
        }

        await PushAllAsync(payloads);
    }

    /// <summary>
    /// Taken before a variant is deleted: a variant that was never transacted is removed outright, and
    /// then nothing is left to read. Null when the variant is unknown or cannot be read.
    /// </summary>
    public async Task<VariantDeletionSnapshot?> CaptureBeforeDeleteAsync(Guid variantUuid)
    {
        try
        {
            var variant = await _db.ProductVariants.AsNoTracking()
                .Include(v => v.Product)
                .FirstOrDefaultAsync(v => v.Uuid == variantUuid);
            if (variant is null) return null;

            var active = await _db.ProductVariants.AsNoTracking()
                .CountAsync(v => v.ProductId == variant.ProductId && v.IsActive);

            return new VariantDeletionSnapshot(
                variant.ProductId, variant.Uuid,
                VariantPayloadFactory.Build(variant, VariantPayloadFactory.NamingCount(active, variant)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Variant {VariantUuid} could not be read for QuickBooks before its delete.", variantUuid);
            return null;
        }
    }

    /// <summary>
    /// After a delete. A soft-deleted variant is still there and is read back and sent as inactive
    /// (<see cref="SyncAction.Deactivate"/>). A hard-deleted one is sent from the snapshot, marked inactive,
    /// as <see cref="SyncAction.Delete"/>, so an item QuickBooks already has is retired rather than left
    /// selling something that no longer exists. Either way, a product left with one active variant sends
    /// that one again, as it goes back to the plain product name.
    /// </summary>
    public async Task PublishDeletedAsync(VariantDeletionSnapshot? snapshot, bool hardDeleted)
    {
        if (snapshot is null) return;

        var ticks = DateTime.UtcNow.Ticks;
        if (hardDeleted)
        {
            snapshot.Payload.IsActive = false;
            await PushAsync(snapshot.Payload, SyncAction.Delete, ticks, hardDeleted: true);
        }

        List<ItemPayload> payloads;
        try
        {
            // Soft: the retired variant reads back and goes as inactive. Hard: it reads back as nothing.
            payloads = await _source.BuildAfterRetirementAsync(snapshot.ProductId, snapshot.VariantUuid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} was deleted (hard: {HardDeleted}), but the entity sync gateway could not be told. " +
                "If QuickBooks already had it as an item, retire that item there by hand.",
                snapshot.Payload.ExternalId, hardDeleted);
            return;
        }

        foreach (var payload in payloads)
            await PushAsync(payload, ActionOf(payload), ticks, hardDeleted: false);
    }

    /// <summary>D-77 — inactive (product or variant soft-deleted / switched off) = Deactivate; otherwise Update.</summary>
    private static SyncAction ActionOf(ItemPayload payload) => payload.IsActive ? SyncAction.Update : SyncAction.Deactivate;

    internal static EntitySyncRequest BuildRequest(ItemPayload payload, SyncAction action, long ticks)
    {
        var entityId = Guid.TryParse(payload.ExternalId, out var id) ? id : Guid.Empty;
        return new EntitySyncRequest
        {
            EntityType     = SyncEntityType.Product,
            Action         = action,
            EntityId       = entityId,
            IdempotencyKey = QuickBooksSupport.IdempotencyKey(SyncEntityType.Product, action, entityId, ticks),
            Snapshot       = new EntitySnapshot
            {
                DisplayName  = payload.VariantName is null ? payload.Name : $"{payload.Name} - {payload.VariantName}",
                IsActive     = payload.IsActive,
                Sku          = payload.Sku,
                UnitPrice    = payload.SalesPrice,
                PurchaseCost = payload.PurchaseCost
            },
            Document = payload
        };
    }

    private async Task PushAllAsync(IReadOnlyList<ItemPayload> payloads)
    {
        var ticks = DateTime.UtcNow.Ticks;
        foreach (var payload in payloads)
            await PushAsync(payload, ActionOf(payload), ticks, hardDeleted: false);
    }

    /// <summary>REV-16: true when a real entity sync router is in the host (not the module's Null default).</summary>
    internal static bool RouterPresent(IEntitySyncGateway gateway) => gateway is not NullEntitySyncGateway;

    private async Task PushAsync(ItemPayload payload, SyncAction action, long ticks, bool hardDeleted)
    {
        // No router (host without Accounting's posting layer): straight to the QuickBooks gateway, exactly as before A44.
        if (!RouterPresent(_sync))
        {
            await _source.SendPayloadAsync(payload);
            return;
        }

        var request = BuildRequest(payload, action, ticks);
        try
        {
            var result = await _sync.PushAsync(request);
            QuickBooksSupport.LogSyncResult(_log, result, SyncEntityType.Product, payload.ExternalId, payload.Sku);
        }
        catch (Exception ex)
        {
            // The gateway promises not to throw; this is the belt to that promise's braces.
            if (hardDeleted)
                // A hard-deleted variant is gone, so no reconciliation can offer it again: say so plainly.
                _log.LogWarning(ex,
                    "Variant {VariantUuid} ({Sku}) was deleted, but the entity sync gateway could not be told. " +
                    "If QuickBooks already had it as an item, retire that item there by hand.",
                    payload.ExternalId, payload.Sku);
            else
                _log.LogWarning(ex,
                    "Variant {VariantUuid} ({Sku}) could not be handed to the entity sync gateway; the hourly reconciliation will offer it again.",
                    payload.ExternalId, payload.Sku);
        }
    }
}
