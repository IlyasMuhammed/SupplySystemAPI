using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Integration;

/// <summary>A variant as it stood just before a delete, so a hard-deleted one can still be retired in QuickBooks.</summary>
internal sealed record VariantDeletionSnapshot(int ProductId, Guid VariantUuid, ItemPayload Payload);

/// <summary>
/// What the inventory service calls once its own save has committed: re-reads what changed and hands it
/// to the QuickBooks gateway.
/// <para>
/// <b>Nothing here throws.</b> The product or variant is saved whatever QuickBooks makes of it; a failure
/// is logged with the record's id and the gateway's hourly reconciliation offers it again.
/// </para>
/// </summary>
internal sealed class VariantQuickBooksPublisher
{
    private readonly InventoryDbContext                   _db;
    private readonly VariantQuickBooksSource              _source;
    private readonly ILogger<VariantQuickBooksPublisher>  _log;

    public VariantQuickBooksPublisher(
        InventoryDbContext db, VariantQuickBooksSource source, ILogger<VariantQuickBooksPublisher> log)
    {
        _db     = db;
        _source = source;
        _log    = log;
    }

    /// <summary>A product was created or changed: every variant of it.</summary>
    public async Task PublishProductAsync(int productId)
    {
        try
        {
            await _source.SendProductAsync(productId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Product {ProductId} was saved, but its variants could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer them again.",
                productId);
        }
    }

    /// <summary>A variant was added to a product: it, and its siblings too if it is the product's second.</summary>
    public async Task PublishVariantAddedAsync(int productId, Guid variantUuid)
    {
        try
        {
            await _source.SendAddedVariantAsync(productId, variantUuid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} was added to product {ProductId}, but could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer it again.",
                variantUuid, productId);
        }
    }

    /// <summary>One variant's own fields changed.</summary>
    public async Task PublishVariantAsync(Guid variantUuid)
    {
        try
        {
            await _source.SendVariantAsync(variantUuid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} was saved, but could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer it again.",
                variantUuid);
        }
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
    /// After a delete. A soft-deleted variant is still there and is read back and sent as inactive. A
    /// hard-deleted one is sent from the snapshot, marked inactive, so an item QuickBooks already has is
    /// retired rather than left selling something that no longer exists. Either way, a product left with
    /// one active variant sends that one again, as it goes back to the plain product name.
    /// </summary>
    public async Task PublishDeletedAsync(VariantDeletionSnapshot? snapshot, bool hardDeleted)
    {
        if (snapshot is null) return;

        try
        {
            if (hardDeleted)
            {
                snapshot.Payload.IsActive = false;
                await _source.SendPayloadAsync(snapshot.Payload);
            }

            // Soft: the retired variant reads back and goes as inactive. Hard: it reads back as nothing.
            await _source.SendAfterRetirementAsync(snapshot.ProductId, snapshot.VariantUuid);
        }
        catch (Exception ex)
        {
            // A hard-deleted variant is gone, so no reconciliation can offer it again: say so plainly.
            _log.LogWarning(ex,
                "Variant {VariantUuid} was deleted (hard: {HardDeleted}), but the QuickBooks gateway could not be told. " +
                "If QuickBooks already had it as an item, retire that item there by hand.",
                snapshot.Payload.ExternalId, hardDeleted);
        }
    }
}
