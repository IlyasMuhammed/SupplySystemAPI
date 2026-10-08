using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A35 P3-05 / P3-10 / P3-12 / P3-15 + D-8 — the purchase order's currency (new in A35): supplier default → PO (T-C4-04),
/// override, rate locked at APPROVED through the workflow's status handler (refused up front without a rate, D-5), the
/// AUTO_SEND deficit PO, the reissue clone, and Demand's ICurrencyUsageChecker.
/// </summary>
public class A35PurchaseOrderCurrencyTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid UsdSupplier = Guid.NewGuid();

    private sealed record H(DemandDbContext Db, PurchaseOrderRepository Repo, FakeCurrencyService Currency, StaticTenantContext Tenant,
        Mock<IOrganizationCurrencyService> OrgCurrency);

    private static H NewHarness(bool withFinance = true)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"PO-2026-{++seq:D5}");
        var currency = new FakeCurrencyService();
        currency.Bases[TransactionDomain.Sale] = Pkr;
        currency.Bases[TransactionDomain.Purchase] = Pkr;
        currency.Codes[Usd] = "USD"; currency.Codes[Eur] = "EUR";
        var defaults = new FakePartnerCurrencyDefaults(currency.Bases);
        defaults.Partners[UsdSupplier] = (null, Usd);
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>(), It.IsAny<TransactionDomain>(), It.IsAny<CancellationToken>())).ReturnsAsync(Pkr);
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance, numbers.Object,
            currency: withFinance ? currency : null, partnerCurrencies: defaults, orgCurrency: orgCurrency.Object);
        return new H(db, repo, currency, tenant, orgCurrency);
    }

    private static CreatePoRequest Po(Guid supplier, Guid? currency = null) => new()
    {
        SupplierId = supplier, SupplierName = "Vendor", CurrencyId = currency,
        Lines = [new CreatePoLineRequest { ItemDescription = "Cable", Quantity = 10m, UnitPrice = 5m }]
    };

    private static Task<PurchaseOrder> Stored(H h, Guid uuid) =>
        h.Db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == uuid);

    [Fact]
    public async Task T_C4_04_a_supplier_with_a_USD_default_gets_a_USD_PO()
    {
        var h = NewHarness();
        (await Stored(h, await h.Repo.CreateAsync(Po(UsdSupplier), 1))).CurrencyId.Should().Be(Usd);
    }

    [Fact]
    public async Task A_supplier_without_a_default_gets_the_purchase_base_and_an_explicit_currency_wins()
    {
        var h = NewHarness();
        (await Stored(h, await h.Repo.CreateAsync(Po(Guid.NewGuid()), 1))).CurrencyId.Should().Be(Pkr);
        (await Stored(h, await h.Repo.CreateAsync(Po(UsdSupplier, Eur), 1))).CurrencyId.Should().Be(Eur);
    }

    [Fact]
    public async Task A_draft_PO_can_change_currency_and_an_edit_without_one_keeps_it()
    {
        var h = NewHarness();
        var uuid = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        await h.Repo.UpdateAsync(uuid, new PatchPoRequest { Notes = "x" }, 1);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Usd);
        await h.Repo.UpdateAsync(uuid, new PatchPoRequest { CurrencyId = Eur }, 1);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Eur);

        var detail = (await h.Repo.GetByIdAsync(uuid))!;
        detail.CurrencyId.Should().Be(Eur);
        detail.ExchangeRate.Should().BeNull();
    }

    [Fact]
    public async Task Approval_through_the_workflow_locks_the_rate_and_the_base_amounts()
    {
        var h = NewHarness();
        h.Currency.Rate(Usd, 278.05m);
        var uuid = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        var handler = new PoStatusHandler(h.Db, h.Currency, h.OrgCurrency.Object);

        await handler.UpdateStatusAsync(uuid, "APPROVED");

        var po = await Stored(h, uuid);
        po.Status.Should().Be("APPROVED");
        po.ExchangeRate.Should().Be(278.05m);
        po.BaseCurrencyId.Should().Be(Pkr);
        po.RateLockedAt.Should().NotBeNull();
        po.Lines.Single().UnitPriceBase.Should().Be(1_390.25m);
        po.Lines.Single().LineTotalBase.Should().Be(13_902.50m);
        po.TotalAmountBase.Should().Be(13_902.50m);
        h.Currency.Locks.Single().Domain.Should().Be(TransactionDomain.Purchase);

        var detail = (await h.Repo.GetByIdAsync(uuid))!;
        detail.TotalAmountBase.Should().Be(13_902.50m);
        detail.Lines.Single().LineTotalBase.Should().Be(13_902.50m);
        (await h.Repo.GetListAsync(new PoListFilter())).Data.Single().TotalAmountBase.Should().Be(13_902.50m);

        // BR-C5-06 — never re-locked, and the currency can no longer change.
        h.Currency.Rates.Clear();
        h.Currency.Rate(Usd, 300m);
        await handler.UpdateStatusAsync(uuid, "APPROVED");
        (await Stored(h, uuid)).ExchangeRate.Should().Be(278.05m);
    }

    [Fact]
    public async Task REV_11_a_PO_sent_back_to_DRAFT_drops_its_lock_and_relocks_at_the_new_rate_on_reapproval()
    {
        var h = NewHarness();
        h.Currency.Rate(Usd, 278.05m);
        var uuid = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        var handler = new PoStatusHandler(h.Db, h.Currency, h.OrgCurrency.Object);
        await handler.UpdateStatusAsync(uuid, "APPROVED");

        await handler.UpdateStatusAsync(uuid, "CANCELLED"); // the workflow's cancel → DRAFT
        var draft = await Stored(h, uuid);
        draft.Status.Should().Be("DRAFT");
        draft.ExchangeRate.Should().BeNull();
        draft.RateLockedAt.Should().BeNull();
        draft.TotalAmountBase.Should().BeNull();
        draft.Lines.Single().LineTotalBase.Should().BeNull();
        draft.Lines.Single().UnitPriceBase.Should().BeNull();

        h.Currency.Rates.Clear();
        h.Currency.Rate(Usd, 300m);
        await handler.UpdateStatusAsync(uuid, "APPROVED");
        var po = await Stored(h, uuid);
        po.ExchangeRate.Should().Be(300m);
        po.TotalAmountBase.Should().Be(15_000m);
    }

    [Fact]
    public async Task A_PO_in_the_purchase_base_locks_at_1()
    {
        var h = NewHarness();
        var uuid = await h.Repo.CreateAsync(Po(Guid.NewGuid()), 1);
        await new PoStatusHandler(h.Db, h.Currency, h.OrgCurrency.Object).UpdateStatusAsync(uuid, "APPROVED");
        var po = await Stored(h, uuid);
        po.ExchangeRate.Should().Be(1m);
        po.TotalAmountBase.Should().Be(50m);
    }

    [Fact]
    public async Task Approving_a_foreign_currency_PO_without_a_rate_is_refused_before_the_workflow_records_it()
    {
        var h = NewHarness();
        var uuid = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        var workflow = new Mock<IWorkflowActionService>();
        var svc = new PurchaseOrderService(h.Repo, workflow.Object, Mock.Of<IWorkflowInboxService>(), Mock.Of<IBackgroundJobClient>(),
            Mock.Of<ITimelineService>(), h.Currency, h.OrgCurrency.Object, h.Tenant);

        var act = () => svc.ApproveAsync(uuid, 5);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("No exchange rate for USD on *");
        workflow.Verify(w => w.ApproveByDocumentAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task An_auto_sent_deficit_PO_is_locked_at_creation_or_created_as_a_draft_when_no_rate_exists()
    {
        var h = NewHarness();
        h.Currency.Rate(Usd, 278m);
        var locked = await h.Repo.CreateFromSaleOrderDeficitAsync(new SaleOrderDeficitPo(
            "BACK_TO_BACK", "APPROVED", Guid.NewGuid(), 1, 1, "SO-1", UsdSupplier, "Vendor", Guid.NewGuid(), 2m, 10m, null), 1);
        locked.Status.Should().Be("APPROVED");
        (await Stored(h, locked.Uuid)).TotalAmountBase.Should().Be(5_560m);

        h.Currency.Rates.Clear();
        var refused = await h.Repo.CreateFromSaleOrderDeficitAsync(new SaleOrderDeficitPo(
            "BACK_TO_BACK", "APPROVED", Guid.NewGuid(), 3, 3, "SO-3", UsdSupplier, "Vendor", Guid.NewGuid(), 2m, 10m, null), 1);
        refused.Status.Should().Be("DRAFT", "no USD rate — left for a person to approve once one is entered");
        var stored = await Stored(h, refused.Uuid);
        stored.Status.Should().Be("DRAFT");
        stored.ExchangeRate.Should().BeNull();
        stored.CurrencyId.Should().Be(Usd);
    }

    [Fact]
    public async Task A_reissue_clone_keeps_the_currency_but_not_the_rate()
    {
        var h = NewHarness();
        h.Currency.Rate(Usd, 278.05m);
        var uuid = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        await new PoStatusHandler(h.Db, h.Currency, h.OrgCurrency.Object).UpdateStatusAsync(uuid, "APPROVED");

        var clone = await Stored(h, await new PoCloneHandler(h.Db).CloneDocumentAsync(uuid));

        clone.CurrencyId.Should().Be(Usd);
        clone.ExchangeRate.Should().BeNull();
        clone.TotalAmountBase.Should().BeNull();
    }

    // ── D-8: Demand's usage checker ──────────────────────────────────────────

    [Fact]
    public async Task The_usage_checker_reports_locked_documents_of_the_organization_only()
    {
        var h = NewHarness();
        var org = h.Tenant.OrganizationId;
        h.Currency.Rate(Usd, 278m);
        var draftPo = await h.Repo.CreateAsync(Po(UsdSupplier), 1);
        var checker = new DemandCurrencyUsageChecker(h.Db);

        (await checker.DescribeCurrencyUsageAsync(org, Usd)).Should().BeNull("a draft PO locks nothing");
        (await checker.DescribeDomainBaseUsageAsync(org, TransactionDomain.Purchase)).Should().BeNull();

        await new PoStatusHandler(h.Db, h.Currency, h.OrgCurrency.Object).UpdateStatusAsync(draftPo, "APPROVED");
        h.Db.SaleOrders.Add(new SaleOrder { OrganizationId = org, SoNumber = "SO-1", CurrencyId = Eur, Status = "CONFIRMED", DeliveryMode = "SHIP" });
        h.Db.SaleOrders.Add(new SaleOrder { OrganizationId = org, SoNumber = "SO-2", CurrencyId = Eur, Status = "DRAFT", DeliveryMode = "SHIP" });
        h.Db.SaleQuotations.Add(new SaleQuotation { OrganizationId = org, QuotationNumber = "SQ-1", CurrencyId = Eur, Status = "SENT", SentAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        (await checker.DescribeCurrencyUsageAsync(org, Usd)).Should().Be("1 approved purchase order");
        (await checker.DescribeCurrencyUsageAsync(org, Pkr)).Should().Be("1 approved purchase order", "PKR is its base");
        (await checker.DescribeCurrencyUsageAsync(org, Eur)).Should().Be("1 confirmed sale order, 1 sent sale quotation");
        (await checker.DescribeDomainBaseUsageAsync(org, TransactionDomain.Sale)).Should().Be("1 confirmed sale order, 1 sent sale quotation");
        (await checker.DescribeDomainBaseUsageAsync(org, TransactionDomain.Purchase)).Should().Be("1 approved purchase order");
        (await checker.DescribeDomainBaseUsageAsync(org, TransactionDomain.Service)).Should().BeNull();
        (await checker.DescribeCurrencyUsageAsync(Guid.NewGuid(), Usd)).Should().BeNull("another organization");
    }
}
