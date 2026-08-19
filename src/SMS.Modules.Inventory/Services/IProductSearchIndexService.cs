namespace SMS.Modules.Inventory.Services;

// FSD §8 (PV-006) — keeps ProductSearchIndex (the denormalised table backing full-text product
// search) in sync with ProductVariant/VariantAttributeValue changes. Callers enqueue
// RebuildForVariantAsync via Hangfire rather than awaiting it inline, so a variant/attribute
// save never blocks on reindexing.
public interface IProductSearchIndexService
{
    // Upserts the ProductSearchIndex row for one variant from its current product, variant, and
    // searchable attribute value data. A no-op if the variant no longer exists.
    Task RebuildForVariantAsync(int variantId);

    // Rebuilds ProductSearchIndex for every active variant — used for initial population and
    // for the admin-triggered full rebuild.
    Task RebuildAllAsync();
}
