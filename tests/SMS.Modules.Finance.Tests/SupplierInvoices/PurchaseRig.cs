using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

// SAP alignment, work package C (docs/finance/SAP-ALIGNMENT-PLAN.md): the supplier invoice's purchase tax
// code (S-3), three-way match on the Subtotal (S-8), the exchange-rate snapshot at approval (S-5), and
// reverse-don't-edit (S-7). The real repository and service over one in-memory database shared by Finance,
// Demand and Warehouse, with the SMS.Shared contracts Finance reads (tax codes, rates, base currency) faked.

internal sealed class FakeTaxCodes : ITaxCodeLookup
{
    public List<TaxCodeInfo> Codes { get; } = [];

    /// <summary>When set, what GetDefaultAsync answers whatever the side — a misconfigured default.</summary>
    public TaxCodeInfo? DefaultOverride { get; set; }

    public TaxCodeInfo Add(string code, decimal rate, string usage = TaxCodeUsage.Purchase, bool isDefault = false, bool active = true)
    {
        var info = new TaxCodeInfo(Guid.NewGuid(), code, $"{code} tax", rate, usage, isDefault, active);
        Codes.Add(info);
        return info;
    }

    public Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default) =>
        Task.FromResult(Codes.FirstOrDefault(c => c.Uuid == uuid));

    public Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaxCodeInfo>>(Codes.Where(c => c.IsActive && TaxCodeUsage.Allows(c.Usage, side)).ToList());

    public Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default) =>
        Task.FromResult(DefaultOverride ?? Codes.FirstOrDefault(c => c.IsDefault && c.IsActive && TaxCodeUsage.Allows(c.Usage, side)));
}

internal sealed class FakeRates : IExchangeRateProvider
{
    public Dictionary<(string From, string To), decimal> Rates { get; } = [];
    public List<(string From, string To, DateTime AsOf)> Asked { get; } = [];
    public Exception? Throw { get; set; }

    public Task<ExchangeRateQuote?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default)
    {
        Asked.Add((fromCurrencyCode, toCurrencyCode, asOf));
        if (Throw is not null) throw Throw;

        return Task.FromResult(Rates.TryGetValue((fromCurrencyCode, toCurrencyCode), out var rate)
            ? new ExchangeRateQuote(fromCurrencyCode, toCurrencyCode, rate, asOf.Date, false)
            : null);
    }
}

/// <summary>Tenancy's base currency (an id) and Lookups' id → code, as one fake.</summary>
internal sealed class FakeBaseCurrency : IOrganizationCurrencyService, ICurrencyCodeLookup
{
    private readonly Dictionary<Guid, string> _codes = [];
    public Guid? BaseId { get; private set; }

    public void Set(string? code)
    {
        if (code is null) { BaseId = null; return; }
        BaseId = Guid.NewGuid();
        _codes[BaseId.Value] = code;
    }

    public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) => Task.FromResult(BaseId);

    public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(_codes.TryGetValue(currencyId, out var code) ? code : null);
}

internal sealed class PurchaseRig
{
    public const int User = 7;

    public required Guid               Org;
    public required string             DbName;
    public required FinanceDbContext   Finance;
    public required DemandDbContext    Demand;
    public required WarehouseDbContext Warehouse;
    public required FakeTaxCodes       TaxCodes;
    public required FakeRates          Rates;
    public required FakeBaseCurrency   BaseCurrency;
    public required InvoiceRepository  Repo;
    public required InvoiceService     Service;
    public Guid Supplier { get; } = Guid.NewGuid();

    /// <param name="withLookups">False: a harness with none of the optional contracts registered (the old constructor).</param>
    public static PurchaseRig New(string? baseCurrency = "PKR", bool withLookups = true)
    {
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };

        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var taxCodes = new FakeTaxCodes();
        var rates    = new FakeRates();
        var currency = new FakeBaseCurrency();
        currency.Set(baseCurrency);

