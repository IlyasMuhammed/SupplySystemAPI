namespace SMS.Modules.Reports.Models;

/// <summary>
/// One KPI: its value, and how many documents it was worked out from. <see cref="Value"/> is null when there was
/// nothing to measure (no basis) — a page must show "no data", not a zero that reads as a failing score.
/// </summary>
public class KpiMetric
{
    public double? Value { get; set; }
    public int     Basis { get; set; }

    public static KpiMetric Rate(int hits, int basis) =>
        new() { Value = basis > 0 ? Math.Round((double)hits / basis * 100, 1) : null, Basis = basis };

    public static KpiMetric Ratio(decimal part, decimal whole, int basis) =>
        new() { Value = whole > 0 ? Math.Round((double)(part / whole) * 100, 1) : null, Basis = basis };

    public static KpiMetric Average(IReadOnlyCollection<double> values) =>
        new() { Value = values.Count > 0 ? Math.Round(values.Average(), 1) : null, Basis = values.Count };
}

/// <summary>
/// The KPIs of the modules built after the original KPI dashboard — sales, fulfilment, manufacturing and receivables —
/// over a rolling window. A section is null when the caller may not see that area.
/// </summary>
public class OperationsKpiModel
{
    public DateTime From       { get; set; }
    public DateTime To         { get; set; }
    public int      WindowDays { get; set; }

    public SalesKpis?         Sales         { get; set; }
    public FulfilmentKpis?    Fulfilment    { get; set; }
    public ManufacturingKpis? Manufacturing { get; set; }
    public ReceivablesKpis?   Receivables   { get; set; }
}

public class SalesKpis
{
    /// <summary>Quotations decided in the window that the customer accepted (or that became an order). %.</summary>
    public KpiMetric QuoteWinRate          { get; set; } = new();
    /// <summary>Inquiries received in the window and decided, that were quoted rather than declined. %.</summary>
    public KpiMetric InquiryConversionRate { get; set; } = new();
    /// <summary>Sale orders placed in the window (beyond draft) that were later cancelled. %.</summary>
    public KpiMetric OrderCancellationRate { get; set; } = new();
    /// <summary>Open sale orders (confirmed or partly fulfilled) already past their expected delivery date. %, now.</summary>
    public KpiMetric LateOpenOrderRate     { get; set; } = new();
}

public class FulfilmentKpis
{
    /// <summary>Outbound deliveries issued in the window on or before their promised (else requested) date. %.</summary>
    public KpiMetric OnTimeShipmentRate { get; set; } = new();
    /// <summary>Completed outbound deliveries in the window where every line arrived in full. %.</summary>
    public KpiMetric InFullRate         { get; set; } = new();
    /// <summary>Average days from the sale order's date to its delivery's goods issue, for issues in the window.</summary>
    public KpiMetric OrderToShipDays    { get; set; } = new();
}

public class ManufacturingKpis
{
    /// <summary>Production orders finished in the window by their required date. %.</summary>
    public KpiMetric OnTimeCompletionRate { get; set; } = new();
    /// <summary>Produced ÷ planned quantity, for orders finished in the window. %.</summary>
    public KpiMetric PlanAttainment       { get; set; } = new();
    /// <summary>Accepted ÷ produced quantity (quality passed first time), for orders finished in the window. %.</summary>
    public KpiMetric FirstPassYield       { get; set; } = new();
    /// <summary>Average days from actual start to actual end, for orders finished in the window.</summary>
    public KpiMetric CycleTimeDays        { get; set; } = new();
}

public class ReceivablesKpis
{
    /// <summary>Days sales outstanding: base-currency balance owed ÷ base-currency invoiced in the window × window days.</summary>
    public KpiMetric DaysSalesOutstanding { get; set; } = new();
    /// <summary>Share of the base-currency balance owed that is past due. %.</summary>
    public KpiMetric OverdueRate          { get; set; } = new();
    /// <summary>Open invoices left out of the two above because no exchange rate was on file for them.</summary>
    public int UnconvertedInvoices { get; set; }
}
