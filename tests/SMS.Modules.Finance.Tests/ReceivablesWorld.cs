using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// The sales side wired the way production wires it — the real invoice service, payment service, ledger
/// and overdue job over one shared in-memory database — with only what lives in other modules faked:
/// deliveries (Logistics), customer names (Suppliers) and the currency catalog (Lookups). Every call a
/// test makes through a <see cref="ReceivablesDesk"/> is its own scope with its own contexts, as an HTTP
/// request is, so nothing is silently shared through a change tracker.
/// </summary>
internal sealed class ReceivablesWorld
{
    internal static readonly Guid PkrId = Guid.NewGuid();

    private readonly Dictionary<(Guid Org, Guid Delivery), DeliveryForInvoicing> _deliveries = [];
    private readonly Dictionary<Guid, (Guid Org, string Name)> _customers = [];
    private int _orders;
    private int _deliveriesMade;

    public string       DbName   { get; } = Guid.NewGuid().ToString();
    public TestClock    Clock    { get; } = new();
    public FakeVariants Variants { get; } = new();

    /// <summary>Everything handed to Hangfire — the sale-order timeline events.</summary>
    public List<Job> Jobs { get; } = [];

    public ReceivablesDesk For(Guid org) => new(this, org);

    /// <summary>
    /// The overdue job as Hangfire runs it: no organization of its own, so it sweeps every
    /// organization's invoices in one pass.
    /// </summary>
    public async Task<int> SweepOverdueAsync()
    {
        await using var db = Receivables.Auditor(DbName);
        return await new InvoiceOverdueJob(db, NullLogger<InvoiceOverdueJob>.Instance, Clock).SweepAsync();
    }

    internal Guid Register(Guid org, string name)
    {
        var id = Guid.NewGuid();
        _customers[id] = (org, name);
        return id;
    }

    internal Guid NewDelivery(Guid org, Guid orderUuid, IReadOnlyList<DeliveredLineForInvoicing> lines, string status)
    {
        var uuid   = Guid.NewGuid();
        var number = $"DLV-2026-{++_deliveriesMade:00000}";
        _deliveries[(org, uuid)] = new DeliveryForInvoicing(uuid, number, status, orderUuid, lines);
        return uuid;
    }

    internal int NextOrderNo() => ++_orders;

    /// <summary>The product a variant belongs to — one per variant, however many times it is asked.</summary>
    internal Guid ProductOf(Guid variant)
    {
        if (!Variants.Products.TryGetValue(variant, out var product))
            Variants.Products[variant] = product = Guid.NewGuid();
        return product;
    }

    internal DeliveryForInvoicing? DeliveryFor(Guid org, Guid uuid) =>
        _deliveries.GetValueOrDefault((org, uuid));

    /// <summary>What Suppliers' tenant-filtered lookup answers: a name only for the caller's own customers.</summary>
    internal Dictionary<Guid, string> NamesFor(Guid org, IReadOnlyList<Guid> ids) =>
        ids.Where(id => _customers.TryGetValue(id, out var c) && c.Org == org)
           .ToDictionary(id => id, id => _customers[id].Name);
}

/// <summary>One organization's view of the <see cref="ReceivablesWorld"/>.</summary>
internal sealed class ReceivablesDesk
{
    internal const int User = Receivables.User;

    private readonly ReceivablesWorld _world;

    internal ReceivablesDesk(ReceivablesWorld world, Guid org)
    {
        _world = world;
        Org    = org;
    }

    public Guid Org { get; }

    public ReceivablesScope Scope(IInterceptor? financeInterceptor = null) => new(_world, Org, financeInterceptor);

    public Guid NewCustomer(string name = "Acme Ltd") => _world.Register(Org, name);

    // ── Orders and deliveries ────────────────────────────────────────────────

    /// <param name="Cost">
    /// What one unit of this line's variant cost, as the product ledger holds it — half the price unless
    /// said otherwise, so a cost that is really the price shows.
    /// </param>
    /// <param name="Stocked">
    /// Whether the product ledger holds the line's delivered quantity at that cost, as it would after a
    /// GRN. A sale is only issued against stock the ledger can cost.
    /// </param>
    /// <param name="Variant">The variant the line is for; a new one when not given. Naming an existing one is how a variant is sold more than once.</param>
    public sealed record OrderLine(
        decimal Qty, decimal Price = 40m, decimal Discount = 0m, decimal Tax = 0m, decimal? Fulfilled = null,
        decimal? Cost = null, bool Stocked = true, Guid? Variant = null);

