using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Warehouse.Data;

namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>
/// REV-8 (D-AP-19) — the approval gate re-evaluates an A48-matched invoice at approval, under the invoice and purchase
/// order locks, against what is invoiced <b>now</b> (QtyInvoiced moves with every approval). A stored "Matched" result can
/// be stale: two invoices on one PO line, both matched before either was approved, would otherwise both be approved.
/// </summary>
internal static class MatchRecheck
{
    /// <summary>The vendor's active rule, else the organization's active default; none → exact match.</summary>
    internal static async Task<(MatchToleranceRule? Rule, MatchTolerance Tolerance)> ToleranceForAsync(
        FinanceDbContext fin, Guid organizationId, Guid vendorUuid, CancellationToken ct)
    {
        var rules = fin.Set<MatchToleranceRule>().AsNoTracking().Where(r => r.OrganizationId == organizationId && r.IsActive);
        var rule = await rules.Where(r => r.VendorUuid == vendorUuid).OrderBy(r => r.Id).FirstOrDefaultAsync(ct)
                ?? await rules.Where(r => r.IsDefault && r.VendorUuid == null).OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        return rule is null
            ? (null, MatchTolerance.Exact)
            : (rule, Tolerance(rule));
    }

    internal static MatchTolerance Tolerance(MatchToleranceRule rule) =>
        new(rule.PriceTolerancePercent, rule.PriceToleranceAmount, rule.QuantityTolerancePercent,
            rule.TotalTolerancePercent, rule.TotalToleranceAmount, rule.AutoApproveWithin, rule.Id, rule.Name);

    /// <summary>
    /// Null when the invoice still matches (or was never A48-matched, or its variance was overridden); otherwise why not.
    /// Must run after the purchase order's lock (so QtyInvoiced is as committed now). Uses the rule the match used when it
    /// is still active, else the current one.
    /// </summary>
    internal static async Task<string?> ProblemAsync(
        FinanceDbContext fin, DemandDbContext demand, WarehouseDbContext warehouse, Invoice invoice, CancellationToken ct = default)
    {
        if (invoice.LineMatchResult is null || invoice.MatchOverrideReason is not null || invoice.PoUuid is null) return null;

        var tolerance = invoice.ToleranceRuleId is { } ruleId
                        && await fin.Set<MatchToleranceRule>().AsNoTracking()
                               .FirstOrDefaultAsync(r => r.Id == ruleId && r.OrganizationId == invoice.OrganizationId && r.IsActive, ct) is { } used
            ? Tolerance(used)
            : (await ToleranceForAsync(fin, invoice.OrganizationId, invoice.SupplierId, ct)).Tolerance;

        MatchSource source;
        try { source = await new MatchSourceReader(demand, warehouse).ReadAsync(invoice, invoice.MatchType, ct); }
        catch (SMS.Shared.Exceptions.BadRequestException ex) { return ex.Message; }

        var eval = MatchEvaluator.Evaluate(source.Lines, tolerance, source.MatchType == MatchStatuses.TwoWay);
        if (eval.Status == MatchStatuses.Matched) return null;

        var first = eval.Lines.FirstOrDefault(l => l.Status != MatchStatuses.Matched);
        var detail = first is null ? eval.Status : $"line {first.Input.LineNumber} is {first.Status}";
        return $"Invoice {invoice.InvoiceNumber} no longer matches its purchase order ({detail}) — another approved invoice may have billed "
             + "the same quantity. Run the match again, or override the variance with a reason.";
    }
}
