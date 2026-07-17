namespace SMS.Modules.Material.Models;

public class PrLineSearchResult
{
    public string  PrNumber                { get; set; } = string.Empty;
    public string  PrTitle                 { get; set; } = string.Empty;
    public string  LineDescription         { get; set; } = string.Empty;
    public decimal RequestedQty            { get; set; }
    public decimal RemainingUndisbursedQty { get; set; }
    public Guid    PrLineId                { get; set; }
}

public class PrLineDisbursementModel
{
    public Guid     MirUuid       { get; set; }
    public string   MirNumber     { get; set; } = string.Empty;
    public DateTime? ApprovedDate { get; set; }
    public decimal  ApprovedQty   { get; set; }
    public string?  ProjectOrDept { get; set; }
}
