using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

internal sealed class SupplierSelectionService : ISupplierSelectionService
{
    private readonly ISaleOrderConfigService _config;
    private readonly IVariantSupplierService _rates;
    private readonly IOrgChartService _orgChart;
    private readonly INotificationService _notifications;
    private readonly IProductVariantResolver _variants;
    private readonly ILogger<SupplierSelectionService> _log;

    public SupplierSelectionService(
        ISaleOrderConfigService config, IVariantSupplierService rates, IOrgChartService orgChart,
        INotificationService notifications, IProductVariantResolver variants, ILogger<SupplierSelectionService> log)
    {
        _config        = config;
        _rates         = rates;
        _orgChart      = orgChart;
        _notifications = notifications;
        _variants      = variants;
        _log           = log;
    }

    public async Task<SupplierSelectionResult> SelectAsync(Guid variantUuid, decimal quantity, int userId)
    {
        var config = await _config.GetConfigAsync();

        var result = config.SupplierSelectionMode switch
        {
            SupplierSelectionModes.DefaultSupplier => await SelectDefaultSupplierAsync(variantUuid),
            SupplierSelectionModes.BestMatch       => await SelectBestMatchAsync(variantUuid),
            _                                       => Manual("This organization is configured for manual supplier selection.")
        };

        if (result.RequiresManualSelection)
            await NotifySupplyTeamAsync(config, variantUuid, quantity, userId, result.Reason);

        return result;
    }

    // §3.3 — "variant's default_supplier_id; none set -> MANUAL for that line." A default supplier
    // with no active rate is treated the same as none set: there is nothing usable to build a PO
    // line from either way.
    private async Task<SupplierSelectionResult> SelectDefaultSupplierAsync(Guid variantUuid)
    {
        var supplierId = await _rates.GetDefaultSupplierIdAsync(variantUuid);
        if (supplierId is null)
            return Manual("The variant has no default supplier configured.");

        var candidates = await _rates.GetComparisonAsync(variantUuid);
        var match = candidates.FirstOrDefault(c => c.SupplierId == supplierId.Value);
        if (match is null)
            return Manual("The variant's default supplier has no active rate for it.");

        return new SupplierSelectionResult(
            false, match.SupplierId, match.SupplierName, match.VendorUnitCost,
            SupplierSelectionModes.DefaultSupplier, "Variant's configured default supplier.");
    }

    // §3.3 — "candidates = active vendors supplying the variant; scorecard grade A=5 B=4 C=3 D=2
    // else 1; latest rate for the qty; composite = score*0.6 + (1/price_rank)*0.4 (price_rank = RANK
    // by rate ascending); highest wins; tie -> shortest lead time."
    //
    // GetComparisonAsync already returns only active vendor rates for the variant, each carrying its
    // ScorecardGrade and LeadTimeDays, sorted by VendorUnitCost ascending — everything this needs,
    // with no separate query. "quantity" isn't used to filter candidates or tier the rate: nothing in
    // this schema ties a VendorUnitCost to a quantity band (no per-qty rate tiers exist), so "latest
    // rate for the qty" cashes out to "the current rate", already what GetComparisonAsync returns.
    private async Task<SupplierSelectionResult> SelectBestMatchAsync(Guid variantUuid)
    {
        var candidates = await _rates.GetComparisonAsync(variantUuid);
        if (candidates.Count == 0)
            return Manual("No active vendor supplies this variant.");

        var best = RankByPrice(candidates)
            .Select(r => (r.Candidate, r.Rank, Composite: GradeScore(r.Candidate.ScorecardGrade) * 0.6m + 1m / r.Rank * 0.4m))
            .OrderByDescending(x => x.Composite)
            .ThenBy(x => x.Candidate.LeadTimeDays ?? int.MaxValue)
            .First();

        return new SupplierSelectionResult(
            false, best.Candidate.SupplierId, best.Candidate.SupplierName, best.Candidate.VendorUnitCost,
            SupplierSelectionModes.BestMatch,
            $"Composite score {best.Composite:0.###} (grade {best.Candidate.ScorecardGrade ?? "unscored"}, price rank {best.Rank}).");
    }

    private static SupplierSelectionResult Manual(string reason) =>
        new(true, null, null, null, SupplierSelectionModes.Manual, reason);

    private static int GradeScore(string? grade) => grade switch
    {
        "A" => 5,
        "B" => 4,
        "C" => 3,
        "D" => 2,
        _   => 1
    };

    // Standard SQL RANK() semantics: rows tied on price share a rank, and the next distinct price
    // jumps to its 1-based position in the list rather than the next integer. candidates is already
    // sorted ascending by VendorUnitCost, so this is one linear pass.
    private static IReadOnlyList<(RateComparisonRowModel Candidate, int Rank)> RankByPrice(
        IReadOnlyList<RateComparisonRowModel> candidates)
    {
        var result = new List<(RateComparisonRowModel, int)>(candidates.Count);
        var rank = 0;
        decimal? lastPrice = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (lastPrice is null || candidates[i].VendorUnitCost != lastPrice)
            {
                rank = i + 1;
                lastPrice = candidates[i].VendorUnitCost;
            }
            result.Add((candidates[i], rank));
        }

        return result;
    }

    // §3.3 — "MANUAL: no auto-PO; create a notification/task for the supply team." No dedicated
    // task-tracking table exists anywhere in this codebase, so this is a real notification through
    // the same pipeline A29-P4-07 uses, to the intimation department when the organization has one
    // configured with a head — and to whoever confirmed the sale order otherwise, so a line stuck
    // needing a manual supplier is never something nobody was ever told about. Never throws: a
    // failed notification must not be the reason a caller can't finish confirming a sale order.
    private async Task NotifySupplyTeamAsync(
        SaleOrderConfigModel config, Guid variantUuid, decimal quantity, int userId, string reason)
    {
        try
        {
            var head = config.IntimationDepartmentId is { } deptId
                ? await _orgChart.GetDepartmentHeadAsync(deptId)
                : null;
            var recipientId = head?.UserId ?? userId;

            // Named for the reader, not the database — an id means nothing to whoever has to act
            // on this. Falls back to the id only if the variant cannot be resolved (deleted since,
            // or the resolver has nothing for it), so the notification is never simply dropped.
            var described = await _variants.DescribeVariantsAsync([variantUuid]);
            var itemName = described.TryGetValue(variantUuid, out var variant) ? variant.DisplayName : variantUuid.ToString();

            await _notifications.TryCreateAsync(new NotificationRequest(
                UserId:    recipientId,
                Type:      "SUPPLIER_SELECTION_MANUAL",
                Title:     "Supplier Selection Needed",
                Message:   $"No supplier could be auto-selected for {itemName} (qty {quantity:0.####}). {reason}",
                Category:  "Demand",
                CreatedBy: userId,
                SendEmail: true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to notify the supply team about variant {VariantUuid}.", variantUuid);
        }
    }
}
