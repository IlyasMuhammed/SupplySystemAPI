using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A46 C4 — what a purchase order line is going through, so the pricing middleware knows whether to (re)price it. Built by
/// <see cref="IPurchaseOrderLineFactory"/> (new lines and the lines a PO edit rebuilds) and by the repository for lines changed in
/// place (a consolidated quantity bump, the parent of a split).
/// </summary>
internal sealed class LineChangeContext
{
    public bool IsNew { get; set; }
    public bool ProductChanged { get; set; }
    public bool QuantityChanged { get; set; }
    public bool DeliveryDateChanged { get; set; }
    public decimal? PreviousQuantity { get; set; }
    public DateTime? PreviousDeliveryDate { get; set; }

    /// <summary>
    /// E-8 — what an API client said about the line's price (A45 D-10). Set: the line is a fresh entity whose price source must be
    /// established and verified from it (a new line, or one a PO edit rebuilds). Null: the line already has a trusted price source
    /// (an in-place change) and only the change rules apply.
    /// </summary>
    public PriceClaim? Claim { get; set; }

    /// <summary>The price to use when nothing resolves and the line has none of its own: a requisition's estimate, the variant's catalogue price.</summary>
    public decimal? FallbackPrice { get; set; }
}

/// <summary>An API client's unverified statement about where a line's price came from (A45 D-10). 0 Manual, 1 Pricelist, 2 Override, 3 RFQ.</summary>
internal sealed class PriceClaim
{
    public int? Source { get; set; }
    public decimal? SystemResolvedPrice { get; set; }
    public Guid? PricelistUuid { get; set; }
    public string? OverrideReason { get; set; }
}

/// <summary>What the middleware did to a line: whether its price now stands on a pricelist row, and anything the buyer should be told.</summary>
internal sealed class LinePricingOutcome
{
    /// <summary>True when the PO is not a draft and nothing was changed.</summary>
    public bool Skipped { get; init; }
    /// <summary>True when the pricelist (or a contractual source) priced the line.</summary>
    public bool PriceWasResolved { get; set; }
    public string? Warning { get; set; }
    public PriceResolutionResult? Resolution { get; set; }
}

/// <summary>
/// A46 C4 / BR-26..28 — the one place a purchase order line's price source is decided on the server. Called by
/// <see cref="IPurchaseOrderLineFactory"/> for every line created, and by the repository for every update path, before the line is
/// saved. A45's save-side verification of the client's claim (D-10) lives here too (E-8).
/// </summary>
internal interface IPurchaseOrderLinePricingMiddleware
{
    /// <summary>Resolves and/or verifies the price of <paramref name="line"/> as <paramref name="context"/> says it changed. Mutates the line in place.</summary>
    Task<LinePricingOutcome> ApplyPricingAsync(PurchaseOrderLine line, PurchaseOrder po, LineChangeContext context, CancellationToken ct = default);
}

internal sealed class PurchaseOrderLinePricingMiddleware : IPurchaseOrderLinePricingMiddleware
{
    private readonly DemandDbContext _db;
    private readonly IPurchasePriceResolver _resolver;
    private readonly InventoryDbContext? _inv;

    public PurchaseOrderLinePricingMiddleware(DemandDbContext db, IPurchasePriceResolver resolver, InventoryDbContext? inv = null)
    {
        _db       = db;
        _resolver = resolver;
        _inv      = inv;
    }

    private Guid Org => _db.TenantContext.OrganizationId;

    /// <summary>Two prices are the same price at their stored precision (BR-25): 4 decimals, no tolerance (A46 E-6 — the A45 2dp comparison is gone).</summary>
    public static bool SamePrice(decimal a, decimal b) =>
        Math.Round(a, PurchasePriceResolver.PriceDecimals, MidpointRounding.AwayFromZero)
        == Math.Round(b, PurchasePriceResolver.PriceDecimals, MidpointRounding.AwayFromZero);

    public static decimal RoundPrice(decimal price) => Math.Round(price, PurchasePriceResolver.PriceDecimals, MidpointRounding.AwayFromZero);

