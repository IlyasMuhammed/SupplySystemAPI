using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A tenant's receivables books — invoices, payments and the customer ledger — written the way Finance
/// writes them (an invoice's ledger debit at its issue, a payment's credit at its payment date, a bounce as
/// a new debit) and the report service that reads them. In-memory, so it shows what the service decides;
/// that the queries also translate to SQL is the SQL Server tests' job, and that they agree with the real
/// invoice and payment services is the reconciliation tests' in Finance.Tests.
/// </summary>
internal sealed class ReceivablesReportWorld
{
    internal static readonly Guid Org      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid OtherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static readonly Guid AcmeId   = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid GlobexId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    internal readonly string DatabaseName = Guid.NewGuid().ToString();

    internal Guid Acme   => AcmeId;
    internal Guid Globex => GlobexId;

    /// <summary>What the customer lookup knows. A customer absent from it has no name to show.</summary>
    internal readonly Dictionary<Guid, string> Names = new() { [AcmeId] = "Acme Ltd", [GlobexId] = "Globex Corp" };

    internal string? CompanyName { get; set; } = "Northwind Trading";

    /// <summary>What "now" is for the service: the as-of date when none is given, and when the report says it was generated.</summary>
    internal DateTime Now { get; set; } = new DateTime(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private int _sequence;

    /// <summary>Set to point the world at another store, such as a real SQL Server database, instead of the in-memory one.</summary>
    internal Func<Guid, FinanceDbContext>? ContextFactory { get; set; }

    internal FinanceDbContext Db(Guid? org = null) =>
        ContextFactory?.Invoke(org ?? Org)
        ?? new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(DatabaseName).Options,
            new StaticTenantContext { OrganizationId = org ?? Org });

    internal ReceivablesReportService Service(Guid? org = null, Mock<ISupplierNameLookupService>? names = null) =>
        Service(Db(org), names);

    internal ReceivablesReportService Service(FinanceDbContext db, Mock<ISupplierNameLookupService>? names = null)
    {
        names ??= new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new ReceivablesReportService(db, names.Object, templates.Object, new MovableClock(this));
    }

