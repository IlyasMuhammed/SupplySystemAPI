namespace SMS.Modules.Integration.Models;

// A49 API-CONTRACT §10.4.

public sealed class CostCenterMappingModel
{
    public Guid    Uuid              { get; set; }
    public string  CostCenterCode    { get; set; } = string.Empty;
    public string  CostCenterName    { get; set; } = string.Empty;
    public string? QboClassRef       { get; set; }
    public string? QboClassName      { get; set; }
    public string? QboDepartmentRef  { get; set; }
    public string? QboDepartmentName { get; set; }
    public bool    IsActive          { get; set; }
    public bool    IsMapped          { get; set; }
}

/// <summary>Null clears the field.</summary>
public sealed class UpdateCostCenterMappingRequest
{
    public string? QboClassRef       { get; set; }
    public string? QboClassName      { get; set; }
    public string? QboDepartmentRef  { get; set; }
    public string? QboDepartmentName { get; set; }
}

public sealed class CostCenterSyncResult
{
    public int Added       { get; set; }
    public int Updated     { get; set; }
    public int Deactivated { get; set; }
}

public sealed class QboRefModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class QboClassReferenceModel
{
    /// <summary>False: no QuickBooks connection, and the lists are empty (not an error).</summary>
    public bool Connected { get; set; }
    /// <summary>True when the company's reference data has been fetched at least once; false with Connected = refresh it.</summary>
    public bool Loaded { get; set; }
    public List<QboRefModel> Classes { get; set; } = [];
    public List<QboRefModel> Departments { get; set; } = [];
    /// <summary>Null when the company's preferences are not known yet.</summary>
    public bool? TracksClasses { get; set; }
}

public sealed record CostCenterQboRefs(string QboClassRef, string? QboClassName, string? QboDepartmentRef, string? QboDepartmentName);
