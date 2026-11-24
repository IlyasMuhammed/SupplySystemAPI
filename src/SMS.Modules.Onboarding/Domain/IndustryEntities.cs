using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Domain;

// A51 P1 — the industries master and which countries offer which. Platform data (no organization); the country code is
// the ISO code of Lookups.Countries (no FK across schemas, D-2).

/// <summary>An industry an organization can be created in (TRADING, MANUFACTURING, SERVICES, RETAIL ...).</summary>
public sealed class Industry : IHasModifiedAt
{
    public int    Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool   IsActive { get; set; } = true;
    public int    SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int?   CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public int?   ModifiedBy { get; set; }
}

/// <summary>An industry offered in a country; unique per (country, industry).</summary>
public sealed class CountryIndustry
{
    public int    Id { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string IndustryCode { get; set; } = string.Empty;
    public bool   IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int?   CreatedBy { get; set; }
}
