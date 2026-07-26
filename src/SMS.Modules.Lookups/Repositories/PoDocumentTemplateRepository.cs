using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Models;

namespace SMS.Modules.Lookups.Repositories;

internal sealed class PoDocumentTemplateRepository : IPoDocumentTemplateRepository
{
    private readonly LookupsDbContext _db;

    public PoDocumentTemplateRepository(LookupsDbContext db) => _db = db;

    public async Task<PoDocumentTemplateModel?> GetActiveAsync()
    {
        return await _db.PoDocumentTemplates
            .Where(t => t.IsActive)
            .Select(t => new PoDocumentTemplateModel
            {
                Id                       = t.Id,
                CompanyName              = t.CompanyName,
                CompanyAddress           = t.CompanyAddress,
                CompanyLogoUrl           = t.CompanyLogoUrl,
                CompanyTaxId             = t.CompanyTaxId,
                CompanyPhone             = t.CompanyPhone,
                CompanyEmail             = t.CompanyEmail,
                BodyHtml                 = t.BodyHtml,
                ShowSignatureBlock       = t.ShowSignatureBlock,
                SignatureDisclaimer      = t.SignatureDisclaimer,
                PreparedByLabel          = t.PreparedByLabel,
                ApprovedByLabel          = t.ApprovedByLabel,
                AuthorizedSignatoryLabel = t.AuthorizedSignatoryLabel,
                FooterText               = t.FooterText,
                ModifiedDate             = t.ModifiedDate
            })
            .FirstOrDefaultAsync();
    }

    // Sensible starting point shown the first time an org opens the template editor — mirrors
    // the structure of the sample vessel-acceptance-letter reference doc (To block, subject line,
    // greeting, body paragraph, items table, signature block) using the token syntax the editor
    // supports. The "To" block is plain body text here (not auto-generated) — the org can freely
    // move, restyle, or remove it.
    public const string DefaultBodyHtml =
        "<p>To: {{SupplierName}}<br>Contact: {{SupplierMobile}}</p>" +
        "<p><strong>SUBJECT: Purchase Order {{PoNumber}}</strong></p>" +
        "<p>Dear Sir/Madam,</p>" +
        "<p>Please find below our Purchase Order {{PoNumber}} dated {{PoDate}}, to be delivered by {{DeliveryDate}}. " +
        "Kindly acknowledge receipt and confirm your acceptance of the order details below.</p>" +
        "{{LineItemsTable}}" +
        "<p>Grand Total: {{TotalAmount}}</p>" +
        "{{SignatureBlock}}";

    public const string DefaultFooterText = "This is a system generated document and does not require a signature.";
    public const string DefaultSignatureDisclaimer = "This is a system generated document and does not require a signature.";

    // Single-row upsert: there is exactly one active template for now (multi-template selection
    // is a deferred extension — IsActive already supports it without a schema change later).
    public async Task<Guid> UpsertAsync(UpsertPoDocumentTemplateRequest req, int userId)
    {
        var now = DateTime.UtcNow;
        var existing = await _db.PoDocumentTemplates.FirstOrDefaultAsync(t => t.IsActive);

        if (existing is null)
        {
            existing = new PoDocumentTemplate
            {
                Id          = Guid.NewGuid(),
                IsActive    = true,
                CreatedBy   = userId,
                CreatedDate = now
            };
            _db.PoDocumentTemplates.Add(existing);
        }

        existing.CompanyName              = req.CompanyName;
        existing.CompanyAddress           = req.CompanyAddress;
        existing.CompanyLogoUrl           = req.CompanyLogoUrl;
        existing.CompanyTaxId             = req.CompanyTaxId;
        existing.CompanyPhone             = req.CompanyPhone;
        existing.CompanyEmail             = req.CompanyEmail;
        existing.BodyHtml                 = req.BodyHtml;
        existing.ShowSignatureBlock       = req.ShowSignatureBlock;
        existing.SignatureDisclaimer      = req.SignatureDisclaimer;
        existing.PreparedByLabel          = req.PreparedByLabel;
        existing.ApprovedByLabel          = req.ApprovedByLabel;
        existing.AuthorizedSignatoryLabel = req.AuthorizedSignatoryLabel;
        existing.FooterText               = req.FooterText;
        existing.ModifiedBy               = userId;
        existing.ModifiedDate             = now;

        await _db.SaveChangesAsync();
        return existing.Id;
    }
}