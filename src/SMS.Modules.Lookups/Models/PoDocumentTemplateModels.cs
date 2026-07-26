namespace SMS.Modules.Lookups.Models;

public class PoDocumentTemplateModel
{
    public Guid Id { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyAddress { get; set; }
    public string? CompanyLogoUrl { get; set; }
    public string? CompanyTaxId { get; set; }
    public string? CompanyPhone { get; set; }
    public string? CompanyEmail { get; set; }
    public string? BodyHtml { get; set; }
    public bool ShowSignatureBlock { get; set; }
    public string? SignatureDisclaimer { get; set; }
    public string? PreparedByLabel { get; set; }
    public string? ApprovedByLabel { get; set; }
    public string? AuthorizedSignatoryLabel { get; set; }
    public string? FooterText { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

public class UpsertPoDocumentTemplateRequest
{
    public string? CompanyName { get; set; }
    public string? CompanyAddress { get; set; }
    public string? CompanyLogoUrl { get; set; }
    public string? CompanyTaxId { get; set; }
    public string? CompanyPhone { get; set; }
    public string? CompanyEmail { get; set; }
    public string? BodyHtml { get; set; }
    public bool ShowSignatureBlock { get; set; } = true;
    public string? SignatureDisclaimer { get; set; }
    public string? PreparedByLabel { get; set; }
    public string? ApprovedByLabel { get; set; }
    public string? AuthorizedSignatoryLabel { get; set; }
    public string? FooterText { get; set; }
}

// Metadata for the frontend's "Insert Variable" picker — no live data, just token names/descriptions.
public class PoDocumentTokenModel
{
    public string Token { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
}
