using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// Round-2 security audit of the purchase-side hardening (C': A PO lock, B invoice locks in payments and notes,
/// E "only Approved invoices are payables"): every way to pay, settle or reduce a supplier invoice, as
/// (1) an ordinary user, (2) a platform super admin working in ANOTHER organization — who bypasses the tenant
/// filter, so only an explicit current-organization filter keeps them out — and (3) a caller naming another
/// supplier's documents. Each test states what must hold, whichever way the owner chooses to refuse.
/// </summary>
public class PaymentsAndNotesSecurityAuditTests
{
    private static readonly Guid OrgB = Guid.NewGuid();

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private sealed class Contexts : IAsyncDisposable
    {
        public Contexts(PurchaseRig rig, ITenantContext tenant)
        {
            Finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
            Demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
            Warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
            Rig       = rig;
        }

        public PurchaseRig        Rig       { get; }
        public FinanceDbContext   Finance   { get; }
        public DemandDbContext    Demand    { get; }
        public WarehouseDbContext Warehouse { get; }

        public SupplierPaymentRepository Payments() =>
            new(Finance, new SupplierLedgerService(Finance), new Mock<INotificationService>().Object);

        public PaymentRepository LegacyPayments() => new(Finance);

        public InvoiceService Invoices() => new(
            new InvoiceRepository(Finance, Demand, Warehouse, new SupplierLedgerService(Finance), new FakeSupplierNameLookup(),
                new TaxCodeLookup(Finance), Rig.Rates, Rig.BaseCurrency, Rig.BaseCurrency),
            new Mock<IBackgroundJobClient>().Object);

        public CreditNoteRepository CreditNotes() => new(Finance, Warehouse, new SupplierLedgerService(Finance));

        public DebitNoteRepository DebitNotes() => new(Finance, Warehouse, new Mock<IBackgroundJobClient>().Object,
            new Mock<INotificationService>().Object, new Mock<IAuditService>().Object, new SupplierLedgerService(Finance));

        public async ValueTask DisposeAsync()
        {
            await Finance.DisposeAsync();
            await Demand.DisposeAsync();
            await Warehouse.DisposeAsync();
        }
    }

    private static Contexts OrgA(PurchaseRig rig) => new(rig, new StaticTenantContext { OrganizationId = rig.Org });
    private static Contexts SuperAdminInB(PurchaseRig rig) => new(rig, new StaticTenantContext { OrganizationId = OrgB, IsSuperAdmin = true });

