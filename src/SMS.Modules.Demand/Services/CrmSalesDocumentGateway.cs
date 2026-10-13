using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A41 D-13 — the CRM opportunity's way into the A32 pipeline (<see cref="ICrmSalesDocumentGateway"/>). Creation goes through
/// the A32 services themselves (their rules, numbering and the caller's organization) with <c>SourceOpportunityUuid</c> set;
/// the reads take the organization explicitly.
/// </summary>
internal sealed class CrmSalesDocumentGateway : ICrmSalesDocumentGateway
{
    /// <summary>A quotation made for an opportunity is valid for this many days (the seller changes it in DRAFT).</summary>
    internal const int DefaultQuotationValidityDays = 30;

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISaleInquiryService _inquiries;
    private readonly ISaleQuotationService _quotations;
    private readonly ICurrencyCodeLookup? _currencyCodes;

    public CrmSalesDocumentGateway(DemandDbContext db, ITenantContext tenant, ISaleInquiryService inquiries, ISaleQuotationService quotations,
        ICurrencyCodeLookup? currencyCodes = null)
    {
        _db = db;
        _tenant = tenant;
        _inquiries = inquiries;
        _quotations = quotations;
        _currencyCodes = currencyCodes;
    }

    public async Task<CrmSalesDocument> CreateInquiryAsync(Guid opportunityUuid, Guid accountUuid, int? assignedToUserId, string? notes, int userId,
        CancellationToken ct = default)
    {
        var uuid = await _inquiries.CreateAsync(new CreateSaleInquiryRequest
        {
            PartnerId = accountUuid, AssignedToUserId = assignedToUserId, Notes = Trim(notes, 2000), SourceOpportunityUuid = opportunityUuid
        }, userId);
        return (await ListAsync(_tenant.OrganizationId, opportunityUuid, ct)).First(d => d.Uuid == uuid);
    }

    public async Task<CrmSalesDocument> CreateQuotationAsync(Guid opportunityUuid, Guid accountUuid, Guid? fromInquiryUuid, string? notes, int userId,
        CancellationToken ct = default)
    {
        var validTo = DateTime.UtcNow.Date.AddDays(DefaultQuotationValidityDays);
        Guid uuid;
        if (fromInquiryUuid is { } inquiryUuid)
        {
            uuid = await _quotations.CreateFromInquiryAsync(inquiryUuid, new CreateSaleQuotationFromInquiryRequest
            {
                ValidTo = validTo, InternalNotes = Trim(notes, 2000)
            }, userId);
            // The inquiry may have been raised by hand and linked later: the quotation still belongs to this opportunity.
            var created = await _db.SaleQuotations.FirstAsync(q => q.UUID == uuid && q.OrganizationId == _tenant.OrganizationId, ct);
            if (created.SourceOpportunityUuid != opportunityUuid)
            {
                created.SourceOpportunityUuid = opportunityUuid;
                await _db.SaveChangesAsync(ct);
            }
        }
        else
            uuid = await _quotations.CreateAsync(new CreateSaleQuotationRequest
            {
                PartnerId = accountUuid, ValidTo = validTo, InternalNotes = Trim(notes, 2000), SourceOpportunityUuid = opportunityUuid
            }, userId);
        return (await ListAsync(_tenant.OrganizationId, opportunityUuid, ct)).First(d => d.Uuid == uuid);
    }

    public Task<IReadOnlyList<CrmSalesDocument>> ListForOpportunityAsync(Guid organizationId, Guid opportunityUuid, CancellationToken ct = default) =>
        ListAsync(organizationId, opportunityUuid, ct);

    private async Task<IReadOnlyList<CrmSalesDocument>> ListAsync(Guid org, Guid opp, CancellationToken ct)
    {
        var inquiries = await _db.SaleInquiries.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceOpportunityUuid == opp)
            .Select(i => new { i.UUID, i.InquiryNumber, i.Status, i.CreatedDate, i.CurrencyId }).ToListAsync(ct);
        var quotations = await _db.SaleQuotations.IgnoreQueryFilters().AsNoTracking()
            .Where(q => q.OrganizationId == org && q.SourceOpportunityUuid == opp)
            .Select(q => new { q.UUID, q.QuotationNumber, q.Status, q.CreatedDate, q.GrandTotal, q.CurrencyId }).ToListAsync(ct);
        var orders = await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == org && o.SourceOpportunityUuid == opp && !o.IsDeleted)
            .Select(o => new { o.UUID, o.SoNumber, o.Status, o.CreatedDate, o.GrandTotal, o.CurrencyId }).ToListAsync(ct);

        var codes = new Dictionary<Guid, string?>();
        async Task<string?> Code(Guid? id)
        {
            if (id is not { } c || c == Guid.Empty || _currencyCodes is null) return null;
            if (!codes.TryGetValue(c, out var code)) codes[c] = code = (await _currencyCodes.GetCodeAsync(c, ct))?.Trim();
            return code;
        }

        var docs = new List<CrmSalesDocument>();
        foreach (var i in inquiries)
            docs.Add(new CrmSalesDocument(CrmSalesDocumentKind.Inquiry, i.UUID, i.InquiryNumber, i.Status, Utc(i.CreatedDate), null, await Code(i.CurrencyId)));
        foreach (var q in quotations)
            docs.Add(new CrmSalesDocument(CrmSalesDocumentKind.Quotation, q.UUID, q.QuotationNumber, q.Status, Utc(q.CreatedDate), q.GrandTotal, await Code(q.CurrencyId)));
        foreach (var o in orders)
            docs.Add(new CrmSalesDocument(CrmSalesDocumentKind.Order, o.UUID, o.SoNumber, o.Status, Utc(o.CreatedDate), o.GrandTotal, await Code(o.CurrencyId)));
        return docs.OrderBy(d => d.CreatedAt).ToList();
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static string? Trim(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];
}
