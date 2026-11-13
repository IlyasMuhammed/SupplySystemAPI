using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Suppliers.Onboarding;

/// <summary>
/// A50 D-27: the sample customers (5) and vendors (3), through this module's own create paths (<see cref="ICustomerService"/>,
/// <see cref="ISuppliersService"/>), with the FSD §7.1 names plus " (Demo)" so they never collide with real partners.
/// Demo vendors are approved, so a demo PO can be raised to them. One record failing is skipped and reported as a note.
/// <para>Cleanup goes through <see cref="IBusinessPartnerService.DeleteAsync"/> and its supplier reference checks (POs, RFQs,
/// invoices, payments, GRNs). Before that, a customer still used by a sale order, quotation, inquiry, sales invoice or customer
/// payment is kept: the same direct reads of Demand and Finance this module already makes for balances and scoring.</para>
/// </summary>
internal sealed class SuppliersSampleDataContributor : ISampleDataContributor
{
    internal const string CustomerType = "Customer";
    internal const string VendorType   = "Vendor";
    internal const string NoteType     = "Note";
    internal const string DemoSuffix   = " (Demo)";

    internal static readonly IReadOnlyList<(string Name, string City, string Email)> Customers =
    [
        ("Acme Corporation",       "New York",   "orders@acme.example"),
        ("BlueStar Enterprises",   "Chicago",    "buying@bluestar.example"),
        ("Lone Star Offices LLC",  "Austin",     "office@lonestar.example"),
        ("Cedar Trading Co",       "Denver",     "trade@cedar.example"),
        ("Highland Industries",    "Seattle",    "purchasing@highland.example"),
    ];

    internal static readonly IReadOnlyList<(string Name, string Code, string City, string Email)> Vendors =
    [
        ("GlobalSupply Inc",   "DEMO-V01", "Houston",   "sales@globalsupply.example"),
        ("Hartwell Furniture", "DEMO-V02", "Charlotte", "sales@hartwell.example"),
        ("QuickParts Co",      "DEMO-V03", "Phoenix",   "sales@quickparts.example"),
    ];

    private readonly SuppliersDbContext _db;
    private readonly ICustomerService _customers;
    private readonly ISuppliersService _suppliers;
    private readonly IBusinessPartnerService _partners;
    private readonly DemandDbContext? _demand;
    private readonly FinanceDbContext? _finance;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger _log;

    public SuppliersSampleDataContributor(SuppliersDbContext db, ICustomerService customers, ISuppliersService suppliers,
        IBusinessPartnerService partners, DemandDbContext? demand = null, FinanceDbContext? finance = null,
        IHttpContextAccessor? http = null, ILogger<SuppliersSampleDataContributor>? log = null)
    {
        _db = db;
        _customers = customers;
        _suppliers = suppliers;
        _partners = partners;
        _demand = demand;
        _finance = finance;
        _http = http;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public int Order => 10;
    public string? RequiresModule => null;

    private int UserId => _http?.HttpContext?.User is { } u && u.GetUserId() is var id && id > 0 ? id : 0;
    private Guid OrgId => _db.TenantContext.OrganizationId;

    public Task<SampleDataPlan> PreviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new SampleDataPlan { Counts = { [CustomerType] = Customers.Count, [VendorType] = Vendors.Count } });

