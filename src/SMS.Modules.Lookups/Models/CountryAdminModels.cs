namespace SMS.Modules.Lookups.Models;

/// <summary>A51 P1 — a country of the master, with its organization defaults.</summary>
public class CountryAdminModel
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CurrencyCode { get; set; }
    public string? DefaultTimezone { get; set; }
    public int FiscalYearStartMonth { get; set; } = 1;
    public string DateFormat { get; set; } = "DD/MM/YYYY";
    public string? PhoneCode { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Create and update share the shape; the code is fixed once a country exists.</summary>
public class SaveCountryRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? CurrencyCode { get; set; }
    public string? DefaultTimezone { get; set; }
    public int? FiscalYearStartMonth { get; set; }
    public string? DateFormat { get; set; }
    public string? PhoneCode { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
}
