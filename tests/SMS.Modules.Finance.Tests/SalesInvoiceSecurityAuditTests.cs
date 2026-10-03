using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Cross-cutting security audit of the SAP-alignment change (docs/finance/SAP-ALIGNMENT-PLAN.md), sales side:
/// cancelling an issued sales invoice (S-7) across organizations.
/// <para>
/// A platform super admin bypasses the tenant filter, so it can load — and act on — another organization's
/// invoice by uuid (its invoice list shows every organization's invoices). Every row a cancellation writes
/// without an explicit organization is stamped with the <i>caller's</i> organization at SaveChanges. The
/// reversal must therefore either be refused for a foreign invoice (404, as for any other user) or land in
/// the invoice's own organization — never split the books between two tenants.
/// </para>
/// </summary>
public class SalesInvoiceSecurityAuditTests
{
    private const int User = Receivables.User;

    /// <summary>The sales side for one request, as <see cref="ReceivablesScope"/>, but with any tenant context.</summary>
    private sealed class Scope : IAsyncDisposable
    {
        public Scope(ReceivablesWorld world, ITenantContext tenant)
        {
            Db     = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(world.DbName).Options, tenant);
            Demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(world.DbName).Options, tenant);

            var jobs = new Mock<IBackgroundJobClient>();
            jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("fake-job-id");

            var ledger        = new CustomerLedgerService(Db, world.Clock);
            var productLedger = new ProductLedgerService(Db, world.Variants, world.Clock);
            Invoices = new SalesInvoiceService(
                Db, Demand, Mock.Of<IDeliveryFulfillmentReader>(), ledger, productLedger,
                Receivables.Names().Object, Receivables.Lookups().Object, jobs.Object,
                NullLogger<SalesInvoiceService>.Instance, world.Clock);
        }

        public FinanceDbContext    Db       { get; }
        public DemandDbContext     Demand   { get; }
        public SalesInvoiceService Invoices { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Demand.DisposeAsync();
        }
    }

    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<(ReceivablesWorld World, ReceivablesDesk.Invoiced Invoice, Guid Customer)> IssuedInOrgAAsync()
    {
        var world    = new ReceivablesWorld();
        var desk     = world.For(OrgA);
        var customer = desk.NewCustomer();
        var invoice  = await desk.InvoiceAsync(customer, 500m);
        return (world, invoice, customer);
    }

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_cancelling_an_invoice_never_books_the_reversal_into_its_own_org()
    {
        var (world, invoice, customer) = await IssuedInOrgAAsync();

        Exception? refused = null;
        await using (var superAdminInB = new Scope(world, new StaticTenantContext { OrganizationId = OrgB, IsSuperAdmin = true }))
        {
            try { await superAdminInB.Invoices.CancelAsync(invoice.Uuid, "Cancelled from another organization", User); }
            catch (Exception ex) { refused = ex; }
        }

        await using var auditor = Receivables.Auditor(world.DbName);
        var stored = await auditor.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == invoice.Uuid);

        if (refused is not null)
        {
            // Fixed by refusing: a foreign invoice is "not found", exactly as for any other caller, and untouched.
            refused.Should().BeOfType<NotFoundException>("another organization's invoice must be a 404, never a 409/403");
            stored.Status.Should().Be(SalesInvoiceStatuses.Issued);
            return;
        }

        // Fixed by booking in the right place: every row the cancellation wrote belongs to the invoice's organization.
        stored.OrganizationId.Should().Be(OrgA);
        stored.Status.Should().Be(SalesInvoiceStatuses.Cancelled);

        var credits = await auditor.CustomerLedgerEntries.AsNoTracking()
            .Where(e => e.ReferenceId == invoice.Uuid && e.EntryType == CustomerLedgerEntryTypes.CreditNote)
            .ToListAsync();
        credits.Should().ContainSingle();
        credits.Select(e => e.OrganizationId).Should().AllBeEquivalentTo(OrgA,
            "the CREDIT_NOTE that offsets org A's INVOICE debit must be on org A's customer ledger; stamped with the super " +
            "admin's org B it leaves org A's customer owing a cancelled invoice and gives org B a credit for a stranger");

        var returns = await auditor.ProductLedgerEntries.AsNoTracking()
            .Where(e => e.ReferenceId == invoice.Uuid && e.EntryType == ProductLedgerEntryTypes.ReturnIn)
            .ToListAsync();
        returns.Should().NotBeEmpty();
        returns.Select(e => e.OrganizationId).Should().AllBeEquivalentTo(OrgA,
            "the stock coming back belongs to org A's product ledger");

        // Org A's customer, read as org A, nets to nothing after the cancellation.
        (await world.For(OrgA).OwesAsync(customer)).Should().Be(0m);
    }

    [Fact]
    public async Task SecurityAudit_an_ordinary_user_of_another_org_gets_404_on_cancel_and_the_invoice_is_untouched()
    {
        var (world, invoice, customer) = await IssuedInOrgAAsync();

        await using (var userInB = new Scope(world, new StaticTenantContext { OrganizationId = OrgB }))
        {
            var act = async () => await userInB.Invoices.CancelAsync(invoice.Uuid, "not mine", User);
            await act.Should().ThrowAsync<NotFoundException>();
        }

        await using var auditor = Receivables.Auditor(world.DbName);
        (await auditor.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == invoice.Uuid)).Status.Should().Be(SalesInvoiceStatuses.Issued);
        (await auditor.CustomerLedgerEntries.AsNoTracking().CountAsync(e => e.ReferenceId == invoice.Uuid)).Should().Be(1, "only the issue's debit");
        (await world.For(OrgA).OwesAsync(customer)).Should().Be(500m);
    }

    [Fact]
    public async Task SecurityAudit_the_cancel_reason_is_bounded_and_required()
    {
        var (world, invoice, _) = await IssuedInOrgAAsync();

        await using var s = new Scope(world, new StaticTenantContext { OrganizationId = OrgA });

        var tooLong = async () => await s.Invoices.CancelAsync(invoice.Uuid, new string('x', 501), User);
        await tooLong.Should().ThrowAsync<BadRequestException>();

        var blank = async () => await s.Invoices.CancelAsync(invoice.Uuid, "   ", User);
        await blank.Should().ThrowAsync<BadRequestException>();
    }
}