        var repo = withLookups
            ? new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup(),
                taxCodes, rates, currency, currency)
            : new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup());

        return new PurchaseRig
        {
            Org = org, DbName = dbName, Finance = finance, Demand = demand, Warehouse = warehouse,
            TaxCodes = taxCodes, Rates = rates, BaseCurrency = currency, Repo = repo,
            Service = new InvoiceService(repo, new Mock<IBackgroundJobClient>().Object)
        };
    }

    /// <summary>A purchase order, one line per (qty, price), received in full unless said otherwise.</summary>
    public async Task<(PurchaseOrder Po, List<PurchaseOrderLine> Lines)> PoAsync(bool received = true, params (decimal Qty, decimal Price)[] lines)
    {
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = $"PO-2026-{Random.Shared.Next(10000, 99999)}",
            SupplierId = Supplier, SupplierName = "Karachi Steel", Status = received ? "RECEIVED" : "SENT",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        for (var i = 0; i < lines.Length; i++)
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = i + 1, ItemDescription = $"PO item {i + 1}",
                Quantity = lines[i].Qty, UnitPrice = lines[i].Price, LineTotal = lines[i].Qty * lines[i].Price,
                QtyReceived = received ? lines[i].Qty : 0m
            });
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);

        Demand.PurchaseOrders.Add(po);
        await Demand.SaveChangesAsync();
        Demand.ChangeTracker.Clear();
        return (po, po.Lines.OrderBy(l => l.LineNo).ToList());
    }

    /// <summary>An approved GRN for the PO: the accepted quantity of each line, at the PO price.</summary>
    public async Task<Grn> GrnAsync(PurchaseOrder po, IReadOnlyList<PurchaseOrderLine> lines, params decimal[] accepted)
    {
        var grn = new Grn
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), GrnNumber = $"GRN-2026-{Random.Shared.Next(10000, 99999)}",
            PoUuid = po.UUID, PoNumber = po.PoNumber, SupplierId = po.SupplierId, SupplierName = po.SupplierName,
            WarehouseUuid = Guid.NewGuid(), ReceivedAt = new DateTime(2026, 9, 10), Status = "APPROVED",
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        for (var i = 0; i < lines.Count; i++)
            grn.Lines.Add(new GrnLine
            {
                UUID = Guid.NewGuid(), PoLineUuid = lines[i].UUID, LineNo = i + 1, ItemDescription = lines[i].ItemDescription,
                UnitOfMeasure = "PC", QtyOrdered = lines[i].Quantity, QtyReceived = accepted[i], QtyAccepted = accepted[i],
                QtyRejected = 0m, UnitCost = lines[i].UnitPrice
            });

        Warehouse.Grns.Add(grn);
        await Warehouse.SaveChangesAsync();
        Warehouse.ChangeTracker.Clear();
        return grn;
    }

    /// <summary>An invoice for the whole PO (one line per PO line, its full quantity), or a header-only one.</summary>
    public CreateInvoiceRequest Request(
        PurchaseOrder? po = null, IReadOnlyList<PurchaseOrderLine>? lines = null, decimal subtotal = 0m, decimal tax = 0m,
        Guid? taxCode = null, string currency = "PKR", Guid? grn = null) => new()
    {
        SupplierId = po?.SupplierId ?? Supplier, SupplierInvoiceNo = "KSW/881", PoUuid = po?.UUID, GrnUuid = grn,
        InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
        Currency = currency, Subtotal = subtotal, TaxAmount = tax, TaxCodeUuid = taxCode,
        Lines = lines is null ? null : [.. lines.Select(l => new InvoiceLineRequest
        {
            PoLineUuid = l.UUID, ItemDescription = l.ItemDescription, QtyInvoiced = l.Quantity, UnitPrice = l.UnitPrice
        })]
    };

    public async Task<Guid> CreateAsync(CreateInvoiceRequest request)
    {
        var uuid = await Service.CreateAsync(request, User);
        Forget();
        return uuid;
    }

    /// <summary>An approved invoice for a fresh, fully received PO of one or more (qty, price) lines.</summary>
    public async Task<(Guid Invoice, PurchaseOrder Po, List<PurchaseOrderLine> Lines)> ApprovedAsync(
        Guid? taxCode = null, decimal tax = 0m, params (decimal Qty, decimal Price)[] lines)
    {
        var (po, poLines) = await PoAsync(true, lines.Length == 0 ? [(10m, 100m)] : lines);
        var uuid = await CreateAsync(Request(po, poLines, tax: tax, taxCode: taxCode));
        (await Service.ApproveAsync(uuid, null, User)).Should().BeTrue();
        Forget();
        return (uuid, po, poLines);
    }

    public void Forget()
    {
        Finance.ChangeTracker.Clear();
        Demand.ChangeTracker.Clear();
        Warehouse.ChangeTracker.Clear();
    }

    public Task<Invoice> LoadAsync(Guid uuid) =>
        Finance.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == uuid);

    public Task<List<SupplierLedgerEntry>> LedgerAsync(Guid uuid) =>
        Finance.SupplierLedgerEntries.AsNoTracking().Where(e => e.ReferenceId == uuid).OrderBy(e => e.SequenceNo).ToListAsync();

    public Task<List<MasterFinancialLedger>> MasterAsync(Guid uuid) =>
        Finance.MasterFinancialLedgers.AsNoTracking().Where(e => e.ReferenceId == uuid).OrderBy(e => e.SequenceNo).ToListAsync();

    public Task<PurchaseOrder> PoNowAsync(Guid poUuid) =>
        Demand.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == poUuid);

    /// <summary>Changes a stored invoice directly — for states the API no longer allows (or another document made).</summary>
    public async Task TamperAsync(Guid uuid, Action<Invoice> change)
    {
        var invoice = await Finance.Invoices.SingleAsync(i => i.UUID == uuid);
        change(invoice);
        await Finance.SaveChangesAsync();
        Forget();
    }

    /// <summary>A multi-invoice supplier payment in <paramref name="status"/> with one line on the invoice.</summary>
    public async Task<SupplierPayment> SupplierPaymentAsync(Guid invoiceUuid, string status, decimal amount = 100m)
    {
        var invoice = await LoadAsync(invoiceUuid);
        var payment = new SupplierPayment
        {
            UUID = Guid.NewGuid(), PaymentNumber = $"SPAY-2026-{Random.Shared.Next(10000, 99999)}", SupplierId = invoice.SupplierId,
            SupplierName = invoice.SupplierName, PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = "CASH",
            TotalAmount = amount, Status = status, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        payment.Lines.Add(new SupplierPaymentLine
        {
            UUID = Guid.NewGuid(), InvoiceUuid = invoice.UUID, InvoiceNumber = invoice.InvoiceNumber,
            AllocatedAmount = amount, OutstandingBeforeAllocation = invoice.TotalAmount
        });
        Finance.SupplierPayments.Add(payment);
        await Finance.SaveChangesAsync();
        Forget();
        return payment;
    }

    /// <summary>A legacy single-invoice payment in <paramref name="status"/>.</summary>
    public async Task<Payment> LegacyPaymentAsync(Guid invoiceUuid, string status, decimal amount = 100m)
    {
        var invoice = await LoadAsync(invoiceUuid);
        var payment = new Payment
        {
            UUID = Guid.NewGuid(), PaymentNumber = $"PAY-2026-{Random.Shared.Next(10000, 99999)}", InvoiceId = invoice.Id,
            InvoiceUuid = invoice.UUID, SupplierId = invoice.SupplierId, SupplierName = invoice.SupplierName,
            PaymentDate = new DateTime(2026, 9, 20), AmountPaid = amount, PaymentMethod = "Cash", Status = status,
            ProcessedBy = 1, ProcessedAt = DateTime.UtcNow, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        Finance.Payments.Add(payment);
        await Finance.SaveChangesAsync();
        Forget();
        return payment;
    }

    /// <summary>A credit note deducted from the invoice (as CreditNoteRepository does: the total goes down).</summary>
    public async Task<CreditNote> CreditNoteAppliedAsync(Guid invoiceUuid, decimal amount)
    {
        var invoice = await Finance.Invoices.SingleAsync(i => i.UUID == invoiceUuid);
        invoice.TotalAmount -= amount;
        var note = new CreditNote
        {
            UUID = Guid.NewGuid(), CreditNoteNumber = $"CN-2026-{Random.Shared.Next(10000, 99999)}", SupplierCreditNoteNo = "S-CN-1",
            SroUuid = Guid.NewGuid(), SroNumber = "SRO-1", SupplierId = invoice.SupplierId, SupplierName = invoice.SupplierName,
            InvoiceUuid = invoice.UUID, InvoiceNumber = invoice.InvoiceNumber, CreditDate = new DateTime(2026, 9, 18), CreditAmount = amount,
            ApplicationStatus = "APPLIED_TO_INVOICE", AppliedToInvoiceUuid = invoice.UUID, AppliedToInvoiceNumber = invoice.InvoiceNumber,
            AppliedAt = DateTime.UtcNow, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        Finance.CreditNotes.Add(note);
        await Finance.SaveChangesAsync();
        Forget();
        return note;
    }
}
