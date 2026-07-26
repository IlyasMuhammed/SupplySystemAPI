using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Repositories;

namespace SMS.Modules.Lookups.Services;

internal sealed class PoDocumentTemplateService : IPoDocumentTemplateService
{
    private readonly IPoDocumentTemplateRepository _repo;

    public PoDocumentTemplateService(IPoDocumentTemplateRepository repo) => _repo = repo;

    public async Task<PoDocumentTemplateModel?> GetActiveAsync()
    {
        var template = await _repo.GetActiveAsync();

        // No template configured yet, or an existing one saved before a field existed — fall
        // back to sensible defaults rather than leaving the PDF/preview blank in those spots.
        if (template is null)
        {
            return new PoDocumentTemplateModel
            {
                BodyHtml                 = PoDocumentTemplateRepository.DefaultBodyHtml,
                ShowSignatureBlock       = true,
                SignatureDisclaimer      = PoDocumentTemplateRepository.DefaultSignatureDisclaimer,
                PreparedByLabel          = "Prepared By",
                ApprovedByLabel          = "Approved By",
                AuthorizedSignatoryLabel = "Authorized Signatory",
                FooterText               = PoDocumentTemplateRepository.DefaultFooterText
            };
        }

        template.BodyHtml            ??= PoDocumentTemplateRepository.DefaultBodyHtml;
        template.SignatureDisclaimer ??= PoDocumentTemplateRepository.DefaultSignatureDisclaimer;
        template.FooterText          ??= PoDocumentTemplateRepository.DefaultFooterText;
        return template;
    }

    public Task<Guid> UpsertAsync(UpsertPoDocumentTemplateRequest req, int userId) =>
        _repo.UpsertAsync(req, userId);

    public IReadOnlyList<PoDocumentTokenModel> GetAvailableTokens() => AvailableTokens;

    public static readonly IReadOnlyList<PoDocumentTokenModel> AvailableTokens = new List<PoDocumentTokenModel>
    {
        new() { Token = "{{PoNumber}}",             Label = "PO Number",              Group = "Purchase Order" },
        new() { Token = "{{PoTitle}}",               Label = "PO Title",               Group = "Purchase Order" },
        new() { Token = "{{PoStatus}}",              Label = "PO Status",              Group = "Purchase Order" },
        new() { Token = "{{PoDate}}",                Label = "PO Created Date",        Group = "Purchase Order" },
        new() { Token = "{{DeliveryDate}}",          Label = "Delivery Date",          Group = "Purchase Order" },
        new() { Token = "{{DeliveryWarehouse}}",     Label = "Delivery Warehouse",     Group = "Purchase Order" },
        new() { Token = "{{BudgetCode}}",            Label = "Budget Code",            Group = "Purchase Order" },
        new() { Token = "{{Notes}}",                 Label = "PO Notes",               Group = "Purchase Order" },
        new() { Token = "{{TotalAmount}}",           Label = "Grand Total",            Group = "Purchase Order" },
        new() { Token = "{{SupplierName}}",          Label = "Supplier Name",          Group = "Supplier" },
        new() { Token = "{{SupplierMobile}}",        Label = "Supplier Mobile/Contact Number", Group = "Supplier" },
        new() { Token = "{{CompanyName}}",           Label = "Company Name",           Group = "Company" },
        new() { Token = "{{CompanyAddress}}",        Label = "Company Address",        Group = "Company" },
        new() { Token = "{{CompanyPhone}}",          Label = "Company Phone",          Group = "Company" },
        new() { Token = "{{CompanyEmail}}",          Label = "Company Email",          Group = "Company" },
        new() { Token = "{{GeneratedDate}}",         Label = "Document Generated Date", Group = "Company" },
        new() { Token = "{{LineItemsTable}}",        Label = "Line Items Table (structural)", Group = "Structural" },
        new() { Token = "{{SignatureBlock}}",        Label = "Signature Block (structural)",  Group = "Structural" },
    };
}