    public sealed record PlacedOrder(Guid Uuid, string Number, IReadOnlyList<Guid> LineUuids, IReadOnlyList<Guid> VariantUuids);

    public async Task<PlacedOrder> PlaceOrderAsync(Guid customer, params OrderLine[] lines)
    {
        await using var scope = Scope();

        var order = new SaleOrder
        {
            SoNumber = $"SO-2026-{_world.NextOrderNo():00000}", TraceId = Guid.NewGuid(), PartnerId = customer,
            OrderDate = new DateTime(2026, 9, 1), CurrencyId = ReceivablesWorld.PkrId, Status = "FULFILLED",
            DeliveryMode = "SHIP", CreatedBy = 1
        };
        foreach (var l in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = l.Variant ?? Guid.NewGuid(), Quantity = l.Qty, UnitPrice = l.Price,
                DiscountPercent = l.Discount, TaxPercent = l.Tax,
                LineTotal = SalesInvoiceTotals.LineTotal(l.Qty, l.Price, l.Discount, l.Tax),
                FulfilledQty = l.Fulfilled ?? l.Qty, FulfillmentMode = "IN_STOCK", Status = "FULFILLED"
            });

        scope.Demand.SaleOrders.Add(order);
        await scope.Demand.SaveChangesAsync();

        var saved = order.Lines.OrderBy(l => l.Id).ToList();

        for (var i = 0; i < lines.Length; i++)
        {
            var stock = lines[i].Fulfilled ?? lines[i].Qty;
            if (lines[i].Stocked && stock > 0m)
                await scope.ProductLedger.AppendEntryAsync(new ProductLedgerPosting(
                    saved[i].VariantUuid, "PURCHASE", "IN", stock, lines[i].Cost ?? Math.Round(lines[i].Price / 2m, 4),
                    "GRN", Guid.NewGuid(), $"GRN-{order.SoNumber}", User, ProductUuid: _world.ProductOf(saved[i].VariantUuid)));
        }

