using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>What a stored line looked like before a PO edit rebuilt it (A46-0152).</summary>
internal sealed record PreviousPriceState(decimal UnitPrice, decimal? SystemResolvedPrice, PriceSource Source);

/// <summary>
/// A46-0152 / FSD C4 - folds the per-line pricing-middleware outcomes of a PO edit into the <c>pricing</c> object the PATCH answers
/// with. Pure: no I/O.
/// </summary>
internal static class PoPricingResultBuilder
{
    public const string KeepOrUpdate = "KEEP_OR_UPDATE";
    public const string KeepOrApply = "KEEP_OR_APPLY";

    public static PoPricingResult Build(IEnumerable<(PurchaseOrderLine Line, PurchaseOrderLineResult? Result, PreviousPriceState? Previous)> lines)
    {
        var pricing = new PoPricingResult();
        foreach (var (line, result, previous) in lines)
        {
            var outcome = result?.Outcome;
            var changed = previous is not null && outcome is { Skipped: false } && SystemPriceMoved(previous, line.SystemResolvedPrice);

            PoPricingOffer? offer = null;
            if (changed && line.SystemResolvedPrice is { } system
                && line.PriceSource is PriceSource.Override or PriceSource.Manual
                && !PurchaseOrderLinePricingMiddleware.SamePrice(system, line.UnitPrice))
            {
                offer = new PoPricingOffer
                {
                    Kind = line.PriceSource == PriceSource.Override ? KeepOrUpdate : KeepOrApply,
                    SystemPrice = system, YourPrice = line.UnitPrice, Qty = line.Quantity
                };
            }

            var warning = outcome?.Warning ?? result?.WarningMessage;
            pricing.Lines.Add(new PoPricingLine
            {
                LineUuid = line.UUID, LineNo = line.LineNo,
                PriceSource = (int)line.PriceSource, PriceSourceName = line.PriceSource.ToString(),
                UnitPrice = line.UnitPrice, SystemResolvedPrice = line.SystemResolvedPrice,
                ResolvedChanged = changed, Warning = string.IsNullOrWhiteSpace(warning) ? null : warning,
                CurrencyMismatchWarning = outcome?.Resolution?.CurrencyMismatchWarning ?? false,
                Offer = offer
            });
        }

        pricing.PricedCount = pricing.Lines.Count(l => l.PriceSource != (int)PriceSource.Manual);
        pricing.UnpricedCount = pricing.Lines.Count - pricing.PricedCount;
        pricing.Warnings = pricing.Lines.Where(l => l.Warning is not null).Select(l => l.Warning!).Distinct().ToList();
        return pricing;
    }

    // A Pricelist line's reference is its own price (they are the same thing); Override / Manual lines carry the reference separately.
    private static bool SystemPriceMoved(PreviousPriceState prev, decimal? now)
    {
        var before = prev.SystemResolvedPrice ?? (prev.Source == PriceSource.Pricelist ? prev.UnitPrice : (decimal?)null);
        if (before is null || now is null) return before is null != now is null;
        return !PurchaseOrderLinePricingMiddleware.SamePrice(before.Value, now.Value);
    }
}
