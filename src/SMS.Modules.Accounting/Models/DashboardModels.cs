namespace SMS.Modules.Accounting.Models;

/// <summary>GET api/finance/gl/dashboard (D-4 gated) — sums of leaf current balances per root type.</summary>
public sealed class GlDashboardModel
{
    public decimal TotalAssets      { get; set; }
    public decimal TotalLiabilities { get; set; }
    public decimal TotalEquity      { get; set; }
    public decimal TotalRevenue     { get; set; }
    public decimal TotalExpenses    { get; set; }
    public string? CurrencyCode     { get; set; }
    public int     AccountCount     { get; set; }
    public GlDashboardFiscalYear? FiscalYear { get; set; }
}

public sealed class GlDashboardFiscalYear
{
    public string   Name      { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate   { get; set; }
}
