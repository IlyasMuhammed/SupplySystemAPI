namespace SMS.Modules.Demand.Models;

// A32 C5 — rejection reasons (Demand-owned). See docs/sales-preorder/API-CONTRACT.md §4.

public class RejectionReasonModel
{
    public Guid      Uuid         { get; set; }
    public string    Code         { get; set; } = string.Empty;
    public string    Description  { get; set; } = string.Empty;
    public bool      IsActive     { get; set; }
    /// <summary>One of the ten seeded codes: can be renamed and deactivated, never deleted.</summary>
    public bool      IsSystem     { get; set; }
    public int       DisplayOrder { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>POST /api/rejection-reasons.</summary>
public class CreateRejectionReasonRequest
{
    /// <summary>1–10 characters, letters/digits/underscore; stored upper case; unique per organization.</summary>
    public string Code         { get; set; } = string.Empty;
    /// <summary>1–200 characters.</summary>
    public string Description  { get; set; } = string.Empty;
    /// <summary>Last when omitted.</summary>
    public int?   DisplayOrder { get; set; }
}

/// <summary>PUT /api/rejection-reasons/{uuid} — rename and reorder. The code never changes (it is the analytics key).</summary>
public class UpdateRejectionReasonRequest
{
    public string Description  { get; set; } = string.Empty;
    public int    DisplayOrder { get; set; }
}
