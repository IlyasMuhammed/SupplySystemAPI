namespace SMS.Shared.Common;

/// <summary>
/// Document number prefixes for the manufacturing documents (A30 M20), issued through
/// <see cref="IDocumentNumberGenerator"/> as <c>PREFIX-YYYY-NNNNN</c>. The counter row for a prefix
/// is created on first use, so there is nothing to migrate.
/// </summary>
public static class ManufacturingDocumentPrefix
{
    public const string BillOfMaterials     = "BOM";
    public const string ProductionOrder     = "PROD";
    public const string SupplyRequirement   = "SR";
    /// <summary>Production material issue. Not "MI": MIR and MIV already exist and mean something else.</summary>
    public const string ProductionIssue     = "PMI";
    public const string QualityInspection   = "QI";
    public const string FinishedGoodsReceipt = "FGR";
}
