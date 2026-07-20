namespace SMS.Modules.Suppliers.Models;

public class ScorecardDimensionWeightModel
{
    public string    DimensionCode    { get; set; } = string.Empty;
    public string    DimensionName    { get; set; } = string.Empty;
    public decimal   WeightPercentage { get; set; }
    public decimal   MaxPoints        { get; set; }
    public bool      IsActive         { get; set; }
    public DateTime? ModifiedDate     { get; set; }
}

public class UpdateDimensionWeightItem
{
    public string  DimensionCode    { get; set; } = string.Empty;
    public decimal WeightPercentage { get; set; }
    public decimal MaxPoints        { get; set; }
}

public class UpdateScorecardWeightsRequest
{
    public List<UpdateDimensionWeightItem> Weights { get; set; } = [];
}
