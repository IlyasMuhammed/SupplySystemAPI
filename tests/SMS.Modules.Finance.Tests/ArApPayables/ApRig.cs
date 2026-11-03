using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.Posting;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Modules.Finance.Tests.SupplierInvoices;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>Suppliers' partner lookup, as the AP services read it: names, codes and (decrypted) bank details.</summary>
internal sealed class FakePartners : IPartnerLookupService
{
    public Dictionary<Guid, PartnerInfo> Partners { get; } = [];
    public Dictionary<Guid, PartnerBankInfo> Banks { get; } = [];

    public FakePartners Vendor(Guid uuid, string name, string code, string? bank = null, string? account = null)
    {
        Partners[uuid] = new PartnerInfo(uuid, code, name, false, true, true, null, null, null, null, null, null, 30, null);
        if (bank is not null) Banks[uuid] = new PartnerBankInfo(uuid, bank, account, $"PK00{account}", "HABBPKKA");
        return this;
    }

    public Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default) =>
        Task.FromResult(Partners.GetValueOrDefault(partnerUuid));

    public Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(Guid organizationId, IEnumerable<Guid> partnerUuids, string? role = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PartnerInfo>>(partnerUuids.Distinct().Where(Partners.ContainsKey).ToDictionary(u => u, u => Partners[u]));

    public Task<IReadOnlyList<PartnerInfo>> SearchAsync(Guid organizationId, string search, string? role = null, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PartnerInfo>>(Partners.Values.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default) =>
        Task.FromResult(Banks.GetValueOrDefault(partnerUuid));
}

/// <summary>
/// A48 AP — every AP service over one in-memory database (Finance, Demand and Warehouse share it, as in
/// <see cref="PurchaseRig"/>): the real invoice repository/service (with the payment-schedule hook), the real supplier
/// payment repository, the posting builders feeding a recording A44 gateway.
/// </summary>
internal sealed class ApRig
{
    public static readonly ApActor Actor = new(7, "Tess Tester");

    public required PurchaseRig               P;
    public required InvoiceService            Invoices;
    public required SupplierPaymentRepository PaymentsRepo;
    public required SupplierPaymentService    Payments;
    public required RecordingPostingGateway   Gateway;
    public required PaymentScheduleService    Schedules;
    public required InvoiceMatchingService    Matching;
    public required ToleranceRuleService      Rules;
    public required EarlyPaymentTermService   Terms;
    public required PaymentRunService         Runs;
    public required ApDebitMemoService        Memos;
    public required FakePartners              Partners;
    public required DateOnly                  Today;
    public required FixedClock                Clock;

    public FinanceDbContext Fin => P.Finance;
    public Guid Org => P.Org;

    public static ApRig New(DateOnly? today = null, IBackgroundJobClient? runJobs = null, SMS.Shared.Finance.Posting.IPostingRoleReadiness? roles = null)
    {
        var p        = PurchaseRig.New();
        var fin      = p.Finance;
        var ledger   = new SupplierLedgerService(fin);
        var jobs     = new Mock<IBackgroundJobClient>().Object;
        var gateway  = new RecordingPostingGateway();
        var qbo      = new RecordingQuickBooksGateway();
        var day      = today ?? new DateOnly(2026, 10, 1);
        var clock    = new FixedClock(day);

        var schedules = new PaymentScheduleService(fin, NullLogger<PaymentScheduleService>.Instance, clock: clock);
        var invoices  = new InvoiceService(p.Repo, jobs, null, schedules);
        var payRepo   = new SupplierPaymentRepository(fin, ledger, new Mock<INotificationService>().Object, roles: roles);
        var postings  = new SupplierPaymentPostings(fin, qbo, NullLogger<SupplierPaymentPostings>.Instance, gateway);
        var notePosts = new SupplierNotePostings(fin, qbo, NullLogger<SupplierNotePostings>.Instance, gateway);
        var payments  = new SupplierPaymentService(payRepo, jobs, postings, schedules);
        var partners  = new FakePartners();

        return new ApRig
        {
            P = p, Invoices = invoices, PaymentsRepo = payRepo, Payments = payments, Gateway = gateway, Schedules = schedules,
            Matching = new InvoiceMatchingService(fin, new MatchSourceReader(p.Demand, p.Warehouse), invoices),
            Rules    = new ToleranceRuleService(fin, partners, new FakeSupplierNameLookup()),
            Terms    = new EarlyPaymentTermService(fin),
            Runs     = new PaymentRunService(fin, schedules, payRepo, postings, null, partners, jobs: runJobs, clock: clock, roles: roles),
            Memos    = new ApDebitMemoService(fin, ledger, new FakeSupplierNameLookup(), notePosts, schedules),
            Partners = partners,
            Today    = day,
            Clock    = clock
        };
    }

    public void Forget() => P.Forget();

    private int _invoiceSeq;

    /// <summary>An approved supplier invoice with no purchase order (its schedule is created by the approval hook).</summary>
    public async Task<Guid> ApprovedInvoiceAsync(Guid vendor, decimal amount, DateOnly due, DateOnly? invoiceDate = null, string currency = "PKR")
    {
        var uuid = await Invoices.CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = vendor, SupplierInvoiceNo = $"V-{++_invoiceSeq:D4}", Currency = currency, Subtotal = amount,
            InvoiceDate = (invoiceDate ?? due.AddDays(-30)).ToDateTime(TimeOnly.MinValue), ReceivedDate = (invoiceDate ?? due.AddDays(-30)).ToDateTime(TimeOnly.MinValue),
            DueDate = due.ToDateTime(TimeOnly.MinValue)
        }, PurchaseRig.User);
        Forget();
        (await Invoices.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();
        Forget();
        return uuid;
    }

    public Task<PaymentSchedule> ScheduleAsync(Guid invoiceUuid) =>
        Fin.Set<PaymentSchedule>().AsNoTracking().SingleAsync(s => s.InvoiceUuid == invoiceUuid);

    public async Task<MatchToleranceRule> DefaultRuleAsync(decimal pricePct = 2m, bool autoApprove = false)
    {
        await Rules.CreateAsync(new ToleranceRuleRequest { Name = "DEFAULT", PriceTolerancePercent = pricePct, IsDefault = true, AutoApproveWithin = autoApprove }, Actor);
        Forget();
        return await Fin.Set<MatchToleranceRule>().AsNoTracking().SingleAsync(r => r.Name == "DEFAULT");
    }

    public async Task<EarlyPaymentTerm> TermAsync(string code = "2-10-N30", decimal pct = 2m, int days = 10, int net = 30)
    {
        var model = await Terms.CreateAsync(new EarlyPaymentTermRequest { Code = code, Name = code, DiscountPercent = pct, DiscountDays = days, NetDays = net }, Actor);
        Forget();
        return await Fin.Set<EarlyPaymentTerm>().AsNoTracking().SingleAsync(t => t.Uuid == model.Uuid);
    }

    /// <summary>A vendor profile (what CORE's profile service creates on first read) with AP preferences.</summary>
    public async Task VendorProfileAsync(Guid vendor, string priority = "Normal", string? method = null, int? termId = null)
    {
        Fin.Set<PartnerFinancialProfile>().Add(new PartnerFinancialProfile
        {
            OrganizationId = Org, PartnerUuid = vendor, PartnerRole = ArApEnums.PartnerRole.Vendor,
            PaymentPriority = priority, PreferredPaymentMethod = method, EarlyPaymentTermId = termId
        });
        await Fin.SaveChangesAsync();
        Forget();
    }

    public ProposePaymentRunRequest Proposal(DateOnly? cutoff = null, DateOnly? paymentDate = null) => new()
    {
        PaymentDate = paymentDate ?? Today, DueDateCutoff = cutoff ?? Today.AddDays(30), BankAccount = "HBL-0001-OPS"
    };

    public Task<List<SupplierLedgerEntry>> LedgerOfVendorAsync(Guid vendor) =>
        Fin.SupplierLedgerEntries.AsNoTracking().Where(e => e.SupplierId == vendor).OrderBy(e => e.SequenceNo).ToListAsync();
}

internal sealed class FixedClock : TimeProvider
{
    public FixedClock(DateOnly day) => Day = day;
    /// <summary>Settable: a test can let days pass (REV-4).</summary>
    public DateOnly Day { get; set; }
    public override DateTimeOffset GetUtcNow() => new(Day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
}
