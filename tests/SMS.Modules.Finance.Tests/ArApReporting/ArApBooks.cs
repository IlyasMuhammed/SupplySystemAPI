using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>
/// A48 RPT test books: one in-memory Finance database, documents written the way the Finance services leave them (an
/// issued invoice has its INVOICE ledger entry, a payment its allocations and PAYMENT entry, an approved supplier invoice
/// its INVOICE_APPROVED entry, a posted supplier payment its lines, PaidAmount and PAYMENT_POSTED entry), and the A48
/// reporting services over it.
/// </summary>
internal sealed class ArApBooks
{
    public static readonly DateTime Today = new(2026, 10, 25);

    private readonly Dictionary<Guid, int> _customerSeq = [];
    private readonly Dictionary<Guid, int> _supplierSeq = [];
    private readonly Dictionary<Guid, string> _names = [];
    private int _numbers;

    private readonly Func<Guid, FinanceDbContext>? _factory;

    /// <param name="factory">Contexts on another store (LocalDB) instead of the in-memory one.</param>
    public ArApBooks(Guid? org = null, Func<Guid, FinanceDbContext>? factory = null)
    {
        Org      = org ?? Guid.NewGuid();
        _factory = factory;
    }

    public string DbName { get; } = Guid.NewGuid().ToString();
    public Guid   Org    { get; }
    public FixedClock Clock { get; } = new(Today);
    public List<(string To, string Subject, IReadOnlyList<EmailAttachment>? Attachments)> Sent { get; } = [];
    public bool FailEmail { get; set; }
    public Dictionary<Guid, string> Emails { get; } = [];

