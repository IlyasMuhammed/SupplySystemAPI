using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Integration;

/// <summary>
/// Product variants, for the QuickBooks gateway, as items. Called <b>by</b> the gateway — from background
/// jobs, with the tenant set through <c>HangfireTenantScope</c> — when an invoice or bill waits on an item
/// it has never been sent, for "Push now", and for the hourly reconciliation (A44 D-3: this pull-back stays
/// on <see cref="IQuickBooksGateway"/>). The inventory service's edits are built by the same class
/// (<see cref="BuildProductAsync"/>, <see cref="BuildVariantAsync"/>, …) and sent by the publisher through the
/// entity sync gateway, so the two paths can never build an item differently.
/// </summary>
internal sealed class VariantQuickBooksSource : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.Item];

    private readonly InventoryDbContext                _db;
    private readonly IQuickBooksGateway                _gateway;
    private readonly ILogger<VariantQuickBooksSource>  _log;

    public VariantQuickBooksSource(InventoryDbContext db, IQuickBooksGateway gateway, ILogger<VariantQuickBooksSource> log)
    {
        _db      = db;
        _gateway = gateway;
        _log     = log;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    /// <summary>
    /// Sends the variants asked for. A variant has no deleted flag: a soft delete makes it inactive, and it
    /// is kept precisely because documents still name it — so an inactive variant is still sent (as
    /// inactive) rather than skipped, or the invoice waiting on it would wait for good. Unknown and
    /// malformed ids are skipped.
    /// </summary>
    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;

        var variants = await OwnVariants().AsNoTracking()
            .Include(v => v.Product)
            .Where(v => ids.Contains(v.Uuid))
            .ToListAsync(ct);

        await SendAllAsync(variants, ct);
    }

    /// <summary>
    /// Every active variant of an active product, changed since <paramref name="changedSince"/>. A variant
    /// records only when it was created and a product only when it was created or last updated, so an edit
    /// to a variant's own fields is invisible here — the edit's own push is what carries it. Loaded
    /// <see cref="QuickBooksSupport.BatchSize"/> at a time by id.
    /// </summary>
    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnVariants().AsNoTracking()
            .Where(v => v.IsActive && v.Product.IsActive);

        if (changedSince is { } since)
            query = query.Where(v => v.CreatedDate >= since
                                  || v.Product.CreatedDate >= since
                                  || v.Product.UpdatedDate >= since);

        var sent   = 0;
        var lastId = 0;

        while (true)
        {
            var batch = await query.Where(v => v.Id > lastId)
                .OrderBy(v => v.Id)
                .Take(QuickBooksSupport.BatchSize)
                .Include(v => v.Product)
                .ToListAsync(ct);

            sent += await SendAllAsync(batch, ct);

            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }

        return sent;
    }

    // ── After an edit in this module (A44 D-25): the payloads only. The publisher sends them through the
    //    entity sync gateway; built by the same code as the pull-back, so the fingerprints are the same. ──

    /// <summary>
    /// Every variant of a product — after the product itself changed: its name, description, kind and flags
    /// are on every one of its items.
    /// </summary>
    internal async Task<List<ItemPayload>> BuildProductAsync(int productId, CancellationToken ct = default)
    {
        var variants = await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.ProductId == productId)
            .ToListAsync(ct);

        return await BuildAllAsync(variants, ct);
    }

    /// <summary>
    /// A variant was added. Its siblings only change when it is the product's second active variant — the
    /// first one then starts carrying its variant name — so only then does the whole product go; otherwise
    /// just the new variant, however many siblings it has.
    /// </summary>
    internal async Task<List<ItemPayload>> BuildAddedVariantAsync(int productId, Guid variantUuid, CancellationToken ct = default)
    {
        var active = await ActiveVariantCountAsync(productId, ct);
        return active == 2
            ? await BuildProductAsync(productId, ct)
            : await BuildVariantAsync(variantUuid, ct);
    }

    /// <summary>
    /// A variant was retired (soft-deleted, or removed — in which case <paramref name="retiredUuid"/> no longer
    /// reads back and is skipped here). It, and the product's one remaining active variant when only one is
    /// left, because that one goes back to the plain product name.
    /// </summary>
    internal async Task<List<ItemPayload>> BuildAfterRetirementAsync(int productId, Guid retiredUuid, CancellationToken ct = default)
    {
        var payloads = await BuildVariantAsync(retiredUuid, ct);

        var remaining = await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.ProductId == productId && v.IsActive)
            .OrderBy(v => v.Id)
            .Take(2)
            .ToListAsync(ct);

        if (remaining.Count == 1)
            payloads.AddRange(await BuildAllAsync(remaining, ct));

        return payloads;
    }

    private Task<int> ActiveVariantCountAsync(int productId, CancellationToken ct) =>
        _db.ProductVariants.AsNoTracking().CountAsync(v => v.ProductId == productId && v.IsActive, ct);

    /// <summary>One variant, after an edit to its own fields. Empty when it does not read back.</summary>
    internal async Task<List<ItemPayload>> BuildVariantAsync(Guid variantUuid, CancellationToken ct = default)
    {
        var variant = await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.Uuid == variantUuid, ct);

        return variant is null ? [] : await BuildAllAsync([variant], ct);
    }

    /// <summary>Builds each variant, counting its product's active variants in one query for the lot.</summary>
    internal async Task<List<ItemPayload>> BuildAllAsync(IReadOnlyList<ProductVariant> variants, CancellationToken ct)
    {
        if (variants.Count == 0) return [];

        var productIds = variants.Select(v => v.ProductId).Distinct().ToList();
        var active = await _db.ProductVariants.AsNoTracking()
            .Where(v => productIds.Contains(v.ProductId) && v.IsActive)
            .GroupBy(v => v.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProductId, x => x.Count, ct);

        return variants
            .Select(v => VariantPayloadFactory.Build(v, VariantPayloadFactory.NamingCount(active.GetValueOrDefault(v.ProductId), v)))
            .ToList();
    }

    /// <summary>The pull-back path: builds and sends each variant to the QuickBooks gateway (D-3).</summary>
    private async Task<int> SendAllAsync(IReadOnlyList<ProductVariant> variants, CancellationToken ct)
    {
        var sent = 0;
        foreach (var payload in await BuildAllAsync(variants, ct))
            if (await SendAsync(payload, ct)) sent++;

        return sent;
    }

    /// <summary>
    /// REV-16 / D-75b fallback: a payload built here, straight to the QuickBooks gateway — the pre-A44 edit path, used
    /// only when the host has no entity sync router. Never throws.
    /// </summary>
    internal Task<bool> SendPayloadAsync(ItemPayload payload, CancellationToken ct = default) => SendAsync(payload, ct);

    /// <summary>One item. A gateway failure is logged and reported as not sent, never thrown.</summary>
    private async Task<bool> SendAsync(ItemPayload payload, CancellationToken ct)
    {
        try
        {
            var result = await _gateway.UpsertItemAsync(payload, ct);
            QuickBooksSupport.LogResult(_log, result, SyncKind.Item, payload.ExternalId, payload.Sku);
            return true;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Variant {VariantUuid} ({Sku}) could not be handed to the QuickBooks gateway; the hourly reconciliation will offer it again.",
                payload.ExternalId, payload.Sku);
            return false;
        }
    }

    /// <summary>
    /// The current organization's variants, limited <b>explicitly</b>: "Sync all" / "Push now" call this
    /// source inside a super admin's request, which bypasses the tenant query filter — without this, every
    /// organization's items would be handed to the caller's QuickBooks company (security audit Q1).
    /// </summary>
    private IQueryable<ProductVariant> OwnVariants()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.ProductVariants.Where(v => v.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind != SyncKind.Item)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Product variants are sent as items only.");
    }
}