    /// <summary>E-6 — the line total is stored at 2dp: Round(quantity × unit price, 2).</summary>
    public static decimal LineTotal(decimal quantity, decimal unitPrice) => Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    public async Task<LinePricingOutcome> ApplyPricingAsync(PurchaseOrderLine line, PurchaseOrder po, LineChangeContext ctx, CancellationToken ct = default)
    {
        // BR-26: nothing changes the price of a PO that has left DRAFT. A line being born on a PO that is created already approved
        // (an AUTO_SEND back-to-back PO) is priced once, at creation.
        if (!ctx.IsNew && po.Status != "DRAFT") return new LinePricingOutcome { Skipped = true };

        var outcome = new LinePricingOutcome();

        if (ctx.Claim is not null)
        {
            if (await EstablishSourceAsync(line, po, ctx, outcome, ct)) return Finish(line, outcome);
        }

        if (!ctx.IsNew) await ApplyChangeRulesAsync(line, po, ctx, outcome, ct);
        return Finish(line, outcome);
    }

    private static LinePricingOutcome Finish(PurchaseOrderLine line, LinePricingOutcome outcome)
    {
        line.UnitPrice = RoundPrice(line.UnitPrice);
        outcome.PriceWasResolved = line.PriceSource is PriceSource.Pricelist or PriceSource.RFQ or PriceSource.BlanketOrder;
        return outcome;
    }

    // ── A45 D-10 (moved here, E-8): the price source of a line that has none yet ───────────────

    private static void Reset(PurchaseOrderLine line)
    {
        line.PriceSource = PriceSource.Manual;
        line.SystemResolvedPrice = null;
        line.SupplierPricelistUuid = null;
        line.PriceOverrideReason = null;
        ClearConversion(line);
    }

    private static void ClearConversion(PurchaseOrderLine line)
    {
        line.OriginalCurrencyId = null;
        line.OriginalCurrencyPrice = null;
        line.ExchangeRateUsed = null;
        line.ExchangeRateDate = null;
    }

    /// <summary>
    /// Sets the line's PriceSource / SystemResolvedPrice / SupplierPricelistUuid / PriceOverrideReason (and the conversion audit) from
    /// what the client claimed, after checking it. Returns true when the line is final (a contractual RFQ price).
    /// <list type="bullet">
    /// <item>A line with no price (0 or less) is priced from the pricelist whatever it claimed (TS-38, BR-27); nothing found = Manual
    /// at the fallback price (or 0).</item>
    /// <item>RFQ stays only for a line from a requisition line whose RFQ was awarded; Blanket (4) cannot be claimed (E-7) - both Manual.</item>
    /// <item>Pricelist / Override: the server finds the row behind the price - the claimed row when the price is exactly that row's,
    /// else whatever the resolver picks now. A price that differs is an Override (BR-05) with the pricelist price kept in
    /// SystemResolvedPrice (BR-06); with no row to stand behind it the line is Manual.</item>
    /// </list>
    /// </summary>
    private async Task<bool> EstablishSourceAsync(PurchaseOrderLine line, PurchaseOrder po, LineChangeContext ctx, LinePricingOutcome outcome, CancellationToken ct)
    {
        Reset(line);

        var claim = ctx.Claim!;
        var claimed = claim.Source ?? (int)PriceSource.Manual;
        if (claimed is < 0 or > (int)PriceSource.BlanketOrder)
            throw new BadRequestException("Price source must be 0 (Manual), 1 (Pricelist), 2 (Override) or 3 (RFQ).");

        if (claimed == (int)PriceSource.RFQ
            && line.SourcePrLineUuid is { } prLineUuid
            && await _db.PrLines.AsNoTracking().AnyAsync(p => p.UUID == prLineUuid && p.QuotationStatus == "AWARDED", ct))
        {
            line.PriceSource = PriceSource.RFQ;
            return true;
        }
        if (claimed is (int)PriceSource.RFQ or (int)PriceSource.BlanketOrder) claimed = (int)PriceSource.Manual;

        // TS-38 / BR-27: a line the caller did not price is priced by the server.
        if (line.UnitPrice <= 0m)
        {
            var found = await ResolveAndAdoptAsync(line, po, outcome, PriceSource.Pricelist, ct);
            if (!found)
            {
                if (ctx.FallbackPrice is { } fallback && fallback > 0m) line.UnitPrice = fallback;
                outcome.Warning ??= UnpricedWarning(line);
            }
            return false;
        }

        if (claimed == (int)PriceSource.Manual) return false;

        await VerifyClaimAsync(line, po, claim, outcome, ct);
        return false;
    }