    private sealed class MovableClock(ReceivablesReportWorld world) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(world.Now, DateTimeKind.Utc));
    }

    // ── Ledger ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends an entry to the customer's ledger the way <c>CustomerLedgerService</c> does: the next
    /// sequence number, and a running balance that chains from the last entry <i>posted</i>, whatever
    /// business date either one carries.
    /// </summary>
    internal CustomerLedgerEntry Ledger(
        Guid partner, string type, DateTime date, decimal debit, decimal credit, string currency = "PKR",
        string? referenceNumber = null, string referenceType = "Manual", Guid? referenceId = null, string? narration = null, Guid? org = null)
    {
        using var db = Db(org);
        var entry = NewEntry(db, partner, type, date, debit, credit, currency, referenceNumber, referenceType, referenceId, narration, org ?? Org);
        db.CustomerLedgerEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private CustomerLedgerEntry NewEntry(
        FinanceDbContext db, Guid partner, string type, DateTime date, decimal debit, decimal credit, string currency,
        string? referenceNumber, string referenceType, Guid? referenceId, string? narration, Guid org)
    {
        var last = db.CustomerLedgerEntries.Where(e => e.PartnerId == partner).OrderByDescending(e => e.SequenceNo).FirstOrDefault();
        var n    = Interlocked.Increment(ref _sequence);

        return new CustomerLedgerEntry
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = org,
            PartnerId       = partner,
            SequenceNo      = (last?.SequenceNo ?? 0) + 1,
            EntryDate       = date,
            EntryType       = type,
            ReferenceType   = referenceType,
            ReferenceId     = referenceId ?? Guid.NewGuid(),
            ReferenceNumber = referenceNumber ?? $"REF-{n:00000}",
            DebitAmount     = debit,
            CreditAmount    = credit,
            RunningBalance  = (last?.RunningBalance ?? 0m) + debit - credit,
            CurrencyCode    = currency,
            Narration       = narration,
            CreatedBy       = 1,
            CreatedDate     = date
        };
    }

    /// <summary>Adds many small debits in one save, for the volume tests: one a minute from the start, each posted after the last.</summary>
    internal void BulkLedger(Guid partner, int count, DateTime start)
    {
        using var db = Db();
        var running = 0m;
        for (var i = 0; i < count; i++)
        {
            running += 1m;
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, PartnerId = partner, SequenceNo = i + 1, EntryDate = start.AddMinutes(i),
                EntryType = "INVOICE", ReferenceType = "SalesInvoice", ReferenceId = Guid.NewGuid(), ReferenceNumber = $"SINV-{i:00000}",
                DebitAmount = 1m, CreditAmount = 0m, RunningBalance = running, CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = start
            });
        }
        db.SaveChanges();
    }

    // ── Invoices and payments ────────────────────────────────────────────────

    /// <summary>
    /// A sales invoice issued on <paramref name="issuedOn"/>: the invoice, dated then and due
    /// <paramref name="termDays"/> later, and the ledger debit that books it. Pass an earlier
    /// <paramref name="invoiceDate"/> for a draft that sat before it was issued.
    /// </summary>
    internal SalesInvoice Invoice(
        Guid partner, string number, DateTime issuedOn, decimal grand, int termDays = 30, string currency = "PKR",
        string status = "ISSUED", DateTime? invoiceDate = null, DateTime? dueDate = null, bool deleted = false, Guid? org = null, bool booked = true)
    {
        var orgId = org ?? Org;
        var date  = (invoiceDate ?? issuedOn).Date;

        var invoice = new SalesInvoice
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = orgId,
            TraceId         = Guid.NewGuid(),
            InvoiceNumber   = number,
            SaleOrderUuid   = Guid.NewGuid(),
            SaleOrderNumber = $"SO-{number}",
            PartnerId       = partner,
            PartnerName     = Names.GetValueOrDefault(partner, "Unknown"),
            InvoiceDate     = date,
            DueDate         = dueDate ?? date.AddDays(termDays),
            Subtotal        = grand,
            GrandTotal      = grand,
            AmountPaid      = 0m,
            BalanceDue      = grand,
            Status          = status,
            CurrencyCode    = currency,
            IsDelete        = deleted,
            CreatedBy       = 1,
            CreatedDate     = date
        };

        using var db = Db(orgId);
        db.SalesInvoices.Add(invoice);
        if (booked)
            db.CustomerLedgerEntries.Add(NewEntry(db, partner, "INVOICE", issuedOn, grand, 0m, currency, number, "SalesInvoice", invoice.UUID,
                $"Invoice {number}", orgId));
        db.SaveChanges();
        return invoice;
    }

    /// <summary>
    /// Money received on <paramref name="paymentDate"/> and applied to invoices as <paramref name="allocations"/> says,
    /// keyed in at <paramref name="keyedAt"/> (the payment date unless it was back-dated): the payment, its
    /// allocations, the invoices' new balances and the ledger credit dated the payment date.
    /// </summary>
    internal CustomerPayment Payment(
        Guid partner, string number, DateTime paymentDate, decimal amount, (SalesInvoice Invoice, decimal Amount)[] allocations,
        string currency = "PKR", string method = "BANK_TRANSFER", DateTime? keyedAt = null, Guid? org = null)
    {
        var orgId = org ?? Org;
        var keyed = keyedAt ?? paymentDate;

        var payment = new CustomerPayment
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = orgId,
            PartnerId      = partner,
            PartnerName    = Names.GetValueOrDefault(partner, "Unknown"),
            PaymentNumber  = number,
            PaymentDate    = paymentDate,
            Amount         = amount,
            PaymentMethod  = method,
            CurrencyCode   = currency,
            Status         = "RECEIVED",
            CreatedBy      = 1,
            CreatedDate    = keyed
        };

        using var db = Db(orgId);

        foreach (var (invoice, applied) in allocations)
        {
            payment.Allocations.Add(new PaymentAllocation
            {
                UUID = Guid.NewGuid(), OrganizationId = orgId, SalesInvoiceId = invoice.Id, AllocatedAmount = applied, AllocatedAt = keyed, AllocatedBy = 1
            });

            var stored = db.SalesInvoices.Single(i => i.Id == invoice.Id);
            stored.AmountPaid += applied;
            stored.BalanceDue -= applied;
            if (stored.Status is "ISSUED" or "PARTIALLY_PAID" or "OVERDUE")
                stored.Status = stored.BalanceDue <= 0m ? "PAID" : "PARTIALLY_PAID";
        }

        db.CustomerPayments.Add(payment);
        db.CustomerLedgerEntries.Add(NewEntry(db, partner, "PAYMENT", paymentDate, 0m, amount, currency, number, "CustomerPayment", payment.UUID,
            $"Payment {number}", orgId));
        db.SaveChanges();
        return payment;
    }

    /// <summary>A cheque coming back on <paramref name="bouncedOn"/>: its allocations no longer apply, and the ledger debits the money again.</summary>
    internal void Bounce(CustomerPayment payment, DateTime bouncedOn)
    {
        using var db = Db(payment.OrganizationId);

        var stored = db.CustomerPayments.Include(p => p.Allocations).Single(p => p.Id == payment.Id);
        foreach (var allocation in stored.Allocations)
        {
            var invoice = db.SalesInvoices.Single(i => i.Id == allocation.SalesInvoiceId);
            invoice.AmountPaid -= allocation.AllocatedAmount;
            invoice.BalanceDue += allocation.AllocatedAmount;
            if (invoice.Status is "PAID" or "PARTIALLY_PAID")
                invoice.Status = invoice.AmountPaid > 0m ? "PARTIALLY_PAID" : "ISSUED";
        }

        stored.Status = "BOUNCED";
        db.CustomerLedgerEntries.Add(NewEntry(db, stored.PartnerId, "PAYMENT", bouncedOn, stored.Amount, 0m, stored.CurrencyCode,
            stored.PaymentNumber, "CustomerPayment", stored.UUID, $"Bounced {stored.PaymentNumber}", stored.OrganizationId));
        db.SaveChanges();
    }

    /// <summary>Sets a payment's status without any ledger consequence, for statuses no flow sets yet.</summary>
    internal void SetPaymentStatus(CustomerPayment payment, string status)
    {
        using var db = Db(payment.OrganizationId);
        db.CustomerPayments.Single(p => p.Id == payment.Id).Status = status;
        db.SaveChanges();
    }

    internal sealed record Bill(string Number, Guid Partner, DateTime IssuedOn, DateTime Due, decimal Grand, string Currency);
    internal sealed record Receipt(DateTime Date, DateTime? BouncedOn, IReadOnlyList<(string Invoice, decimal Amount)> Applied);

    /// <summary>
    /// Thirty invoices for two customers in two currencies, issued between 1 July and 15 September, and
    /// twenty payments spread over them — some back-dated, some part-payments of two invoices, a third of
    /// them cheques that bounced. What the aging must say on any day is worked out from these lists alone.
    /// </summary>
    internal (List<Bill> Bills, List<Receipt> Receipts) SeedScatter()
    {
        var random   = new Random(20260920);
        var bills    = new List<Bill>();
        var receipts = new List<Receipt>();
        var remaining = new Dictionary<string, decimal>();
        var made      = new Dictionary<string, SalesInvoice>();

        for (var i = 1; i <= 30; i++)
        {
            var partner  = random.Next(0, 2) == 0 ? Acme : Globex;
            var currency = random.Next(0, 4) == 0 ? "USD" : "PKR";
            var issued   = new DateTime(2026, 7, 1).AddDays(random.Next(0, 77)).AddHours(random.Next(0, 24));
            var grand    = random.Next(100, 5000) + random.Next(0, 100) / 100m;
            var bill     = new Bill($"SINV-{i:00}", partner, issued, issued.Date.AddDays(random.Next(15, 61)), grand, currency);

            bills.Add(bill);
            remaining[bill.Number] = grand;
            made[bill.Number] = Invoice(partner, bill.Number, issued, grand, currency: currency, dueDate: bill.Due);
        }

        for (var p = 1; p <= 20; p++)
        {
            var first = bills[random.Next(0, bills.Count)];
            var mates = bills.Where(b => b.Partner == first.Partner && b.Currency == first.Currency && remaining[b.Number] > 1m).Take(2).ToList();
            if (mates.Count == 0) continue;

            var applied = mates.Select(b => (b.Number, Amount: Math.Round(remaining[b.Number] * (random.Next(30, 101) / 100m), 2))).ToList();
            foreach (var (number, amount) in applied) remaining[number] -= amount;

            var latestIssue = mates.Max(b => b.IssuedOn).Date;
            var paidOn      = latestIssue.AddDays(random.Next(0, 40));
            var cheque      = random.Next(0, 3) == 0;

            var payment = Payment(first.Partner, $"CPAY-{p:00}", paidOn, applied.Sum(a => a.Amount),
                [.. applied.Select(a => (made[a.Number], a.Amount))], currency: first.Currency, method: cheque ? "CHEQUE" : "BANK_TRANSFER",
                keyedAt: paidOn.AddDays(random.Next(0, 6)));

            DateTime? bounced = null;
            if (cheque)
            {
                bounced = paidOn.AddDays(random.Next(1, 20));
                Bounce(payment, bounced.Value);
            }

            receipts.Add(new Receipt(paidOn, bounced, applied));
        }

        return (bills, receipts);
    }

    // ── Sales analysis (R4, R5) ──────────────────────────────────────────────

    /// <summary>What the variant lookup knows: a variant's product and names. A variant absent from it has no name to show.</summary>
    internal readonly Dictionary<Guid, VariantDescription> Variants = new();

    internal SalesAnalysisReportService AnalysisService(
        Guid? org = null, Mock<IProductVariantResolver>? resolver = null, Mock<ISupplierNameLookupService>? names = null) =>
        AnalysisService(Db(org), resolver, names);

    internal SalesAnalysisReportService AnalysisService(
        FinanceDbContext db, Mock<IProductVariantResolver>? resolver = null, Mock<ISupplierNameLookupService>? names = null)
    {
        names ??= new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        resolver ??= new Mock<IProductVariantResolver>();
        resolver.Setup(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                    (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(Variants.ContainsKey).ToDictionary(id => id, id => Variants[id]));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new SalesAnalysisReportService(db, names.Object, resolver.Object, templates.Object, new MovableClock(this));
    }

    /// <summary>
    /// A variant that came into stock as <paramref name="product"/>: the product ledger has an entry for it,
    /// which is how the books know what product a variant is a variant of, and the lookup can name it.
    /// </summary>
    internal void Stock(Guid variant, Guid product, string productName, Guid? org = null)
    {
        Variants[variant] = new VariantDescription(variant, product, "SKU-" + variant.ToString("N")[..6], "Default", productName, true, "PCS");

        using var db = Db(org);
        db.ProductLedgerEntries.Add(new ProductLedgerEntry
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, VariantUuid = variant, ProductUuid = product, SequenceNo = 1,
            EntryDate = new DateTime(2026, 1, 1), EntryType = "PURCHASE", ReferenceType = "GRN", ReferenceId = Guid.NewGuid(),
            ReferenceNumber = "GRN-1", Direction = "IN", Quantity = 1000m, UnitCost = 1m, TotalCost = 1000m,
            RunningQty = 1000m, RunningValue = 1000m, CreatedBy = 1, CreatedDate = new DateTime(2026, 1, 1)
        });
        db.SaveChanges();
    }

    internal sealed record SoldLine(Guid Variant, decimal Quantity, decimal UnitPrice, decimal DiscountPercent = 0m, decimal TaxPercent = 0m);

    /// <summary>
    /// A sales invoice with lines, issued on <paramref name="issuedOn"/>: the header totals worked out from the
    /// lines the way Finance works them out, and the ledger debit that books it. One order can be billed in
    /// several invoices by passing the same <paramref name="saleOrder"/>.
    /// </summary>
    internal SalesInvoice Sale(
        Guid partner, string number, DateTime issuedOn, IReadOnlyList<SoldLine> lines, string currency = "PKR",
        string status = "ISSUED", Guid? saleOrder = null, bool booked = true, bool deleted = false, Guid? org = null)
    {
        var orgId = org ?? Org;
        var (subtotal, discount, tax, grand) = SalesInvoiceTotals.Header(lines.Select(l => (l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent)));
        var date = issuedOn.Date;

        var invoice = new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = orgId, TraceId = Guid.NewGuid(), InvoiceNumber = number,
            SaleOrderUuid = saleOrder ?? Guid.NewGuid(), SaleOrderNumber = $"SO-{number}", PartnerId = partner,
            PartnerName = Names.GetValueOrDefault(partner, "Unknown"), InvoiceDate = date, DueDate = date.AddDays(30),
            Subtotal = subtotal, DiscountAmount = discount, TaxAmount = tax, GrandTotal = grand, AmountPaid = 0m, BalanceDue = grand,
            Status = status, CurrencyCode = currency, IsDelete = deleted, CreatedBy = 1, CreatedDate = date
        };

        var lineNo = 0;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                UUID = Guid.NewGuid(), OrganizationId = orgId, LineNo = ++lineNo, SoLineUuid = Guid.NewGuid(), VariantUuid = l.Variant,
                Description = "Item", Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent, LineTotal = SalesInvoiceTotals.LineTotal(l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent)
            });

        using var db = Db(orgId);
        db.SalesInvoices.Add(invoice);
        if (booked)
            db.CustomerLedgerEntries.Add(NewEntry(db, partner, "INVOICE", issuedOn, grand, 0m, currency, number, "SalesInvoice", invoice.UUID,
                $"Invoice {number}", orgId));
        db.SaveChanges();
        return invoice;
    }

    internal static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// Fifty invoices from a fixed pseudo-random hand: three customers (one the lookup does not know), two
    /// currencies, every status, some never issued and some deleted, some another organization's, one to four
    /// lines each of variants that belong to four products, two that the product ledger never saw (one the
    /// lookup can name and one it cannot), quarter-unit quantities, discounts and tax, and pairs of
    /// invoices sharing a sale order. Identical in every world that seeds it, so two worlds can be compared.
    /// </summary>
    internal void SeedSales()
    {
        var random   = new Random(20260921);
        var variants = Enumerable.Range(1, 7).Select(i => Guid.Parse($"d0000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var products = Enumerable.Range(1, 4).Select(i => Guid.Parse($"e0000000-0000-0000-0000-{i:000000000000}")).ToArray();

        // v1 and v2 are two variants of the first product; v3, v4 and v5 are one product each.
        Stock(variants[0], products[0], "Laptop");
        Stock(variants[1], products[0], "Laptop");
        Stock(variants[2], products[1], "Mouse");
        Stock(variants[3], products[2], "Cable");
        Stock(variants[4], products[3], "Dock");
        Stock(variants[0], products[0], "Laptop", org: OtherOrg);
        Variants[variants[5]] = new VariantDescription(variants[5], Guid.Parse("e0000000-0000-0000-0000-0000000000ff"), "SKU-6", "Default", "Gadget", true, "PCS");

        string[] statuses = ["ISSUED", "PARTIALLY_PAID", "PAID", "OVERDUE", "ISSUED", "DRAFT", "CANCELLED", "CREDIT_NOTE", "ISSUED"];
        Guid[]   partners = [Acme, Globex, Stranger];
        Guid[]   orders   = [.. Enumerable.Range(0, 30).Select(_ => Guid.NewGuid())];
        decimal[] discounts = [0m, 5m, 12.5m];
        decimal[] taxes     = [0m, 17m];

        for (var i = 0; i < 50; i++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new SoldLine(variants[random.Next(0, variants.Length)], random.Next(1, 40) / 4m, random.Next(100, 90000) / 100m,
                                           discounts[random.Next(discounts.Length)], taxes[random.Next(taxes.Length)]))
                .ToList();

            Sale(partners[i % 3], $"SINV-2026-{i + 1:00000}",
                new DateTime(2026, 8, 1).AddDays(random.Next(0, 60)).AddHours(random.Next(0, 24)).AddMinutes(random.Next(0, 60)),
                lines,
                currency: i % 7 == 0 ? "USD" : "PKR",
                status: statuses[i % 9],
                saleOrder: orders[i / 2 % orders.Length],
                booked: i % 11 != 10,
                deleted: i % 13 == 12,
                org: i % 17 == 15 ? OtherOrg : null);
        }
    }

    // ── Product ledger (R9, R10) ─────────────────────────────────────────────

    /// <summary>The variant that stands in for a product, for a product with nothing on the ledger yet.</summary>
    internal readonly Dictionary<Guid, DefaultVariantResult> DefaultVariants = new();

    private int _moves;

    /// <summary>A variant Inventory can name, as a variant of <paramref name="product"/>. The default one is what the product is looked up by.</summary>
    internal void Catalog(Guid variant, Guid product, string productName, string? sku = null, string variantName = "Default", bool isDefault = true)
    {
        sku ??= "SKU-" + variant.ToString("N")[..6];
        Variants[variant] = new VariantDescription(variant, product, sku, variantName, productName, isDefault, "PCS");
        if (isDefault) DefaultVariants[product] = new DefaultVariantResult(product, variant, sku, variantName);
    }

    internal ProductLedgerReportService LedgerService(Guid? org = null, Mock<IProductVariantResolver>? resolver = null, Mock<ISupplierNameLookupService>? names = null) => LedgerService(Db(org), resolver, names);

    internal ProductLedgerReportService LedgerService(FinanceDbContext db, Mock<IProductVariantResolver>? resolver = null, Mock<ISupplierNameLookupService>? names = null)
    {
        names ??= new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        resolver ??= new Mock<IProductVariantResolver>();
        resolver.Setup(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                    (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(Variants.ContainsKey).Distinct().ToDictionary(id => id, id => Variants[id]));
        resolver.Setup(r => r.ResolveDefaultVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                    (IReadOnlyDictionary<Guid, DefaultVariantResult>)ids.Where(DefaultVariants.ContainsKey).Distinct().ToDictionary(id => id, id => DefaultVariants[id]));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new ProductLedgerReportService(db, names.Object, resolver.Object, new ProductLedgerQueryService(db, resolver.Object), templates.Object, new MovableClock(this));
    }

    /// <summary>Finance's own profitability query over the same books: the ranking P8-05's endpoint pages through, which R10 must agree with.</summary>
    internal ProductLedgerQueryService ProfitabilityQuery(Guid? org = null)
    {
        var resolver = new Mock<IProductVariantResolver>();
        resolver.Setup(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) =>
                    (IReadOnlyDictionary<Guid, VariantDescription>)ids.Where(Variants.ContainsKey).Distinct().ToDictionary(id => id, id => Variants[id]));

        return new ProductLedgerQueryService(Db(org), resolver.Object);
    }

    /// <summary>
    /// One movement on a variant's ledger, chained the way <c>ProductLedgerService</c> chains it: the next sequence
    /// number, and running totals worked from the variant's last entry by the weighted-average rules. An OUT is
    /// costed at the average and takes no cost of its own. Posted in the order the calls are made unless
    /// <paramref name="postedOn"/> says otherwise, whatever business date it carries.
    /// </summary>
    internal ProductLedgerEntry Move(
        Guid variant, Guid product, string type, decimal quantity, DateTime date, decimal? unitCost = null, string? reference = null,
        Guid? partner = null, Guid? org = null, DateTime? postedOn = null)
    {
        var orgId = org ?? Org;
        var isIn  = type is "PURCHASE" or "RETURN_IN" || (type == "ADJUSTMENT" && unitCost is not null);

        using var db = Db(orgId);
        var last = db.ProductLedgerEntries.Where(e => e.VariantUuid == variant).OrderByDescending(e => e.SequenceNo).FirstOrDefault();
        var step = isIn
            ? WeightedAverageCosting.In(last?.RunningQty ?? 0m, last?.RunningValue ?? 0m, quantity, unitCost!.Value)
            : WeightedAverageCosting.Out(last?.RunningQty ?? 0m, last?.RunningValue ?? 0m, quantity);

        var n = ++_moves;
        var entry = new ProductLedgerEntry
        {
            UUID = Guid.NewGuid(), OrganizationId = orgId, VariantUuid = variant, ProductUuid = product, SequenceNo = (last?.SequenceNo ?? 0) + 1,
            EntryDate = date, EntryType = type, ReferenceType = type == "SALE" ? "SalesInvoice" : type == "PURCHASE" ? "GRN" : "Adjustment",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = reference ?? $"REF-{n:000}", PartnerId = partner,
            Quantity = quantity, UnitCost = step.UnitCost, TotalCost = step.TotalCost, Direction = isIn ? "IN" : "OUT",
            RunningQty = step.RunningQty, RunningValue = step.RunningValue, CreatedBy = 1,
            CreatedDate = postedOn ?? new DateTime(2026, 1, 1).AddMinutes(n)
        };

        db.ProductLedgerEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    /// <summary>The three products <see cref="SeedLedgerScatter"/> moves; the first has three variants, the second two and the third one.</summary>
    internal static readonly Guid[] ScatterProducts =
        [.. Enumerable.Range(1, 3).Select(i => Guid.Parse($"e1000000-0000-0000-0000-{i:000000000000}"))];

    /// <summary>
    /// A history from a fixed pseudo-random hand: six variants of three products, twelve to seventeen movements
    /// each over about two months, of every kind (purchases, sales, returns either way, write-offs, adjustments
    /// either way), with suppliers and customers (one the lookup does not know) and some with none, several a day
    /// on some days, and never more taken out than the variant holds. A few purchases are another organization's.
    /// Identical in every world that seeds it, so two worlds can be compared.
    /// </summary>
    internal void SeedLedgerScatter()
    {
        var random = new Random(20260924);
        string[] productNames = ["Laptop", "Mouse", "Cable"];
        (Guid Variant, int Product, bool Default)[] variants =
        [
            (Guid.Parse("f1000000-0000-0000-0000-000000000001"), 0, true), (Guid.Parse("f1000000-0000-0000-0000-000000000002"), 0, false),
            (Guid.Parse("f1000000-0000-0000-0000-000000000003"), 0, false), (Guid.Parse("f1000000-0000-0000-0000-000000000004"), 1, true),
            (Guid.Parse("f1000000-0000-0000-0000-000000000005"), 1, false), (Guid.Parse("f1000000-0000-0000-0000-000000000006"), 2, true)
        ];
        Guid?[] suppliers = [Globex, Stranger];
        Guid?[] customers = [Acme, Globex, Stranger];
        string[] kinds = ["PURCHASE", "SALE", "SALE", "RETURN_IN", "RETURN_OUT", "WRITE_OFF", "ADJUSTMENT_IN", "ADJUSTMENT_OUT"];

        foreach (var (variant, productIndex, isDefault) in variants)
            Catalog(variant, ScatterProducts[productIndex], productNames[productIndex], sku: $"SKU-{variant.ToString("N")[^2..]}", variantName: isDefault ? "Default" : $"Option {variant.ToString("N")[^2..]}", isDefault);

        foreach (var (variant, productIndex, _) in variants)
        {
            var product = ScatterProducts[productIndex];
            var day = 0;
            var held = 0m;

            for (var i = 0; i < random.Next(12, 18); i++)
            {
                day += random.Next(0, 4);
                var date = new DateTime(2026, 8, 1).AddDays(day).AddHours(random.Next(0, 24));
                var kind = held < 1m ? "PURCHASE" : kinds[random.Next(kinds.Length)];
                var quantity = random.Next(1, 40) / 4m;

                var isIn = kind is "PURCHASE" or "RETURN_IN" or "ADJUSTMENT_IN";
                if (!isIn) quantity = Math.Min(quantity, held);

                var partner = kind switch
                {
                    "PURCHASE" or "RETURN_OUT" => suppliers[random.Next(suppliers.Length)],
                    "SALE" or "RETURN_IN"      => customers[random.Next(customers.Length)],
                    _                          => null
                };

                var entry = Move(variant, product, kind.StartsWith("ADJUSTMENT", StringComparison.Ordinal) ? "ADJUSTMENT" : kind, quantity, date,
                    unitCost: isIn ? random.Next(100, 5000) / 100m : null, partner: partner);
                held = entry.RunningQty;
            }
        }

        for (var i = 0; i < 4; i++)
            Move(variants[0].Variant, ScatterProducts[0], "PURCHASE", 3m + i, new DateTime(2026, 8, 5 + i), unitCost: 7m, org: OtherOrg);
    }

    // ── Cost of sales (R8) ───────────────────────────────────────────────────

    /// <summary>
    /// The cost of sales issuing <paramref name="invoice"/> books: one SALE entry on the product ledger to an
    /// invoice line, each costed at <paramref name="lineCosts"/> in turn. Like Finance's own entries it names the
    /// invoice, and it is dated the day the invoice was issued.
    /// </summary>
    internal void BookCost(SalesInvoice invoice, params decimal[] lineCosts)
    {
        var lines = invoice.Lines.OrderBy(l => l.LineNo).ToList();
        if (lineCosts.Length != lines.Count) throw new ArgumentException("A cost for every invoice line.", nameof(lineCosts));

        using var db = Db(invoice.OrganizationId);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var last = db.ProductLedgerEntries.Where(e => e.VariantUuid == line.VariantUuid).OrderByDescending(e => e.SequenceNo).FirstOrDefault();

            db.ProductLedgerEntries.Add(new ProductLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = invoice.OrganizationId, VariantUuid = line.VariantUuid,
                ProductUuid = last?.ProductUuid ?? Variants.GetValueOrDefault(line.VariantUuid)?.ProductUuid ?? line.VariantUuid,
                SequenceNo = (last?.SequenceNo ?? 0) + 1, EntryDate = invoice.InvoiceDate, EntryType = "SALE", ReferenceType = "SalesInvoice",
                ReferenceId = invoice.UUID, ReferenceNumber = invoice.InvoiceNumber, PartnerId = invoice.PartnerId, Quantity = line.Quantity,
                UnitCost = line.Quantity == 0m ? 0m : Math.Round(lineCosts[i] / line.Quantity, 4), TotalCost = lineCosts[i], Direction = "OUT",
                RunningQty = last?.RunningQty ?? 0m, RunningValue = last?.RunningValue ?? 0m, CreatedBy = 1, CreatedDate = invoice.InvoiceDate
            });
            db.SaveChanges();
        }
    }

    /// <summary>
    /// <see cref="SeedSales"/>, then the cost of sales for three invoices in four, drawn from a fixed hand: the
    /// costs are between half and one and a quarter times what each line billed, so some rows have a margin and
    /// some a loss, and the fourth invoice never had its cost booked, as one issued before the product ledger
    /// would not have. Cancelled, credited, draft and unbooked invoices are costed too, which the report must
    /// ignore. Identical in every world that seeds it.
    /// </summary>
    internal void SeedSalesWithCosts()
    {
        SeedSales();

        var random = new Random(20260922);
        List<SalesInvoice> invoices;
        using (var db = Db(Org))  invoices = [.. db.SalesInvoices.Include(i => i.Lines).OrderBy(i => i.InvoiceNumber)];
        using (var db = Db(OtherOrg)) invoices.AddRange(db.SalesInvoices.Include(i => i.Lines).OrderBy(i => i.InvoiceNumber));

        var n = 0;
        foreach (var invoice in invoices.OrderBy(i => i.InvoiceNumber).ThenBy(i => i.OrganizationId))
        {
            if (++n % 4 == 0) continue;
            BookCost(invoice, [.. invoice.Lines.OrderBy(l => l.LineNo).Select(l =>
                Math.Round(l.Quantity * l.UnitPrice * (1m - l.DiscountPercent / 100m) * (50 + random.Next(0, 76)) / 100m, 2))]); // the ledger holds a cost to the cent
        }
    }

    internal static DateTime D(int month, int day) => new(2026, month, day);
}
