using SMS.Modules.Lookups.Repositories;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Lookups.Services;

/// <summary>
/// A50 TPL (D-25) — the organization's saved PO letterhead (PoDocumentTemplate) for Onboarding: the one-time copy into the
/// PurchaseOrder DocumentTemplate and the company block of every templated document. Reads the repository, not the
/// service, so an organization that never saved a letterhead answers null instead of the service's invented defaults.
/// </summary>
internal sealed class LegacyLetterheadSource : ILegacyLetterheadSource
{
    private readonly IPoDocumentTemplateRepository _repo;

    public LegacyLetterheadSource(IPoDocumentTemplateRepository repo) => _repo = repo;

    public async Task<LegacyLetterhead?> GetAsync(CancellationToken ct = default)
    {
        var t = await _repo.GetActiveAsync();
        return t is null
            ? null
            : new LegacyLetterhead(
                t.CompanyName, t.CompanyAddress, t.CompanyPhone, t.CompanyEmail, t.CompanyTaxId, t.CompanyLogoUrl,
                t.BodyHtml, t.FooterText, t.BankDetails, t.ShowSignatureBlock, t.SignatureDisclaimer,
                t.PreparedByLabel, t.ApprovedByLabel, t.AuthorizedSignatoryLabel);
    }
}
