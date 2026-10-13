using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Import;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.MultiCurrency;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.DataImport;

/// <summary>A40 (TFIN) — partners by code, as Suppliers' <c>ImportPartnerLookup</c> answers for one organization.</summary>
internal sealed class FakeImportPartners : IImportPartnerLookup
{
    private readonly Dictionary<Guid, List<ImportPartner>> _byOrg = [];

    public void Add(Guid org, ImportPartner partner)
    {
        if (!_byOrg.TryGetValue(org, out var list)) _byOrg[org] = list = [];
        list.Add(partner);
    }

    public Task<IReadOnlyDictionary<string, ImportPartner>> FindByCodesAsync(Guid organizationId, IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        var wanted = codes.Select(c => c.ToUpperInvariant()).ToHashSet();
        IReadOnlyDictionary<string, ImportPartner> result = _byOrg.GetValueOrDefault(organizationId, [])
            .Where(p => wanted.Contains(p.Code.ToUpperInvariant()))
            .ToDictionary(p => p.Code.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<string>> ListCodesAsync(Guid organizationId, bool customers, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_byOrg.GetValueOrDefault(organizationId, [])
            .Where(p => p.IsActive && (customers ? p.IsCustomer : p.IsVendor)).Select(p => p.Code).OrderBy(c => c).Take(max).ToList());
}

/// <summary>
/// A40 (TFIN) — Finance's import targets over the receivables world (real invoice, payment and ledger services on one in-memory
/// database); only other modules are faked: partners by code (Suppliers), currencies and rates (a fake CUR service: PKR base,
/// USD at 280 from 2026-01-01), the org's currencies (PKR, USD active; EUR inactive). Each call is its own scope.
/// </summary>
internal sealed class FinanceImportRig
{
    internal const int User = Receivables.User;
    internal static readonly Guid Usd = Guid.Parse("00000000-0000-0000-0000-0000000a0002");
    internal static readonly Guid Eur = Guid.Parse("00000000-0000-0000-0000-0000000a0003");

    public ReceivablesWorld   World    { get; } = new();
    public ReceivablesDesk    Desk     { get; }
    public FakeImportPartners Partners { get; } = new();
    public FakeCurrencyService Currency { get; } =
        new FakeCurrencyService(ReceivablesWorld.PkrId, "PKR").Currency(Usd, "USD").Currency(Eur, "EUR")
            .Rate(Usd, 280m, new DateOnly(2026, 1, 1));

    public FinanceImportRig()
    {
        Desk = World.For(Guid.NewGuid());
        using var db = Db();
        db.OrgCurrencies.AddRange(
            new OrgCurrency { OrganizationId = Org, CurrencyId = ReceivablesWorld.PkrId, Code = "PKR", Name = "Rupee", Symbol = "Rs", DisplayOrder = 1 },
            new OrgCurrency { OrganizationId = Org, CurrencyId = Usd, Code = "USD", Name = "Dollar", Symbol = "$", DisplayOrder = 2 },
            new OrgCurrency { OrganizationId = Org, CurrencyId = Eur, Code = "EUR", Name = "Euro", Symbol = "E", DisplayOrder = 3, IsActive = false });
        db.SaveChanges();
    }

    public Guid Org => Desk.Org;

    public FinanceDbContext Db() => Receivables.Db(Org, World.DbName);

    public Guid Customer(string code, string name = "Acme Ltd", int terms = 0, bool active = true, bool isCustomer = true)
    {
        var uuid = Desk.NewCustomer(name);
        Partners.Add(Org, new ImportPartner(uuid, code, name, isCustomer, !isCustomer, active, terms));
        return uuid;
    }

    public Guid Supplier(string code, string name = "Steel Traders", bool isVendor = true)
    {
        var uuid = Guid.NewGuid();
        Partners.Add(Org, new ImportPartner(uuid, code, name, !isVendor, isVendor, true, 0));
        return uuid;
    }

    public async Task SetOpeningDateAsync(string date)
    {
        await using var db = Db();
        await new OpeningBalanceDateService(db, World.Clock).SetAsync(DateOnly.Parse(date), User);
    }

    public async Task<T> CustomerTargetAsync<T>(Func<CustomerOpeningBalanceImportTarget, Task<T>> call)
    {
        await using var scope = Desk.Scope();
        var target = new CustomerOpeningBalanceImportTarget(
            scope.Db, scope.Ledger, new OpeningBalanceDateService(scope.Db, World.Clock), Currency, new OrgCurrencyLookup(scope.Db),
            Partners, World.Clock);
        return await call(target);
    }

    public async Task<T> SupplierTargetAsync<T>(Func<SupplierOpeningBalanceImportTarget, Task<T>> call)
    {
        await using var db = Db();
        var target = new SupplierOpeningBalanceImportTarget(
            db, new OpeningBalanceDateService(db, World.Clock), Currency, new OrgCurrencyLookup(db), Partners, World.Clock);
        return await call(target);
    }

    public async Task<T> TaxTargetAsync<T>(Func<TaxCodeImportTarget, Task<T>> call)
    {
        await using var db = Db();
        return await call(new TaxCodeImportTarget(db, World.Clock));
    }

    public static ImportRowInput Input(int row, params (string Field, string? Value)[] values) =>
        new(row, values.ToDictionary(v => v.Field, v => v.Value), null);

    public static ImportApplyRow Create(int row, params (string Field, string? Value)[] values) =>
        new(row, values.ToDictionary(v => v.Field, v => v.Value), "CREATE", null);

    public static ImportApplyRow Update(int row, Guid existing, params (string Field, string? Value)[] values) =>
        new(row, values.ToDictionary(v => v.Field, v => v.Value), "UPDATE", existing);

    public static ImportApplyRow AsCreate(ImportRowInput input) => new(input.RowNumber, input.Values, "CREATE", null);

    /// <summary>What R3 (aging) says the customer owes, as of the world's clock.</summary>
    public async Task<decimal> AgingAsync(Guid customer)
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => World.NamesFor(Org, ids));
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        var report = await new ReceivablesReportService(Db(), names.Object, templates.Object, World.Clock)
            .GetAgingReceivablesAsync(new AgingReceivablesFilter { PartnerId = customer });
        return report.Invoices.Sum(i => i.Outstanding);
    }