    private async Task VerifyClaimAsync(PurchaseOrderLine line, PurchaseOrder po, PriceClaim claim, LinePricingOutcome outcome, CancellationToken ct)
    {
        if (line.VariantUuid is not { } variantUuid || po.SupplierId == Guid.Empty) return;

        SupplierPricelist? claimedRow = null;
        if (claim.PricelistUuid is { } claimedUuid)
        {
            claimedRow = await _db.SupplierPricelists.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(r => r.UUID == claimedUuid && r.OrganizationId == Org && r.SupplierUuid == po.SupplierId
                                       && (r.VariantUuid == variantUuid || r.VariantUuid == null), ct);
            if (claimedRow is not null && (claimedRow.MinQuantity > line.Quantity || !await BelongsToVariantsProductAsync(claimedRow, variantUuid, ct)))
                claimedRow = null;
        }
        // A row in another currency than the PO's cannot be adopted as-is; the resolver converts it below (E-9).
        var claimedUsable = claimedRow is not null && (po.CurrencyId is not { } poCurrency || claimedRow.CurrencyId == poCurrency);

        if (claimedUsable && SamePrice(line.UnitPrice, claimedRow!.UnitPrice))
        {
            line.PriceSource = PriceSource.Pricelist;
            line.SystemResolvedPrice = claimedRow.UnitPrice;
            line.SupplierPricelistUuid = claimedRow.UUID;
            DefaultRequiredDate(line, po, claimedRow.LeadTimeDays);
            return;
        }

        var resolved = await ResolveAsync(line, po, ct);
        if (resolved is { Found: true, UnitPrice: { } resolvedPrice, SupplierPricelistUuid: { } resolvedUuid })
        {
            Adopt(line, resolved, resolvedPrice, resolvedUuid);
            line.PriceSource = SamePrice(line.UnitPrice, resolvedPrice) ? PriceSource.Pricelist : PriceSource.Override;
            outcome.Resolution = resolved;
        }
        else if (claimedUsable)
        {
            line.SystemResolvedPrice = claimedRow!.UnitPrice;
            line.SupplierPricelistUuid = claimedRow.UUID;
            line.PriceSource = PriceSource.Override;
        }
        else
        {
            outcome.Resolution = resolved;
            if (resolved is { CurrencyMismatchWarning: true }) outcome.Warning = resolved.WarningMessage;
            return;
        }

        if (line.PriceSource == PriceSource.Override && !string.IsNullOrWhiteSpace(claim.OverrideReason))
        {
            var reason = claim.OverrideReason.Trim();
            if (reason.Length > 500) throw new BadRequestException("The price override reason must not exceed 500 characters.");
            line.PriceOverrideReason = reason;
        }
        if (line.PriceSource is PriceSource.Pricelist or PriceSource.Override)
            DefaultRequiredDate(line, po, resolved?.LeadTimeDays);
    }

    // A product-level pricelist row (no variant) only prices variants of its own product. Without Inventory the claim is
    // accepted only for a row that names this very variant.
    private async Task<bool> BelongsToVariantsProductAsync(SupplierPricelist row, Guid variantUuid, CancellationToken ct)
    {
        if (row.VariantUuid is not null) return row.VariantUuid == variantUuid;
        if (_inv is null) return false;
        return await _inv.ProductVariants.AsNoTracking().AnyAsync(v => v.Uuid == variantUuid && v.Product.Uuid == row.ProductUuid, ct);
    }

    // ── C4 step 2..4: what a change to an existing line does to its price ──────────────────────

    private async Task ApplyChangeRulesAsync(PurchaseOrderLine line, PurchaseOrder po, LineChangeContext ctx, LinePricingOutcome outcome, CancellationToken ct)
    {
        // BR-28: contractual prices are never re-priced by a mutation.
        if (line.PriceSource is PriceSource.RFQ or PriceSource.BlanketOrder) return;

        var dateRelevant = false;
        if (ctx.DeliveryDateChanged && line.SupplierPricelistUuid is { } rowUuid)
        {
            var basis = await _db.SupplierPricelists.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == Org && r.UUID == rowUuid)
                .Select(r => (PricingDateBasis?)r.PricingDateBasis)
                .FirstOrDefaultAsync(ct);
            dateRelevant = basis == PricingDateBasis.DeliveryDate;
        }
        if (!(ctx.ProductChanged || ctx.QuantityChanged || dateRelevant)) return;