        return new PlacedOrder(order.UUID, order.SoNumber, [.. saved.Select(l => l.UUID)], [.. saved.Select(l => l.VariantUuid)]);
    }

    /// <summary>Goods received into the product ledger at a cost — what a GRN does — for a variant of no order in particular.</summary>
    public async Task StockAsync(Guid variant, decimal qty, decimal cost)
    {
        await using var scope = Scope();
        await scope.ProductLedger.AppendEntryAsync(new ProductLedgerPosting(
            variant, "PURCHASE", "IN", qty, cost, "GRN", Guid.NewGuid(), "GRN-STOCK", User, ProductUuid: _world.ProductOf(variant)));
    }

    /// <summary>Writes one entry straight to the product ledger, as a GRN, a return, an adjustment or a write-off would.</summary>
    public async Task<ProductLedgerPosted> AppendAsync(ProductLedgerPosting posting)
    {
        await using var scope = Scope();
        return await scope.ProductLedger.AppendEntryAsync(posting);
    }

    /// <summary>A variant's product ledger, in the order it was written.</summary>
    public async Task<List<ProductLedgerEntry>> ProductLedgerAsync(Guid variant)
    {
        await using var scope = Scope();
        return await scope.Db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variant).OrderBy(e => e.SequenceNo).ToListAsync();
    }

    /// <summary>Tells Logistics' side of the world about a delivery: (order line index, quantity delivered) pairs.</summary>
    public Guid Deliver(PlacedOrder order, string status = "DELIVERED", params (int Line, decimal Qty)[] lines)
    {
        var delivered = lines.Select((l, i) => new DeliveredLineForInvoicing(
            Guid.NewGuid(), i + 1, order.LineUuids[l.Line], Guid.NewGuid(), $"Item {l.Line + 1}", l.Qty)).ToList();

        return _world.NewDelivery(Org, order.Uuid, delivered, status);
    }

    // ── Invoicing ────────────────────────────────────────────────────────────

    public sealed record Invoiced(Guid Uuid, string Number, decimal Amount);

    /// <summary>A delivered order for one line of <paramref name="amount"/>, billed and issued — the customer now owes it.</summary>
    public async Task<Invoiced> InvoiceAsync(Guid customer, decimal amount)
    {
        var order    = await PlaceOrderAsync(customer, new OrderLine(1m, amount));
        var delivery = Deliver(order, "DELIVERED", (0, 1m));
        return await IssueDeliveryAsync(delivery);
    }

    public async Task<Invoiced> IssueDeliveryAsync(Guid deliveryUuid)
    {
        await using var scope = Scope();
        var created = await scope.Invoices.CreateFromFulfillmentAsync(deliveryUuid, User);
        var issued  = await scope.Invoices.IssueAsync(created.InvoiceUuid, User);
        return new Invoiced(issued.InvoiceUuid, issued.InvoiceNumber, issued.GrandTotal);
    }

    // ── Payments ─────────────────────────────────────────────────────────────

    public async Task<CustomerPaymentRecorded> PayAsync(
        Guid customer, decimal amount, string method = "BANK_TRANSFER", IReadOnlyList<ManualPaymentAllocation>? allocations = null,
        string? chequeNumber = null, string? notes = null)
    {
        await using var scope = Scope();
        return await scope.Payments.RecordPaymentAsync(customer, amount, method,
            new CustomerPaymentDetails("PKR",
                ChequeNumber: chequeNumber ?? (method == "CHEQUE" ? $"CHQ-{Guid.NewGuid().ToString("N")[..8]}" : null),
                Notes: notes, Allocations: allocations), User);
    }

    public async Task<CustomerPaymentBounced> BounceAsync(Guid paymentUuid, string? reason = null)
    {
        await using var scope = Scope();
        return await scope.Payments.BounceAsync(paymentUuid, reason, User);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<Books> BooksAsync(Guid customer)
    {
        await using var scope = Scope();

        return new Books(
            customer,
            await scope.Db.SalesInvoices.AsNoTracking().Where(i => i.PartnerId == customer && !i.IsDelete).OrderBy(i => i.Id).ToListAsync(),
            await scope.Db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).Where(p => p.PartnerId == customer).OrderBy(p => p.Id).ToListAsync(),
            await scope.Db.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == customer).OrderBy(e => e.SequenceNo).ToListAsync());
    }

    /// <summary>What the customer owes, by the ledger — its last running balance, or nothing before any entry.</summary>
    public async Task<decimal> OwesAsync(Guid customer) =>
        (await BooksAsync(customer)).Ledger.LastOrDefault()?.RunningBalance ?? 0m;
}

/// <summary>One request's worth of the sales side: fresh contexts, the real services on top of them.</summary>
internal sealed class ReceivablesScope : IAsyncDisposable
{
    internal ReceivablesScope(ReceivablesWorld world, Guid org, IInterceptor? financeInterceptor)
    {
        var tenant = new StaticTenantContext { OrganizationId = org };

        var financeOptions = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(world.DbName);
        if (financeInterceptor is not null) financeOptions.AddInterceptors(financeInterceptor);

        Db     = new FinanceDbContext(financeOptions.Options, tenant);
        Demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(world.DbName).Options, tenant);

        var reader = new Mock<IDeliveryFulfillmentReader>();
        reader.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((Guid id, CancellationToken _) => world.DeliveryFor(org, id));

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => world.NamesFor(org, ids));

        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(
        [
            new CurrencyModel { Id = ReceivablesWorld.PkrId, Name = "Pakistani Rupee", Code = "PKR" },
            new CurrencyModel { Id = Guid.NewGuid(), Name = "US Dollar", Code = "USD" }
        ]);

        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => world.Jobs.Add(job)).Returns("fake-job-id");

        Ledger        = new CustomerLedgerService(Db, world.Clock);
        ProductLedger = new ProductLedgerService(Db, world.Variants, world.Clock);
        Invoices      = new SalesInvoiceService(
            Db, Demand, reader.Object, Ledger, ProductLedger, names.Object, lookups.Object, jobs.Object,
            NullLogger<SalesInvoiceService>.Instance, world.Clock);
        Payments      = new CustomerPaymentService(Db, Ledger, names.Object, lookups.Object, world.Clock);
    }

    public FinanceDbContext       Db            { get; }
    public DemandDbContext        Demand        { get; }
    public CustomerLedgerService  Ledger        { get; }
    public ProductLedgerService   ProductLedger { get; }
    public SalesInvoiceService    Invoices      { get; }
    public CustomerPaymentService Payments      { get; }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Demand.DisposeAsync();
    }
}

