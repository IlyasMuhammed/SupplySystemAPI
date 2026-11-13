using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Data;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp;

/// <summary>A48 RPT — aging, inquiry, statements, vendor reconciliation (API-CONTRACT §5).</summary>
internal static partial class ArApRegistration
{
    static partial void AddRpt(IServiceCollection services)
    {
        services.AddScoped<IApDiscountOpportunitySource, PaymentScheduleDiscountOpportunities>();
        services.AddScoped(sp => new ArApReportingService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetService<ISupplierNameLookupService>(),
            sp.GetService<ICurrencyService>(),
            sp.GetService<IOrgCurrencyLookup>(),
            sp.GetService<IApDiscountOpportunitySource>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<IPartnerLookupService>()));
        services.AddScoped<IAgingReportService>(sp => sp.GetRequiredService<ArApReportingService>());
        services.AddScoped<IArInquiryService>(sp => sp.GetRequiredService<ArApReportingService>());
        services.AddScoped<IApInquiryService>(sp => sp.GetRequiredService<ArApReportingService>());

        services.AddScoped(sp => new StatementService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<ArApReportingService>(),
            sp.GetService<IEmailSender>(),
            sp.GetService<IPoDocumentTemplateService>(),
            // A50 (D-25) — the CustomerStatement template; null in hosts without Onboarding (the pre-A50 PDF).
            sp.GetService<SMS.Shared.Onboarding.IDocumentBrandingProvider>(),
            sp.GetService<SMS.Shared.Onboarding.ITemplatedDocumentRenderer>()));
        services.AddScoped<IStatementService>(sp => sp.GetRequiredService<StatementService>());
        services.AddScoped<IVendorReconciliationService>(sp => sp.GetRequiredService<StatementService>());
    }
}

/// <summary>
/// A48 RPT (C12 DiscountOpportunityAmount) — the early-payment discounts still takeable on the as-of day: open, unblocked
/// payment schedules whose discount date has not passed, remaining × discount percent (BR-EP-001; the stored amount when no
/// percent), in the purchase base at the invoice's locked rate.
/// </summary>
internal sealed class PaymentScheduleDiscountOpportunities : IApDiscountOpportunitySource
{
    private static readonly string[] Payable =
        [ArApEnums.ScheduleStatus.Open, ArApEnums.ScheduleStatus.Scheduled, ArApEnums.ScheduleStatus.PartiallyPaid];

    private readonly FinanceDbContext _db;

    public PaymentScheduleDiscountOpportunities(FinanceDbContext db) => _db = db;

    public async Task<decimal> GetDiscountOpportunityBaseAsync(DateOnly asOfDate, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var rows = await _db.PaymentSchedules.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org && !s.PaymentBlock && Payable.Contains(s.Status)
                     && s.DiscountDate != null && s.DiscountDate >= asOfDate && s.AmountRemaining > 0m)
            .Select(s => new { s.AmountRemaining, s.DiscountPercent, s.DiscountAmount, s.Invoice.ExchangeRate })
            .ToListAsync(ct);

        var total = rows.Sum(r =>
        {
            var discount = r.DiscountPercent is { } p and > 0m
                ? Math.Round(r.AmountRemaining * p / 100m, 2, MidpointRounding.AwayFromZero)
                : r.DiscountAmount ?? 0m;
            return discount * (r.ExchangeRate ?? 1m);
        });
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
