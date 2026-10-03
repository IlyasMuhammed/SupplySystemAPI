using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// Cross-cutting security audit of the SAP-alignment change, purchase side: reversing an approved supplier
/// invoice (S-7) and the purchase tax code (S-3) across organizations.
/// <para>
/// A platform super admin bypasses the tenant filter, so it can load — and act on — another organization's
/// invoice by uuid. A reversal posts a supplier ledger entry and a master ledger entry that carry no explicit
/// organization, so they are stamped with the <i>caller's</i> organization at SaveChanges. The reversal must
/// therefore either be refused for a foreign invoice (404, as for any other user) or land in the invoice's
/// own organization.
/// </para>
/// </summary>
public class SupplierInvoiceSecurityAuditTests
{
    private static readonly Guid OrgB = Guid.NewGuid();

    /// <summary>The purchase side for one request against <paramref name="rig"/>'s database, as <paramref name="tenant"/>.</summary>
    private sealed class Desk : IAsyncDisposable
    {
        public Desk(PurchaseRig rig, ITenantContext tenant, ITaxCodeLookup? taxCodes = null)
        {
            Finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
            Demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
            Warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);

            var repo = new InvoiceRepository(Finance, Demand, Warehouse, new SupplierLedgerService(Finance), new FakeSupplierNameLookup(),
                taxCodes ?? new TaxCodeLookup(Finance), rig.Rates, rig.BaseCurrency, rig.BaseCurrency);
            Service = new InvoiceService(repo, new Mock<IBackgroundJobClient>().Object);
        }

        public FinanceDbContext   Finance   { get; }
        public DemandDbContext    Demand    { get; }
        public WarehouseDbContext Warehouse { get; }
        public InvoiceService     Service   { get; }