        var resolved = await ResolveAsync(line, po, ct);
        outcome.Resolution = resolved;

        if (resolved is { Found: true, UnitPrice: { } price, SupplierPricelistUuid: { } uuid })
        {
            var wasPricelist = line.PriceSource == PriceSource.Pricelist;
            Adopt(line, resolved, price, uuid);          // SystemResolvedPrice + row + conversion audit - always
            if (wasPricelist) line.UnitPrice = price;    // BR-07: silent re-resolve; Override / Manual keep the buyer's price (TS-40)
            if (line.PriceSource is PriceSource.Pricelist or PriceSource.Override) DefaultRequiredDate(line, po, resolved.LeadTimeDays);
            return;
        }

        // Nothing (or nothing convertible) stands behind this quantity / product / date any more.
        line.SystemResolvedPrice = null;
        line.SupplierPricelistUuid = null;
        ClearConversion(line);
        if (line.PriceSource is PriceSource.Pricelist or PriceSource.Override)
        {
            line.PriceSource = PriceSource.Manual;
            line.PriceOverrideReason = null;
            outcome.Warning = resolved?.WarningMessage ?? UnpricedWarning(line);
        }
        else if (resolved is { CurrencyMismatchWarning: true })
            outcome.Warning = resolved.WarningMessage;
    }

    // ── shared ─────────────────────────────────────────────────────────────────────────────────

    private async Task<PriceResolutionResult?> ResolveAsync(PurchaseOrderLine line, PurchaseOrder po, CancellationToken ct)
    {
        if (line.VariantUuid is not { } variantUuid || po.SupplierId == Guid.Empty) return null;
        return await _resolver.ResolveAsync(new PriceResolutionRequest
        {
            VariantUuid          = variantUuid,
            SupplierUuid         = po.SupplierId,
            Quantity             = line.Quantity,
            // BR-21 - the PO's own order date, never today and never CreatedDate.
            OrderDate            = po.OrderDate,
            ExpectedDeliveryDate = line.RequiredDate ?? po.DeliveryDate,
            PoCurrencyId         = po.CurrencyId
        }, ct);
    }

    /// <summary>Resolves a new line's price and, when found, makes it the line's: source <paramref name="source"/>, price = the resolved one.</summary>
    private async Task<bool> ResolveAndAdoptAsync(PurchaseOrderLine line, PurchaseOrder po, LinePricingOutcome outcome, PriceSource source, CancellationToken ct)
    {
        var resolved = await ResolveAsync(line, po, ct);
        outcome.Resolution = resolved;
        if (resolved is { Found: true, UnitPrice: { } price, SupplierPricelistUuid: { } uuid })
        {
            Adopt(line, resolved, price, uuid);
            line.UnitPrice = price;
            line.PriceSource = source;
            DefaultRequiredDate(line, po, resolved.LeadTimeDays);
            return true;
        }
        if (resolved is { CurrencyMismatchWarning: true }) outcome.Warning = resolved.WarningMessage;
        else if (resolved is { Found: false }) outcome.Warning = resolved.WarningMessage;
        return false;
    }

    private static void Adopt(PurchaseOrderLine line, PriceResolutionResult resolved, decimal price, Guid rowUuid)
    {
        line.SystemResolvedPrice = price;
        line.SupplierPricelistUuid = rowUuid;
        if (resolved.CurrencyConverted)
        {
            line.OriginalCurrencyId = resolved.OriginalCurrencyId;
            line.OriginalCurrencyPrice = resolved.OriginalPrice;
            line.ExchangeRateUsed = resolved.ExchangeRateUsed;
            line.ExchangeRateDate = resolved.ExchangeRateDate;
        }
        else ClearConversion(line);
    }

    /// <summary>E-5 / C1 lead time: a line (and PO) with no delivery date gets OrderDate + the row's lead time, only while empty.</summary>
    private static void DefaultRequiredDate(PurchaseOrderLine line, PurchaseOrder po, int? leadTimeDays)
    {
        if (line.RequiredDate is null && po.DeliveryDate is null && leadTimeDays is { } days and >= 0)
            line.RequiredDate = po.OrderDate.Date.AddDays(days);
    }

    private static string UnpricedWarning(PurchaseOrderLine line) =>
        $"No configured price for {(string.IsNullOrWhiteSpace(line.ItemDescription) ? "this line" : line.ItemDescription)}. Enter the price manually.";
}