    /// <summary>The query SalesInvoiceCustomerBalanceLookup (Suppliers) makes: open invoices' balance due.</summary>
    public async Task<decimal> OpenBalanceAsync(Guid customer)
    {
        await using var db = Db();
        return await db.SalesInvoices.AsNoTracking()
            .Where(i => i.PartnerId == customer && !i.IsDelete
                     && (i.Status == "ISSUED" || i.Status == "PARTIALLY_PAID" || i.Status == "OVERDUE"))
            .SumAsync(i => i.BalanceDue);
    }

    public SalesAnalysisReportService SalesReports()
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => World.NamesFor(Org, ids));
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
        return new SalesAnalysisReportService(Db(), names.Object, World.Variants, templates.Object, World.Clock);
    }

    /// <summary>A supplier payment created, approved and posted against one invoice, as the payments screen does.</summary>
    public async Task PaySupplierAsync(Guid supplier, Guid invoiceUuid, decimal amount)
    {
        await using var db = Db();
        var repo = new SupplierPaymentRepository(db, new SupplierLedgerService(db), new Mock<INotificationService>().Object);
        var uuid = await repo.CreateAsync(new SMS.Modules.Finance.Models.CreateSupplierPaymentRequest
        {
            SupplierId    = supplier,
            SupplierName  = "Steel Traders",
            PaymentDate   = World.Clock.Value,
            PaymentMethod = "CASH",
            TotalAmount   = amount,
            Lines         = [new SMS.Modules.Finance.Models.CreateSupplierPaymentLineRequest { InvoiceUuid = invoiceUuid, AllocatedAmount = amount }]
        }, createdBy: User);
        await repo.ApproveAsync(uuid, approvedBy: User);
        await repo.PostAsync(uuid, postedBy: User);
    }
}
