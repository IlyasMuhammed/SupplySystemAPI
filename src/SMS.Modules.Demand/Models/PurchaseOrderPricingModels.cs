using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Models;

/// <summary>
/// A46 C5 / BR-30 — what a batch of purchase order lines created together (a requisition converted to one or more POs) came
/// out as: how many were priced from a pricelist and how many still need a person to price them.
/// <c>pricedCount</c> counts lines the factory priced (pricelist) or took a contractual price for (an awarded RFQ);
/// <c>unpricedCount</c> the rest. <c>warnings</c> carries one line per unpriced line, naming it.
/// </summary>
public class PricingSummary
{
    public int TotalLines { get; set; }
    public int PricedCount { get; set; }
    public int UnpricedCount { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// The standard envelope plus the batch pricing summary as a sibling of <c>result</c> (so the existing
/// <c>ApiResponse&lt;string&gt;</c> readers of the convert endpoints keep working untouched).
/// </summary>
public class PricedApiResponse<T> : ApiResponse<T>
{
    public PricingSummary? PricingSummary { get; set; }

    public static PricedApiResponse<T> Ok(T result, PricingSummary? summary, string message = "Request successful") =>
        new() { Success = true, Message = message, Result = result, PricingSummary = summary };
}

/// <summary>
/// A46-0152 / FSD C4 - what a PO edit (PATCH) did to the price of each line, so the UI can show the Override toast and the
/// "keep or update" choice. Built from the per-line pricing-middleware outcomes. Additive: the PATCH envelope keeps
/// <c>success</c>/<c>message</c> and carries this as the top-level <c>pricing</c>.
/// </summary>
public class PoPricingResult
{
    public List<PoPricingLine> Lines { get; set; } = new();
    public int PricedCount { get; set; }
    public int UnpricedCount { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class PoPricingLine
{
    /// <summary>The line's UUID as stored after the save (a PO edit rebuilds its lines, so this is the new one).</summary>
    public Guid LineUuid { get; set; }
    public int LineNo { get; set; }
    /// <summary>0 Manual, 1 Pricelist, 2 Override, 3 RFQ, 4 BlanketOrder (the same codes the request takes).</summary>
    public int PriceSource { get; set; }
    public string PriceSourceName { get; set; } = "Manual";
    public decimal UnitPrice { get; set; }
    public decimal? SystemResolvedPrice { get; set; }
    /// <summary>The pricelist's price for this line moved compared with what was stored before the edit (quantity / date / product change).</summary>
    public bool ResolvedChanged { get; set; }
    public string? Warning { get; set; }
    public bool CurrencyMismatchWarning { get; set; }
    public PoPricingOffer? Offer { get; set; }
}

/// <summary>An Override or Manual line whose system price changed: the buyer chooses to keep their price or take the system's.</summary>
public class PoPricingOffer
{
    /// <summary>KEEP_OR_UPDATE (Override line: update its system price reference) or KEEP_OR_APPLY (Manual line: apply the system price).</summary>
    public string Kind { get; set; } = string.Empty;
    public decimal SystemPrice { get; set; }
    public decimal YourPrice { get; set; }
    public decimal Qty { get; set; }
}

/// <summary>A PO edit's outcome: the audited field changes (as before) and the pricing result.</summary>
public sealed record PoUpdateResult(IReadOnlyList<PoFieldChange> Changes, PoPricingResult Pricing);

/// <summary>The standard envelope plus the PO edit's pricing result as a top-level sibling of <c>result</c>.</summary>
public class PoPricingApiResponse : ApiResponse
{
    public PoPricingResult? Pricing { get; set; }
}

/// <summary>A PR → PO conversion's outcome: the new PO(s) and the pricing summary across all their lines.</summary>
public sealed record PoConversionResult(IReadOnlyList<Guid> PoUuids, PricingSummary Summary);