/// <summary>A customer's receivables as committed: the invoices, the payments with their allocations, and the ledger.</summary>
internal sealed record Books(
    Guid Customer,
    IReadOnlyList<SalesInvoice> Invoices,
    IReadOnlyList<CustomerPayment> Payments,
    IReadOnlyList<CustomerLedgerEntry> Ledger)
{
    public SalesInvoice Invoice(Guid uuid) => Invoices.Single(i => i.UUID == uuid);
    public CustomerPayment Payment(Guid uuid) => Payments.Single(p => p.UUID == uuid);

    /// <summary>Money received and not applied to any invoice: it is on the customer's account.</summary>
    public decimal OnAccount => Payments
        .Where(p => p.Status == CustomerPaymentStatuses.Received)
        .Sum(p => p.Amount - p.Allocations.Sum(a => a.AllocatedAmount));

    public decimal Owing => Invoices.Where(i => i.Status != SalesInvoiceStatuses.Draft).Sum(i => i.BalanceDue);

    /// <summary>A value that changes if anything at all about the receivables changes, for "and nothing else moved".</summary>
    public string Fingerprint() => string.Join("|",
        Invoices.Select(i => $"I:{i.UUID}:{i.Status}:{i.AmountPaid}:{i.BalanceDue}:{i.ModifiedDate:O}")
            .Concat(Payments.Select(p => $"P:{p.UUID}:{p.Status}:{p.Notes}:{p.Allocations.Count}:{p.Allocations.Sum(a => a.AllocatedAmount)}:{p.ModifiedDate:O}"))
            .Concat(Ledger.Select(e => $"L:{e.SequenceNo}:{e.DebitAmount}:{e.CreditAmount}:{e.RunningBalance}")));
}