    public FinanceDbContext Db(Guid? org = null) =>
        _factory is not null
            ? _factory(org ?? Org)
            : new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(DbName).Options,
                new StaticTenantContext { OrganizationId = org ?? Org });

    public FinanceDbContext SuperAdminDb(Guid org) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(DbName).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = true });

    public Guid Partner(string name, string? email = null)
    {
        var id = Guid.NewGuid();
        _names[id] = name;
        if (email is not null) Emails[id] = email;
        return id;
    }

    public ArApReportingService Reports(FinanceDbContext db) =>
        new(db, NameLookup(), clock: Clock, partners: PartnerLookup());

    public StatementService Statements(FinanceDbContext db, IEmailSender? email = null) =>
        new(db, Reports(db), email ?? EmailSender(), Templates());

    public ISupplierNameLookupService NameLookup()
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(_names.ContainsKey).ToDictionary(id => id, id => _names[id]));
        return names.Object;
    }

    public IPartnerLookupService PartnerLookup()
    {
        var lookup = new Mock<IPartnerLookupService>();
        lookup.Setup(l => l.GetManyAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((Guid org, IEnumerable<Guid> ids, string? _, CancellationToken _) =>
                  (IReadOnlyDictionary<Guid, PartnerInfo>)ids.Where(id => org == Org && _names.ContainsKey(id)).Distinct()
                      .ToDictionary(id => id, id => new PartnerInfo(id, "P-" + _names[id][..Math.Min(3, _names[id].Length)].ToUpperInvariant(),
                          _names[id], true, true, true, Emails.GetValueOrDefault(id), null, "1 Main Street, Lahore", null, null, null, 30, null)));
        return lookup.Object;
    }

    public IEmailSender EmailSender()
    {
        var sender = new Mock<IEmailSender>();
        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()))
              .Returns((string to, string subject, string _, IReadOnlyList<EmailAttachment>? att) =>
              {
                  if (FailEmail) throw new InvalidOperationException("SMTP is down");
                  Sent.Add((to, subject, att));
                  return Task.CompletedTask;
              });
        // REV-1: what the A48 callers use. Never throws — a failure is a result, as the real sender's is.
        sender.Setup(s => s.SendWithResultAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                  It.IsAny<IReadOnlyList<EmailAttachment>?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string to, string subject, string _, IReadOnlyList<EmailAttachment>? att, CancellationToken _) =>
              {
                  if (FailEmail) return EmailSendResult.Failed("SMTP is down");
                  Sent.Add((to, subject, att));
                  return EmailSendResult.Sent;
              });
        return sender.Object;
    }

    public static IPoDocumentTemplateService Templates()
    {
        var t = new Mock<IPoDocumentTemplateService>();
        t.Setup(x => x.GetActiveAsync()).ReturnsAsync(new PoDocumentTemplateModel { CompanyName = "Acme Supplies", CompanyAddress = "Lahore" });
        return t.Object;
    }

    private string Next(string prefix) => $"{prefix}-{++_numbers:00000}";

    // ── Receivables ──────────────────────────────────────────────────────────

    public async Task<SalesInvoice> SaleAsync(
        Guid customer, DateTime invoiceDate, decimal amount, int termsDays = 30, string status = SalesInvoiceStatuses.Issued,
        string currency = "PKR", decimal? rate = null, Guid? org = null, string? source = null, bool book = true)
    {
        await using var db = Db(org);
        var inv = new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, TraceId = Guid.NewGuid(), InvoiceNumber = Next("SINV"),
            SaleOrderNumber = "SO-1", PartnerId = customer, PartnerName = _names.GetValueOrDefault(customer) ?? "",
            InvoiceDate = invoiceDate, DueDate = invoiceDate.AddDays(termsDays), Subtotal = amount, GrandTotal = amount,
            BalanceDue = amount, Status = status, CurrencyCode = currency, ExchangeRate = rate,
            BaseGrandTotal = rate is { } r ? amount * r : null, SourceType = source ?? SalesInvoiceSources.SaleOrder,
            CreatedBy = 1, CreatedDate = invoiceDate
        };
        db.SalesInvoices.Add(inv);
        if (book && status != SalesInvoiceStatuses.Draft)
            db.CustomerLedgerEntries.Add(CustomerEntry(customer, org, invoiceDate, CustomerLedgerEntryTypes.Invoice, "SalesInvoice",
                inv.UUID, inv.InvoiceNumber, amount, 0m, currency));
        await db.SaveChangesAsync();
        return inv;
    }

    public async Task<CustomerPayment> ReceiveAsync(
        Guid customer, DateTime date, decimal amount, IReadOnlyList<(SalesInvoice Invoice, decimal Amount)>? allocations = null,
        string currency = "PKR", decimal? rate = null, string status = CustomerPaymentStatuses.Received, Guid? org = null)
    {
        await using var db = Db(org);
        var pay = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, PartnerId = customer, PaymentNumber = Next("CPAY"),
            PaymentDate = date, Amount = amount, PaymentMethod = CustomerPaymentMethods.BankTransfer, CurrencyCode = currency,
            ExchangeRate = rate, AmountBase = rate is { } r ? amount * r : null, Status = status, CreatedBy = 1, CreatedDate = date
        };
        foreach (var (invoice, applied) in allocations ?? [])
        {
            var tracked = await db.SalesInvoices.SingleAsync(i => i.Id == invoice.Id);
            pay.Allocations.Add(new PaymentAllocation
            {
                UUID = Guid.NewGuid(), OrganizationId = org ?? Org, SalesInvoiceId = tracked.Id, AllocatedAmount = applied,
                AllocatedAt = date, AllocatedBy = 1
            });
            tracked.AmountPaid += applied;
            tracked.BalanceDue -= applied;
            tracked.Status = tracked.BalanceDue <= 0m ? SalesInvoiceStatuses.Paid : SalesInvoiceStatuses.PartiallyPaid;
        }
        db.CustomerPayments.Add(pay);
        db.CustomerLedgerEntries.Add(CustomerEntry(customer, org, date, CustomerLedgerEntryTypes.Payment, "CustomerPayment",
            pay.UUID, pay.PaymentNumber, 0m, amount, currency));
        await db.SaveChangesAsync();
        return pay;
    }

    /// <summary>A customer credit note (A38 / A48 memo): positive GrandTotal, CREDIT_NOTE ledger credit; <paramref name="directlyApplied"/> on its original (A38 style, no rows).</summary>
    public async Task<SalesInvoice> CreditNoteAsync(Guid customer, DateTime date, decimal amount, SalesInvoice? original = null, decimal directlyApplied = 0m)
    {
        await using var db = Db();
        var note = new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), InvoiceNumber = Next("SCN"), PartnerId = customer,
            PartnerName = _names.GetValueOrDefault(customer) ?? "", InvoiceDate = date, DueDate = date, Subtotal = amount, GrandTotal = amount,
            Status = SalesInvoiceStatuses.CreditNote, CurrencyCode = "PKR", OriginalInvoiceUuid = original?.UUID, CreditedAmount = directlyApplied,
            SourceType = "AR_CREDIT_MEMO", CreatedBy = 1, CreatedDate = date
        };
        db.SalesInvoices.Add(note);
        if (original is not null && directlyApplied > 0m)
        {
            var tracked = await db.SalesInvoices.SingleAsync(i => i.Id == original.Id);
            tracked.CreditedAmount += directlyApplied;
            tracked.BalanceDue     -= directlyApplied;
        }
        db.CustomerLedgerEntries.Add(CustomerEntry(customer, null, date, CustomerLedgerEntryTypes.CreditNote, "SalesCreditNote",
            note.UUID, note.InvoiceNumber, 0m, amount, "PKR"));
        await db.SaveChangesAsync();
        return note;
    }

    /// <summary>An A48 application row (D-AR-12): both documents' CreditedAmount rise; no ledger entry.</summary>
    public async Task ApplyCreditAsync(SalesInvoice note, SalesInvoice invoice, decimal amount, DateTime when)
    {
        await using var db = Db();
        var n = await db.SalesInvoices.SingleAsync(i => i.Id == note.Id);
        var i = await db.SalesInvoices.SingleAsync(x => x.Id == invoice.Id);
        n.CreditedAmount += amount;
        i.CreditedAmount += amount;
        i.BalanceDue     -= amount;
        db.CustomerCreditApplications.Add(new CustomerCreditApplication
        {
            OrganizationId = Org, CreditNoteId = n.Id, CreditNoteUuid = n.UUID, CreditNoteNumber = n.InvoiceNumber, InvoiceId = i.Id,
            InvoiceUuid = i.UUID, InvoiceNumber = i.InvoiceNumber, PartnerUuid = i.PartnerId, Amount = amount, AppliedAt = when
        });
        await db.SaveChangesAsync();
    }

    public async Task CancelAsync(SalesInvoice invoice, DateTime when)
    {
        await using var db = Db();
        var tracked = await db.SalesInvoices.SingleAsync(i => i.Id == invoice.Id);
        tracked.Status = SalesInvoiceStatuses.Cancelled;
        tracked.CancelledAt = when;
        tracked.BalanceDue = 0m;
        db.CustomerLedgerEntries.Add(CustomerEntry(invoice.PartnerId, null, when, CustomerLedgerEntryTypes.CreditNote, "SalesInvoice",
            invoice.UUID, invoice.InvoiceNumber, 0m, invoice.GrandTotal, invoice.CurrencyCode));
        await db.SaveChangesAsync();
    }

    public async Task BounceAsync(CustomerPayment payment, DateTime when)
    {
        await using var db = Db();
        var tracked = await db.CustomerPayments.Include(p => p.Allocations).SingleAsync(p => p.Id == payment.Id);
        tracked.Status = CustomerPaymentStatuses.Bounced;
        db.CustomerLedgerEntries.Add(CustomerEntry(payment.PartnerId, null, when, CustomerLedgerEntryTypes.Payment, "CustomerPayment",
            payment.UUID, payment.PaymentNumber, payment.Amount, 0m, payment.CurrencyCode));
        await db.SaveChangesAsync();
    }

    private CustomerLedgerEntry CustomerEntry(
        Guid customer, Guid? org, DateTime date, string type, string refType, Guid refId, string refNo, decimal debit, decimal credit,
        string currency)
    {
        var seq = _customerSeq[customer] = _customerSeq.GetValueOrDefault(customer) + 1;
        return new CustomerLedgerEntry
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, PartnerId = customer, SequenceNo = seq, EntryDate = date,
            EntryType = type, ReferenceType = refType, ReferenceId = refId, ReferenceNumber = refNo, DebitAmount = debit,
            CreditAmount = credit, CurrencyCode = currency, CreatedBy = 1, CreatedDate = date
        };
    }

    // ── Payables ─────────────────────────────────────────────────────────────

    public async Task<Invoice> BillAsync(
        Guid supplier, DateTime invoiceDate, decimal amount, int termsDays = 30, string matchStatus = "Approved",
        string currency = "PKR", decimal? rate = null, string? supplierInvoiceNo = null, Guid? org = null)
    {
        await using var db = Db(org);
        var inv = new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = org ?? Org, InvoiceNumber = Next("INV"),
            SupplierInvoiceNo = supplierInvoiceNo, SupplierId = supplier, SupplierName = _names.GetValueOrDefault(supplier) ?? "",
            InvoiceDate = invoiceDate, ReceivedDate = invoiceDate, DueDate = invoiceDate.AddDays(termsDays), Currency = currency,
            Subtotal = amount, TotalAmount = amount, ExchangeRate = rate, BaseTotalAmount = rate is { } r ? amount * r : null,
            MatchStatus = matchStatus, PaymentStatus = "UNPAID",
            ApprovedAt = matchStatus == "Approved" ? invoiceDate : null, CreatedBy = 1, CreatedDate = invoiceDate
        };
        db.Invoices.Add(inv);
        if (matchStatus == "Approved")
            db.SupplierLedgerEntries.Add(SupplierEntry(supplier, org, invoiceDate, "INVOICE_APPROVED", "Invoice", inv.UUID,
                inv.InvoiceNumber, amount, 0m));
        await db.SaveChangesAsync();
        return inv;
    }

    public async Task<SupplierPayment> PayAsync(
        Guid supplier, DateTime date, IReadOnlyList<(Invoice Invoice, decimal Amount)> lines, string currency = "PKR",
        decimal? rate = null, string status = "POSTED")
    {
        await using var db = Db();
        var pay = new SupplierPayment
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = Org, PaymentNumber = Next("SPAY"), SupplierId = supplier,
            PaymentDate = date, PaymentMethod = "BANK_TRANSFER", BankAccount = "ACC-1", TotalAmount = lines.Sum(l => l.Amount),
            Status = status, CurrencyCode = currency, ExchangeRate = rate, CreatedBy = 1, CreatedDate = date, PostedAt = date
        };
        foreach (var (invoice, amount) in lines)
        {
            pay.Lines.Add(new SupplierPaymentLine
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, InvoiceUuid = invoice.UUID, InvoiceNumber = invoice.InvoiceNumber,
                AllocatedAmount = amount, OutstandingBeforeAllocation = invoice.TotalAmount
            });
            if (status == "POSTED")
            {
                var tracked = await db.Invoices.SingleAsync(i => i.Id == invoice.Id);
                tracked.PaidAmount += amount;
            }
        }
        db.SupplierPayments.Add(pay);
        if (status == "POSTED")
            db.SupplierLedgerEntries.Add(SupplierEntry(supplier, null, date, "PAYMENT_POSTED", "SupplierPayment", pay.UUID,
                pay.PaymentNumber, 0m, pay.TotalAmount));
        await db.SaveChangesAsync();
        return pay;
    }

    public async Task<SupplierAdvancePayment> AdvanceAsync(Guid supplier, DateTime date, decimal amount)
    {
        await using var db = Db();
        var pay = new SupplierPayment
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = Org, PaymentNumber = Next("SPAY"), SupplierId = supplier,
            PaymentDate = date, PaymentMethod = "BANK_TRANSFER", BankAccount = "ACC-1", TotalAmount = amount, Status = "POSTED",
            PaymentType = "ADVANCE_PAYMENT", CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = date, PostedAt = date
        };
        var advance = new SupplierAdvancePayment
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, SupplierId = supplier, SupplierPaymentUuid = pay.UUID, OriginalAmount = amount,
            AvailableBalance = amount, CreatedDate = date
        };
        db.SupplierPayments.Add(pay);
        db.SupplierAdvancePayments.Add(advance);
        db.SupplierLedgerEntries.Add(SupplierEntry(supplier, null, date, "PAYMENT_POSTED", "SupplierPayment", pay.UUID, pay.PaymentNumber, 0m, amount));
        await db.SaveChangesAsync();
        return advance;
    }

    public async Task ReverseAsync(Invoice invoice, DateTime when)
    {
        await using var db = Db();
        var tracked = await db.Invoices.SingleAsync(i => i.Id == invoice.Id);
        tracked.MatchStatus = "Reversed";
        tracked.ReversedAt = when;
        db.SupplierLedgerEntries.Add(SupplierEntry(invoice.SupplierId, null, when, "INVOICE_REVERSED", "Invoice", invoice.UUID,
            invoice.InvoiceNumber, 0m, invoice.TotalAmount - invoice.PaidAmount));
        await db.SaveChangesAsync();
    }

    private SupplierLedgerEntry SupplierEntry(
        Guid supplier, Guid? org, DateTime date, string type, string refType, Guid refId, string refNo, decimal debit, decimal credit)
    {
        var seq = _supplierSeq[supplier] = _supplierSeq.GetValueOrDefault(supplier) + 1;
        return new SupplierLedgerEntry
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, SupplierId = supplier, SequenceNo = seq, TransactionType = type,
            ReferenceType = refType, ReferenceId = refId, ReferenceNo = refNo, EntryDate = date, DebitAmount = debit,
            CreditAmount = credit, CreatedBy = 1, CreatedDate = date
        };
    }
}

internal sealed class FixedClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = DateTime.SpecifyKind(now, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(Now);
}