    private static FinanceDbContext Auditor(PurchaseRig rig) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options, new StaticTenantContext { IsSuperAdmin = true });

    private static CreateSupplierPaymentRequest Pay(PurchaseRig rig, Guid invoice, decimal amount = 100m, string method = "CASH") => new()
    {
        SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 30), PaymentMethod = method,
        TotalAmount = amount, ChequeNo = method == "CHEQUE" ? "CHQ-1" : null, ChequeDate = method == "CHEQUE" ? new DateTime(2026, 9, 30) : null,
        Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = amount }]
    };

    /// <summary>A supplier invoice that is booked as a payable — or not (Matched, never approved).</summary>
    private static async Task<Guid> InvoiceAsync(PurchaseRig rig, bool approved)
    {
        if (approved) return (await rig.ApprovedAsync()).Invoice;
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        return await rig.CreateAsync(rig.Request(po, lines));
    }

    private static async Task<T?> Swallow<T>(Func<Task<T>> act)
    {
        try { return await act(); }
        catch (NotFoundException) { return default; }
    }

    private static async Task ShouldRefuse(Func<Task> act, string why)
    {
        var refused = false;
        try { await act(); }
        catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException or NotFoundException) { refused = true; }
        refused.Should().BeTrue(why);
    }

    // ── E: only Approved invoices are payables ───────────────────────────────

    [Fact]
    public async Task SecurityAudit_E_a_supplier_payment_cannot_be_drafted_for_an_unapproved_invoice()
    {
        var rig = PurchaseRig.New();
        var pending = await InvoiceAsync(rig, approved: false);

        await using var a = OrgA(rig);
        await ShouldRefuse(() => a.Payments().CreateAsync(Pay(rig, pending), PurchaseRig.User),
            "an invoice that was never approved is not a payable: nothing is on the supplier ledger for it yet");
    }

    [Fact]
    public async Task SecurityAudit_E_the_legacy_single_payment_flow_refuses_an_unapproved_invoice()
    {
        var rig = PurchaseRig.New();
        var pending = await InvoiceAsync(rig, approved: false);

        await using (var a = OrgA(rig))
            await ShouldRefuse(() => a.LegacyPayments().CreateAsync(new CreatePaymentRequest
            {
                InvoiceUuid = pending, PaymentDate = new DateTime(2026, 9, 30), AmountPaid = 1000m, PaymentMethod = "Cash"
            }, PurchaseRig.User), "POST api/finance/payments must not pay an unapproved invoice either");

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.SingleAsync(i => i.UUID == pending)).PaymentStatus.Should().Be("Unpaid");
        (await auditor.Payments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SecurityAudit_E_a_draft_payment_already_carrying_an_unapproved_invoice_can_be_neither_approved_nor_posted()
    {
        // A draft from before E (or one whose invoice changed under it) must not move money at approval or posting.
        var rig = PurchaseRig.New();
        var pending = await InvoiceAsync(rig, approved: false);
        var draft    = await rig.SupplierPaymentAsync(pending, "DRAFT");
        var approved = await rig.SupplierPaymentAsync(pending, "APPROVED");

        await using (var a = OrgA(rig))
        {
            await ShouldRefuse(() => a.Payments().ApproveAsync(draft.UUID, PurchaseRig.User), "approving it would schedule a payment of a non-payable");
            await ShouldRefuse(() => a.Payments().PostAsync(approved.UUID, PurchaseRig.User), "posting it would pay a non-payable");
        }

        await using var auditor = Auditor(rig);
        (await auditor.SupplierPayments.SingleAsync(p => p.UUID == draft.UUID)).Status.Should().Be("DRAFT");
        (await auditor.SupplierPayments.SingleAsync(p => p.UUID == approved.UUID)).Status.Should().Be("APPROVED");
        (await auditor.Invoices.SingleAsync(i => i.UUID == pending)).PaidAmount.Should().Be(0m);
        (await auditor.SupplierLedgerEntries.CountAsync(e => e.TransactionType == "PAYMENT_POSTED")).Should().Be(0);
    }

    // ── Tenancy: a super admin working in another organization ───────────────

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cannot_draft_a_payment_against_org_A_invoice()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);

        await using (var b = SuperAdminInB(rig))
            await b.Payments().Invoking(p => p.CreateAsync(Pay(rig, invoice), 1))
                .Should().ThrowAsync<NotFoundException>("another organization's invoice is not found, for a super admin too");

        await using var auditor = Auditor(rig);
        (await auditor.SupplierPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cannot_approve_cancel_post_or_bounce_org_A_payment()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);

        Guid draft, posted;
        await using (var a = OrgA(rig))
        {
            draft  = await a.Payments().CreateAsync(Pay(rig, invoice, 100m), PurchaseRig.User);
            posted = await a.Payments().CreateAsync(Pay(rig, invoice, 200m, "CHEQUE"), PurchaseRig.User);
            (await a.Payments().ApproveAsync(posted, PurchaseRig.User)).Should().BeTrue();
            (await a.Payments().PostAsync(posted, PurchaseRig.User)).Should().BeTrue();
        }

        await using (var b = SuperAdminInB(rig))
        {
            (await Swallow(() => b.Payments().ApproveAsync(draft, 1))).Should().BeFalse("approve: another org's payment is a 404");
            (await Swallow(() => b.Payments().CancelAsync(draft, 1))).Should().BeFalse("cancel: another org's payment is a 404");
            (await Swallow(() => b.Payments().PostAsync(draft, 1))).Should().BeFalse("post: another org's payment is a 404");
            (await Swallow(() => b.Payments().BounceAsync(posted, 1))).Should().BeFalse("bounce: another org's payment is a 404");
        }

        await using var auditor = Auditor(rig);
        (await auditor.SupplierPayments.SingleAsync(p => p.UUID == draft)).Status.Should().Be("DRAFT");
        (await auditor.SupplierPayments.SingleAsync(p => p.UUID == posted)).Status.Should().Be("POSTED");
        (await auditor.Invoices.SingleAsync(i => i.UUID == invoice)).PaidAmount.Should().Be(200m);
        (await auditor.SupplierLedgerEntries.CountAsync(e => e.OrganizationId == OrgB)).Should().Be(0, "nothing may be booked into org B's ledger");
    }

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cannot_record_or_reverse_a_legacy_payment_on_org_A_invoice()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);
        var legacy  = await rig.LegacyPaymentAsync(invoice, "Pending", 100m);

        await using (var b = SuperAdminInB(rig))
        {
            await b.LegacyPayments().Invoking(p => p.CreateAsync(new CreatePaymentRequest
            {
                InvoiceUuid = invoice, PaymentDate = new DateTime(2026, 9, 30), AmountPaid = 900m, PaymentMethod = "Cash"
            }, 1)).Should().ThrowAsync<NotFoundException>();

            (await Swallow(() => b.LegacyPayments().PatchAsync(legacy.UUID, new PatchPaymentRequest { Status = "Reversed" }, 1)))
                .Should().BeFalse("another org's payment is a 404");
        }

        await using var auditor = Auditor(rig);
        (await auditor.Payments.CountAsync()).Should().Be(1);
        (await auditor.Payments.SingleAsync()).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task SecurityAudit_the_legacy_payment_status_cannot_be_patched_to_an_arbitrary_value()
    {
        // PatchPaymentRequest.Status is copied verbatim; only the transitions the flow knows may be set.
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);
        var legacy  = await rig.LegacyPaymentAsync(invoice, "Pending", 100m);

        await using (var a = OrgA(rig))
            await ShouldRefuse(() => a.LegacyPayments().PatchAsync(legacy.UUID, new PatchPaymentRequest { Status = "Settled-by-anyone" }, PurchaseRig.User),
                "an unknown status is a mass-assigned field, not a transition");

        await using var auditor = Auditor(rig);
        (await auditor.Payments.SingleAsync()).Status.Should().Be("Pending");
    }

    // ── Credit / debit notes ─────────────────────────────────────────────────

    private static CreditNote CarriedForwardCredit(Guid supplier, decimal amount = 50m) => new()
    {
        UUID = Guid.NewGuid(), CreditNoteNumber = $"CN-2026-{Random.Shared.Next(10000, 99999)}", SupplierCreditNoteNo = "S-1",
        SroUuid = Guid.NewGuid(), SroNumber = "SRO-1", SupplierId = supplier, SupplierName = "Somebody", CreditDate = new DateTime(2026, 9, 20),
        CreditAmount = amount, ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = amount, IsActive = true,
        CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    private static DebitNote CarriedForwardDebit(Guid supplier, decimal amount = 50m) => new()
    {
        UUID = Guid.NewGuid(), DebitNoteNumber = $"DN-2026-{Random.Shared.Next(10000, 99999)}", SroUuid = Guid.NewGuid(), SroNumber = "SRO-2",
        SupplierId = supplier, SupplierName = "Somebody", DebitReason = "SHORT_SUPPLY", DebitAmount = amount,
        ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = amount, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task SecurityAudit_a_carried_forward_credit_or_debit_note_of_one_supplier_cannot_reduce_another_suppliers_invoice()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);
        var otherSupplier = Guid.NewGuid();
        var credit = CarriedForwardCredit(otherSupplier);
        var debit  = CarriedForwardDebit(otherSupplier);
        rig.Finance.CreditNotes.Add(credit);
        rig.Finance.DebitNotes.Add(debit);
        await rig.Finance.SaveChangesAsync();
        rig.Forget();

        await using (var a = OrgA(rig))
        {
            await ShouldRefuse(() => a.CreditNotes().ApplyCarriedForwardAsync(credit.UUID, new ApplyCreditNoteRequest { InvoiceUuid = invoice }, PurchaseRig.User),
                "supplier X's credit would cut what we owe supplier Y");
            await ShouldRefuse(() => a.DebitNotes().ApplyCarriedForwardAsync(debit.UUID, new ApplyDebitNoteRequest { InvoiceUuid = invoice }, PurchaseRig.User),
                "supplier X's debit note would cut what we owe supplier Y");
        }

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.SingleAsync(i => i.UUID == invoice)).TotalAmount.Should().Be(1000m);
        (await auditor.CreditNotes.SingleAsync()).ApplicationStatus.Should().Be("CARRIED_FORWARD");
        (await auditor.DebitNotes.SingleAsync()).ApplicationStatus.Should().Be("CARRIED_FORWARD");
    }

    [Fact]
    public async Task SecurityAudit_a_credit_or_debit_note_raised_for_one_suppliers_return_never_reduces_another_suppliers_named_invoice()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);

        SupplierReturnOrder Sro(Guid supplier) => new()
        {
            UUID = Guid.NewGuid(), ReturnNumber = $"SRO-2026-{Random.Shared.Next(10000, 99999)}", SroType = "POST_RECEIPT_DEFECT",
            SupplierId = supplier, SupplierName = "Somebody Else", ReturnReason = "DAMAGED", Status = "SUPPLIER_RECEIVED",
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [new SupplierReturnOrderLine { UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "x", QtyToReturn = 10m, UnitCost = 100m }]
        };
        var forCredit = Sro(Guid.NewGuid());
        var forDebit  = Sro(Guid.NewGuid());
        rig.Warehouse.SupplierReturnOrders.AddRange(forCredit, forDebit);
        await rig.Warehouse.SaveChangesAsync();
        rig.Forget();

        await using (var a = OrgA(rig))
        {
            try
            {
                await a.CreditNotes().CreateAsync(new CreateCreditNoteRequest
                {
                    SroId = forCredit.UUID, SupplierCreditNoteNo = "X-CN", CreditDate = new DateTime(2026, 9, 25), CreditAmount = 300m, InvoiceUuid = invoice
                }, PurchaseRig.User);
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException or NotFoundException) { }

            try
            {
                await a.DebitNotes().CreateAsync(new CreateDebitNoteRequest
                {
                    SroId = forDebit.UUID, DebitReason = "SHORT_SUPPLY", DebitAmount = 200m, InvoiceUuid = invoice
                }, PurchaseRig.User);
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException or NotFoundException) { }
        }

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.SingleAsync(i => i.UUID == invoice)).TotalAmount.Should().Be(1000m,
            "a note for supplier X's return may be refused or carried forward, never deducted from supplier Y's invoice");
    }

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cannot_apply_a_note_to_org_A_invoice()
    {
        var rig = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, approved: true);

        await using (var b = SuperAdminInB(rig))
        {
            // Org B's own carried-forward notes, for the same supplier id.
            var credit = CarriedForwardCredit(rig.Supplier);
            var debit  = CarriedForwardDebit(rig.Supplier);
            b.Finance.CreditNotes.Add(credit);
            b.Finance.DebitNotes.Add(debit);
            await b.Finance.SaveChangesAsync();
            b.Finance.ChangeTracker.Clear();

            await b.CreditNotes().Invoking(r => r.ApplyCarriedForwardAsync(credit.UUID, new ApplyCreditNoteRequest { InvoiceUuid = invoice }, 1))
                .Should().ThrowAsync<NotFoundException>("org A's invoice does not exist for org B, super admin or not");
            await b.DebitNotes().Invoking(r => r.ApplyCarriedForwardAsync(debit.UUID, new ApplyDebitNoteRequest { InvoiceUuid = invoice }, 1))
                .Should().ThrowAsync<NotFoundException>();
        }

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.SingleAsync(i => i.UUID == invoice)).TotalAmount.Should().Be(1000m);
    }

    // ── Purchase-return settlement payments ──────────────────────────────────

    [Fact]
    public async Task SecurityAudit_a_purchase_return_settlement_must_name_this_suppliers_own_credit_note()
    {
        var rig = PurchaseRig.New();
        var foreign = CarriedForwardCredit(Guid.NewGuid(), 500m);
        rig.Finance.CreditNotes.Add(foreign);
        await rig.Finance.SaveChangesAsync();
        rig.Forget();

        CreateSupplierPaymentRequest Settle(Guid creditNote) => new()
        {
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 30), PaymentMethod = "CASH",
            TotalAmount = 500m, PaymentType = "PURCHASE_RETURN_SETTLEMENT", CreditNoteUuid = creditNote
        };

        await using (var a = OrgA(rig))
        {
            await ShouldRefuse(() => a.Payments().CreateAsync(Settle(foreign.UUID), PurchaseRig.User),
                "supplier X's credit note cannot settle a payment to supplier Y");
            await ShouldRefuse(() => a.Payments().CreateAsync(Settle(Guid.NewGuid()), PurchaseRig.User),
                "a credit note that does not exist cannot be settled");
        }

        await using var auditor = Auditor(rig);
        (await auditor.SupplierPayments.CountAsync()).Should().Be(0);
        (await auditor.CreditNotes.SingleAsync()).CarriedForwardAmount.Should().Be(500m);
    }

    [Fact]
    public async Task SecurityAudit_a_purchase_return_settlement_cannot_draw_a_credit_note_below_zero()
    {
        var rig = PurchaseRig.New();
        var credit = CarriedForwardCredit(rig.Supplier, 50m);
        rig.Finance.CreditNotes.Add(credit);
        await rig.Finance.SaveChangesAsync();
        rig.Forget();

        await using (var a = OrgA(rig))
        {
            try
            {
                var uuid = await a.Payments().CreateAsync(new CreateSupplierPaymentRequest
                {
                    SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 30), PaymentMethod = "CASH",
                    TotalAmount = 5000m, PaymentType = "PURCHASE_RETURN_SETTLEMENT", CreditNoteUuid = credit.UUID
                }, PurchaseRig.User);
                await a.Payments().ApproveAsync(uuid, PurchaseRig.User);
                await a.Payments().PostAsync(uuid, PurchaseRig.User);
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException) { }
        }

        await using var auditor = Auditor(rig);
        (await auditor.CreditNotes.SingleAsync()).CarriedForwardAmount.Should().BeGreaterThanOrEqualTo(0m,
            "a 50 credit settled for 5000 would leave -4950 'credit' and credit the supplier ledger with money nobody owed");
    }

    // ── A: the purchase order an invoice moves must be this organization's ────

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cannot_raise_an_invoice_against_org_A_purchase_order_or_grn()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await using (var b = SuperAdminInB(rig))
        {
            await b.Invoices().Invoking(s => s.CreateAsync(rig.Request(po, lines), 1))
                .Should().ThrowAsync<NotFoundException>("org A's purchase order is not org B's to invoice");

            Guid? created = null;
            try { created = await b.Invoices().CreateAsync(rig.Request(subtotal: 1000m, grn: grn.UUID), 1); }
            catch (NotFoundException) { }
            if (created is { } uuid)
            {
                var inv = await b.Finance.Invoices.AsNoTracking().SingleAsync(i => i.UUID == uuid);
                (inv.GrnNumber, inv.MatchedGrnValue).Should().Be(((string?)null, 0m), "org A's GRN number and value must not be read into org B's invoice");
            }
        }

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.CountAsync(i => i.PoUuid == po.UUID)).Should().Be(0);
    }

    [Fact]
    public async Task SecurityAudit_approving_an_invoice_never_moves_another_orgs_purchase_order()
    {
        // An org-B invoice that points at org A's PO (written before any create-time check existed): approving it
        // as org B's super admin must not lock or change org A's purchase order.
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));

        Guid invoice;
        await using (var b = SuperAdminInB(rig))
        {
            var inv = new Invoice
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = "INV-B-1", SupplierId = rig.Supplier, SupplierName = "Karachi Steel",
                PoUuid = po.UUID, PoNumber = po.PoNumber, InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16),
                DueDate = new DateTime(2026, 10, 15), Currency = "PKR", Subtotal = 1000m, TotalAmount = 1000m, MatchStatus = "Matched",
                PaymentStatus = "Unpaid", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            inv.Lines.Add(new InvoiceLine { UUID = Guid.NewGuid(), PoLineUuid = lines[0].UUID, LineNo = 1, ItemDescription = "x", QtyInvoiced = 10m, UnitPrice = 100m, LineTotal = 1000m });
            b.Finance.Invoices.Add(inv);
            await b.Finance.SaveChangesAsync();
            b.Finance.ChangeTracker.Clear();
            invoice = inv.UUID;

            try { await b.Invoices().ApproveAsync(invoice, null, 1); }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException or NotFoundException) { }
        }

        var after = await rig.PoNowAsync(po.UUID);
        after.Lines.Single().QtyInvoiced.Should().Be(0m, "org A's purchase order is not org B's to invoice");
        after.Status.Should().Be("RECEIVED");
    }

    // ── Permissions on the payment and note endpoints ────────────────────────

    public static IEnumerable<object[]> PaymentAndNoteActions() =>
        new[] { typeof(SupplierPaymentsController), typeof(PaymentsController), typeof(CreditNotesController), typeof(DebitNotesController) }
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => new object[] { c, m.Name }));

    [Theory]
    [MemberData(nameof(PaymentAndNoteActions))]
    public void SecurityAudit_every_payment_and_note_action_requires_a_permission(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var gated  = method.GetCustomAttributes<RequirePermissionAttribute>().Any()
                  || controller.GetCustomAttributes<RequirePermissionAttribute>().Any();

        gated.Should().BeTrue($"{controller.Name}.{action} moves or reveals money; a login alone must not be enough");
    }
}