/// <summary>
/// What must hold of a customer's books after <i>any</i> sequence of operations. Returned as a list of
/// what is wrong so that a failing test can say exactly which rule broke, and where.
/// </summary>
internal static class ReceivablesInvariants
{
    public static IReadOnlyList<string> Violations(Books books, DateTime today, bool afterOverdueSweep = false)
    {
        var bad = new List<string>();

        // ── The ledger chains: each balance is the one before, plus debit, less credit ─────────────
        decimal running = 0m;
        var expectedSeq = 1;
        foreach (var e in books.Ledger)
        {
            if (e.SequenceNo != expectedSeq) bad.Add($"ledger sequence {e.SequenceNo} where {expectedSeq} was expected");
            expectedSeq++;

            if ((e.DebitAmount > 0m) == (e.CreditAmount > 0m))
                bad.Add($"ledger entry {e.SequenceNo} is not exactly one of a debit and a credit");

            running += e.DebitAmount - e.CreditAmount;
            if (e.RunningBalance != running)
                bad.Add($"ledger entry {e.SequenceNo} says the balance is {e.RunningBalance}, but the entries before it come to {running}");
        }

        // ── The ledger agrees with the documents ───────────────────────────────────────────────────
        // What the customer owes is everything billed, less every payment that is still good. A bounced
        // payment is a credit and its offsetting debit, and counts for nothing.
        var billed   = books.Invoices.Where(i => i.Status != SalesInvoiceStatuses.Draft).Sum(i => i.GrandTotal);
        var received = books.Payments.Where(p => p.Status == CustomerPaymentStatuses.Received).Sum(p => p.Amount);
        if (running != billed - received)
            bad.Add($"the ledger balance is {running}, but {billed} billed less {received} received is {billed - received}");

        // The same fact from the other side: what is owing on invoices, less what is held on account.
        if (running != books.Owing - books.OnAccount)
            bad.Add($"the ledger balance is {running}, but {books.Owing} owing on invoices less {books.OnAccount} on account is {books.Owing - books.OnAccount}");

        // ── Each invoice ───────────────────────────────────────────────────────────────────────────
        var live = books.Payments.Where(p => p.Status == CustomerPaymentStatuses.Received)
            .SelectMany(p => p.Allocations).ToList();

        foreach (var i in books.Invoices)
        {
            var paid = i.Status == SalesInvoiceStatuses.Draft ? 0m : live.Where(a => a.SalesInvoiceId == i.Id).Sum(a => a.AllocatedAmount);

            if (i.AmountPaid != paid)
                bad.Add($"{i.InvoiceNumber} has {i.AmountPaid} paid, but the payments still good add up to {paid}");
            if (i.BalanceDue != i.GrandTotal - i.AmountPaid)
                bad.Add($"{i.InvoiceNumber} owes {i.BalanceDue}, not {i.GrandTotal} less {i.AmountPaid}");
            if (i.AmountPaid < 0m || i.AmountPaid > i.GrandTotal)
                bad.Add($"{i.InvoiceNumber} has {i.AmountPaid} paid of {i.GrandTotal}");

            switch (i.Status)
            {
                case SalesInvoiceStatuses.Paid when i.BalanceDue != 0m:
                    bad.Add($"{i.InvoiceNumber} is PAID but owes {i.BalanceDue}"); break;
                case SalesInvoiceStatuses.PartiallyPaid when i.AmountPaid <= 0m || i.BalanceDue <= 0m:
                    bad.Add($"{i.InvoiceNumber} is PARTIALLY_PAID with {i.AmountPaid} paid and {i.BalanceDue} owing"); break;
                case SalesInvoiceStatuses.Issued when i.AmountPaid != 0m:
                    bad.Add($"{i.InvoiceNumber} is ISSUED with {i.AmountPaid} already paid"); break;
                case SalesInvoiceStatuses.Overdue when i.BalanceDue <= 0m || i.DueDate >= today:
                    bad.Add($"{i.InvoiceNumber} is OVERDUE but owes {i.BalanceDue} and falls due {i.DueDate:d}"); break;
            }

            if (i.Status != SalesInvoiceStatuses.Draft && i.Status != SalesInvoiceStatuses.Paid && i.BalanceDue == 0m)
                bad.Add($"{i.InvoiceNumber} owes nothing but is {i.Status}");

            if (afterOverdueSweep
                && (i.Status is SalesInvoiceStatuses.Issued or SalesInvoiceStatuses.PartiallyPaid)
                && i.DueDate < today && i.BalanceDue > 0m)
                bad.Add($"{i.InvoiceNumber} is {i.Status}, owes {i.BalanceDue} and was due {i.DueDate:d}: the sweep missed it");
        }

        // ── Each payment ───────────────────────────────────────────────────────────────────────────
        foreach (var p in books.Payments)
        {
            var applied = p.Allocations.Sum(a => a.AllocatedAmount);
            if (applied > p.Amount) bad.Add($"{p.PaymentNumber} of {p.Amount} has {applied} applied");

            var credits = books.Ledger.Count(e => e.ReferenceId == p.UUID && e.CreditAmount > 0m);
            var debits  = books.Ledger.Count(e => e.ReferenceId == p.UUID && e.DebitAmount > 0m);

            if (credits != 1) bad.Add($"{p.PaymentNumber} has {credits} ledger credits; it should have exactly one");

            var wantDebits = p.Status == CustomerPaymentStatuses.Bounced ? 1 : 0;
            if (debits != wantDebits) bad.Add($"{p.PaymentNumber} is {p.Status} and has {debits} ledger debits; it should have {wantDebits}");

            if (p.Status == CustomerPaymentStatuses.Bounced && p.PaymentMethod != CustomerPaymentMethods.Cheque)
                bad.Add($"{p.PaymentNumber} bounced, but it was not a cheque");
        }

        return bad;
    }
}