        public async ValueTask DisposeAsync()
        {
            await Finance.DisposeAsync();
            await Demand.DisposeAsync();
            await Warehouse.DisposeAsync();
        }
    }

    private static FinanceDbContext Auditor(PurchaseRig rig) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    [Fact]
    public async Task SecurityAudit_super_admin_in_another_org_reversing_an_invoice_never_books_the_reversal_into_its_own_org()
    {
        var rig = PurchaseRig.New();
        var (invoice, _, _) = await rig.ApprovedAsync();

        var reversed = false;
        Exception? refused = null;
        await using (var superAdminInB = new Desk(rig, new StaticTenantContext { OrganizationId = OrgB, IsSuperAdmin = true }))
        {
            try { reversed = await superAdminInB.Service.ReverseAsync(invoice, "Reversed from another organization", PurchaseRig.User); }
            catch (Exception ex) { refused = ex; }
        }

        await using var auditor = Auditor(rig);
        var stored = await auditor.Invoices.AsNoTracking().SingleAsync(i => i.UUID == invoice);

        if (!reversed)
        {
            // Fixed by refusing: the controller turns false into a 404 (a NotFoundException is a 404 too); untouched.
            if (refused is not null) refused.Should().BeOfType<NotFoundException>("another organization's invoice must be a 404, never a 409/403");
            stored.MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
            return;
        }

        // Fixed by booking in the right place: every row the reversal wrote belongs to the invoice's organization.
        stored.OrganizationId.Should().Be(rig.Org);
        stored.MatchStatus.Should().Be(InvoiceMatchStatus.Reversed);

        var supplierLedger = await auditor.SupplierLedgerEntries.AsNoTracking()
            .Where(e => e.ReferenceId == invoice && e.TransactionType == InvoiceRepository.ReversedLedgerType)
            .ToListAsync();
        supplierLedger.Should().ContainSingle();
        supplierLedger.Select(e => e.OrganizationId).Should().AllBeEquivalentTo(rig.Org,
            "the INVOICE_REVERSED credit offsets org A's INVOICE_APPROVED debit, so it belongs on org A's supplier ledger; " +
            "stamped with the super admin's org B, org A still owes the reversed invoice and org B gets a credit for a stranger");

        var master = await auditor.MasterFinancialLedgers.AsNoTracking()
            .Where(e => e.ReferenceId == invoice && e.TransactionType == InvoiceRepository.ReversedLedgerType)
            .ToListAsync();
        master.Select(e => e.OrganizationId).Should().AllBeEquivalentTo(rig.Org, "the master ledger mirrors the supplier ledger");
    }

    [Fact]
    public async Task SecurityAudit_an_ordinary_user_of_another_org_gets_not_found_on_reverse_and_the_invoice_is_untouched()
    {
        var rig = PurchaseRig.New();
        var (invoice, _, _) = await rig.ApprovedAsync();

        await using (var userInB = new Desk(rig, new StaticTenantContext { OrganizationId = OrgB }))
        {
            // False is what InvoicesController.Reverse turns into a 404 — not a 409 that would confirm the invoice exists.
            (await userInB.Service.ReverseAsync(invoice, "not mine", PurchaseRig.User)).Should().BeFalse();
        }

        await using var auditor = Auditor(rig);
        (await auditor.Invoices.AsNoTracking().SingleAsync(i => i.UUID == invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
        (await auditor.SupplierLedgerEntries.AsNoTracking().CountAsync(e => e.ReferenceId == invoice)).Should().Be(1, "only the approval's debit");
    }

    [Fact]
    public async Task SecurityAudit_another_orgs_reversed_or_pending_invoice_is_not_found_before_any_state_check_leaks_its_status()
    {
        // The state checks (409 "already reversed", "only an approved invoice") must come after the tenant check, or a
        // 409 would confirm to another organization that the uuid exists and what state it is in.
        var rig = PurchaseRig.New();
        var (approved, _, _) = await rig.ApprovedAsync();
        (await rig.Service.ReverseAsync(approved, "Entered twice", PurchaseRig.User)).Should().BeTrue();
        rig.Forget();

        var (po, lines) = await rig.PoAsync(true, (5m, 10m));
        var pending = await rig.CreateAsync(rig.Request(po, lines));

        await using var userInB = new Desk(rig, new StaticTenantContext { OrganizationId = OrgB });
        foreach (var uuid in new[] { approved, pending })
        {
            (await userInB.Service.ReverseAsync(uuid, "probe", PurchaseRig.User)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task SecurityAudit_a_purchase_tax_code_of_another_org_cannot_be_put_on_an_invoice()
    {
        var rig = PurchaseRig.New();

        // Org B owns a PURCHASE code; org A's user (the rig's tenant) tries to use its uuid.
        var foreignCode = new TaxCode
        {
            Uuid = Guid.NewGuid(), OrganizationId = OrgB, Code = "WHT10", Name = "WHT 10%", RatePercent = 10m,
            Usage = TaxCodeUsages.Purchase, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        await using (var seed = Auditor(rig))
        {
            seed.TaxCodes.Add(foreignCode);
            await seed.SaveChangesAsync();
        }

        await using var userInA = new Desk(rig, new StaticTenantContext { OrganizationId = rig.Org });
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));

        var create = async () => await userInA.Service.CreateAsync(rig.Request(po, lines, taxCode: foreignCode.Uuid), PurchaseRig.User);
        (await create.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().NotContain("WHT10",
            "the refusal must not reveal another organization's code");

        var uuid = await userInA.Service.CreateAsync(rig.Request(po, lines), PurchaseRig.User);
        var patch = async () => await userInA.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = foreignCode.Uuid }, PurchaseRig.User);
        (await patch.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().NotContain("WHT10");
    }

    [Fact]
    public async Task SecurityAudit_the_reversal_reason_is_bounded_and_required()
    {
        var rig = PurchaseRig.New();
        var (invoice, _, _) = await rig.ApprovedAsync();

        var tooLong = async () => await rig.Service.ReverseAsync(invoice, new string('x', InvoiceRepository.ReversalReasonMaxLength + 1), PurchaseRig.User);
        await tooLong.Should().ThrowAsync<BadRequestException>();

        var blank = async () => await rig.Service.ReverseAsync(invoice, "  ", PurchaseRig.User);
        await blank.Should().ThrowAsync<BadRequestException>();

        rig.Forget();
        (await rig.LoadAsync(invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
    }
}
