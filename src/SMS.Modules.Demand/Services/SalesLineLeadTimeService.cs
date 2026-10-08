using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

/// <summary>A34 D-16 — the ⏱ endpoints of inquiry and quotation lines (API-CONTRACT §5.2). Sale order lines: ISaleOrderService.</summary>
public interface ISalesLineLeadTimeService
{
    /// <summary>An open inquiry's line, on its variant's route (else the org's SHIP default), its requested date as the target. Null = not found.</summary>
    Task<SaleLineLeadTimeModel<SaleInquiryLineModel>?> CalculateInquiryLineAsync(Guid uuid, Guid lineUuid, int userId);

    /// <summary>A DRAFT quotation's line, on its variant's route (else the org's SHIP default). Null = not found.</summary>
    Task<SaleLineLeadTimeModel<SaleQuotationLineModel>?> CalculateQuotationLineAsync(Guid uuid, Guid lineUuid, int userId);
}

/// <summary>A34 — whether this organization can calculate lead times at all (Inventory's calculator and its module).</summary>
internal static class LeadTimeGate
{
    internal const string InventoryFeature = "MODULE_INVENTORY";
    internal const string Unavailable      = "Lead-time calculation needs the Inventory module.";

    public static async Task<ILeadTimeCalculator> RequireAsync(ILeadTimeCalculator? calculator, ITenantSnapshotProvider? tenants, Guid organizationId)
    {
        if (calculator is null) throw new BadRequestException(Unavailable);
        if (tenants is not null)
        {
            var tenant = await tenants.GetSnapshotAsync(organizationId);
            if (tenant is null || !tenant.EnabledFeatureCodes.Contains(InventoryFeature))
                throw new BadRequestException(Unavailable);
        }
        return calculator;
    }
}

internal sealed class SalesLineLeadTimeService : ISalesLineLeadTimeService
{
    private static readonly string[] OpenInquiry = ["RECEIVED", "UNDER_REVIEW", "REVIEW_COMPLETE"];
    private const string DraftQuotation = "DRAFT";

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISaleInquiryService _inquiries;
    private readonly ISaleQuotationService _quotations;
    private readonly ILeadTimeCalculator? _calculator;
    private readonly ITenantSnapshotProvider? _tenants;

    public SalesLineLeadTimeService(
        DemandDbContext db, ITenantContext tenant, ISaleInquiryService inquiries, ISaleQuotationService quotations,
        ILeadTimeCalculator? calculator = null, ITenantSnapshotProvider? tenants = null)
    {
        _db         = db;
        _tenant     = tenant;
        _inquiries  = inquiries;
        _quotations = quotations;
        _calculator = calculator;
        _tenants    = tenants;
    }

    public async Task<SaleLineLeadTimeModel<SaleInquiryLineModel>?> CalculateInquiryLineAsync(Guid uuid, Guid lineUuid, int userId)
    {
        // Own organization explicitly: the tenant filter is off for a super admin.
        var org = _tenant.OrganizationId;
        var inquiry = await _db.SaleInquiries.Include(i => i.Lines).FirstOrDefaultAsync(i => i.UUID == uuid && i.OrganizationId == org);
        var line = inquiry?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (inquiry is null || line is null) return null;

        if (!OpenInquiry.Contains(inquiry.Status))
            throw new BadRequestException(
                $"Inquiry {inquiry.InquiryNumber} is {inquiry.Status}: line lead times can only be calculated while it is open.");
        if (line.VariantUuid is not { } variant || variant == Guid.Empty)
            throw new BadRequestException(
                $"Line {line.LineNumber} of inquiry {inquiry.InquiryNumber} names no catalog item: identify it to calculate its lead time.");

        var calculator = await LeadTimeGate.RequireAsync(_calculator, _tenants, org);
        var result = await calculator.CalculateAsync(org,
            new LeadTimeRequest(variant, line.RequestedQuantity, null, line.RequestedDeliveryDate?.Date));

        line.CalculatedLeadTimeDays = result.TotalLeadTimeDays;
        line.CalculatedDeliveryDate = result.EarliestDeliveryDate.Date;
        line.LeadTimeCalculatedAt   = result.CalculatedAt;
        line.ModifiedDate           = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var model = await _inquiries.GetByIdAsync(uuid);
        return new SaleLineLeadTimeModel<SaleInquiryLineModel> { Line = model!.Lines.Single(l => l.Uuid == lineUuid), LeadTime = result };
    }

    public async Task<SaleLineLeadTimeModel<SaleQuotationLineModel>?> CalculateQuotationLineAsync(Guid uuid, Guid lineUuid, int userId)
    {
        var org = _tenant.OrganizationId;
        var quotation = await _db.SaleQuotations.Include(q => q.Lines).FirstOrDefaultAsync(q => q.UUID == uuid && q.OrganizationId == org);
        var line = quotation?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (quotation is null || line is null) return null;

        if (quotation.Status != DraftQuotation)
            throw new BadRequestException(
                $"Quotation {quotation.QuotationNumber} is {quotation.Status}: line lead times can only be calculated on a draft.");
        if (line.VariantUuid is not { } variant || variant == Guid.Empty)
            throw new BadRequestException(
                $"Line {line.LineNumber} of quotation {quotation.QuotationNumber} names no catalog item, so it has no lead time.");

        var calculator = await LeadTimeGate.RequireAsync(_calculator, _tenants, org);
        var result = await calculator.CalculateAsync(org, new LeadTimeRequest(variant, line.Quantity));

        line.CalculatedLeadTimeDays = result.TotalLeadTimeDays;
        line.CalculatedDeliveryDate = result.EarliestDeliveryDate.Date;
        line.LeadTimeCalculatedAt   = result.CalculatedAt;
        line.ModifiedDate           = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var model = await _quotations.GetByIdAsync(uuid);
        return new SaleLineLeadTimeModel<SaleQuotationLineModel> { Line = model!.Lines.Single(l => l.Uuid == lineUuid), LeadTime = result };
    }
}
