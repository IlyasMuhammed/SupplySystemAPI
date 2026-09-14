using SMS.Modules.Inventory.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services;

// RC-001 — validates a VariantSupplier's discount_tiers JSON payload before it's serialized and
// persisted. No JSON-schema library is used anywhere in this codebase (see
// AttributeDefinition.DropdownOptions) — this is a plain imperative check, same convention.
internal static class DiscountTierValidator
{
    public static void Validate(List<DiscountTierDto> tiers)
    {
        if (tiers.Count == 0) return;

        var sorted = tiers.OrderBy(t => t.QtyFrom).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var t = sorted[i];

            if (t.DiscountPct < 0 || t.DiscountPct > 100)
                throw new BadRequestException(
                    $"Discount tier ({t.QtyFrom}-{(t.QtyTo?.ToString() ?? "∞")}) has an invalid discount_pct ({t.DiscountPct}) — must be between 0 and 100.");

            if (t.QtyTo.HasValue && t.QtyFrom >= t.QtyTo.Value)
                throw new BadRequestException(
                    $"Discount tier qty_from ({t.QtyFrom}) must be less than qty_to ({t.QtyTo}).");

            if (i < sorted.Count - 1)
            {
                var next = sorted[i + 1];
                if (!t.QtyTo.HasValue || t.QtyTo.Value >= next.QtyFrom)
                    throw new BadRequestException(
                        $"Discount tiers overlap: ({t.QtyFrom}-{(t.QtyTo?.ToString() ?? "∞")}) overlaps ({next.QtyFrom}-{(next.QtyTo?.ToString() ?? "∞")}).");
            }
        }
    }
}
