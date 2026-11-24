namespace SMS.Modules.Onboarding.Models;

/// <summary>A51 P1 — an industry with the countries that offer it.</summary>
public class IndustryModel
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Country codes with an active assignment.</summary>
    public List<string> CountryCodes { get; set; } = new();
}

/// <summary>Create and update share the shape. <see cref="CountryCodes"/> null = leave the assignments alone; a list = exactly these countries.</summary>
public class SaveIndustryRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
    public List<string>? CountryCodes { get; set; }
}

public class AssignIndustryCountryRequest
{
    public string CountryCode { get; set; } = string.Empty;
}