    public async Task<IReadOnlyList<DemoRecordRef>> CreateAsync(IReadOnlyList<DemoRecordRef> createdSoFar, CancellationToken ct = default)
    {
        var refs = new List<DemoRecordRef>();
        foreach (var c in Customers)
        {
            try
            {
                var uuid = await _customers.CreateAsync(new CustomerUpsert
                {
                    Name = c.Name + DemoSuffix, CustomerType = CustomerTypes.Company, City = c.City, Country = "United States",
                    Email = c.Email, PaymentTermsDays = 30, Notes = "Sample data: remove it from Settings › Organization › Sample Data."
                }, UserId, ct);
                refs.Add(new(CustomerType, uuid.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data: customer {Name} was not created.", c.Name);
                refs.Add(new(NoteType, $"Customer {c.Name}{DemoSuffix} was not created: {ex.Message}"));
            }
        }

        foreach (var v in Vendors)
        {
            try
            {
                var uuid = await _suppliers.CreateSupplierAsync(new CreateSupplierRequest
                {
                    SupplierName = v.Name + DemoSuffix, SupplierCode = v.Code, City = v.City, Country = "United States",
                    Email = v.Email, LeadTimeDays = 7, Notes = "Sample data: remove it from Settings › Organization › Sample Data."
                }, UserId);
                refs.Add(new(VendorType, uuid.ToString()));
                // Approved like a vendor that passed onboarding, so a purchase order can be raised to it.
                await _suppliers.ApproveSupplierAsync(uuid, UserId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data: vendor {Name} was not created or approved.", v.Name);
                refs.Add(new(NoteType, $"Vendor {v.Name}{DemoSuffix} was not created: {ex.Message}"));
            }
        }
        return refs;
    }

    public async Task<IReadOnlyList<DemoRecordRef>> DeleteAsync(IReadOnlyList<DemoRecordRef> refs, CancellationToken ct = default)
    {
        var kept = new List<DemoRecordRef>();
        foreach (var r in refs)
        {
            if (r.EntityType is not (CustomerType or VendorType) || !Guid.TryParse(r.EntityRef, out var uuid)) continue;
            var partner = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.UUID == uuid && p.OrganizationId == OrgId, ct);
            if (partner is null || partner.IsDelete) continue;   // already gone: nothing to keep

            if (await CustomerUseAsync(uuid, ct) is { } use)
            {
                kept.Add(r);
                kept.Add(new(NoteType, $"{partner.SupplierName} is kept: {use}."));
                continue;
            }
            try
            {
                await _partners.DeleteAsync(uuid, UserId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The supplier reference checks refused it (a real PO, invoice, payment, GRN…), or the delete failed.
                kept.Add(r);
                kept.Add(new(NoteType, $"{partner.SupplierName} is kept: {ex.Message}"));
            }
        }
        return kept;
    }

    /// <summary>What still uses the partner as a customer, or null. Demo documents were removed before (reverse order).</summary>
    private async Task<string?> CustomerUseAsync(Guid uuid, CancellationToken ct)
    {
        var org = OrgId;
        if (_demand is not null)
        {
            if (await _demand.SaleOrders.IgnoreQueryFilters().Where(o => o.OrganizationId == org && o.PartnerId == uuid)
                    .Select(o => o.SoNumber).FirstOrDefaultAsync(ct) is { } so) return $"used by sale order {so}";
            if (await _demand.SaleQuotations.IgnoreQueryFilters().AnyAsync(q => q.OrganizationId == org && q.PartnerId == uuid, ct))
                return "used by a sale quotation";
            if (await _demand.SaleInquiries.IgnoreQueryFilters().AnyAsync(q => q.OrganizationId == org && q.PartnerId == uuid, ct))
                return "used by a sale inquiry";
        }
        if (_finance is not null)
        {
            if (await _finance.SalesInvoices.IgnoreQueryFilters().AnyAsync(i => i.OrganizationId == org && i.PartnerId == uuid, ct))
                return "used by a sales invoice";
            if (await _finance.CustomerPayments.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == org && p.PartnerId == uuid, ct))
                return "used by a customer payment";
        }
        return null;
    }
}

/// <summary>A50 D-28: ADD_CUSTOMER (a customer other than the walk-in one) and ADD_SUPPLIER (a vendor); demo records excluded.</summary>
internal sealed class SuppliersOnboardingSignal : IOnboardingSignal
{
    private readonly SuppliersDbContext _db;
    public SuppliersOnboardingSignal(SuppliersDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.AddCustomer, OnboardingStepCodes.AddSupplier];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var demo = demoEntityIds.ToList();
        var partners = _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && !p.IsDelete && !demo.Contains(p.UUID));
        var done = new HashSet<string>();
        if (await partners.AnyAsync(p => p.IsCustomer && !p.IsSystem, ct)) done.Add(OnboardingStepCodes.AddCustomer);
        if (await partners.AnyAsync(p => p.IsVendor, ct)) done.Add(OnboardingStepCodes.AddSupplier);
        return done;
    }
}
