using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A49 E-13 (A-14): a manual service invoice, with no sale order or delivery, through the normal receivables path.</summary>
public class ServiceInvoiceTests
{
    private static readonly Guid Customer = Guid.NewGuid();

    private static async Task<(A48ArWorld World, ArScope Scope, ServiceInvoiceService Service)> NewAsync()
    {
        var w = new A48ArWorld();
        w.Partners.Add(new PartnerInfo(Customer, "C-1", "Acme Services", true, false, true, null, null, null, null, null, null, 30, null));
        var s = w.Scope();
        s.Db.TaxCodes.Add(new TaxCode { OrganizationId = w.Org, Code = "GST17", Name = "GST 17", RatePercent = 17m, IsActive = true });
        await s.Db.SaveChangesAsync();
        return (w, s, new ServiceInvoiceService(s.Db, s.Invoices, new ArDocumentCurrency(), w.Partners, w.Clock));
    }

    private static CreateServiceInvoiceRequest Request(string? currency = "PKR", string? tax = "GST17") => new()
    {
        CustomerUuid = Customer, InvoiceDate = new DateOnly(2026, 10, 1), CurrencyCode = currency,
        Lines =
        [
            new ServiceInvoiceLineRequest { Description = "Consulting", Quantity = 2m, UnitPrice = 500m, TaxCode = tax },
            new ServiceInvoiceLineRequest { Description = "Support", Quantity = 1m, UnitPrice = 100m }
        ]
    };

    [Fact]
    public async Task A_draft_with_no_sale_order_no_delivery_and_non_stock_lines()
    {
        var (_, s, service) = await NewAsync();
        await using var _s = s;

        var detail = await service.CreateAsync(Request(), 5);

        var row = await s.Db.SalesInvoices.IgnoreQueryFilters().Include(i => i.Lines).SingleAsync(i => i.UUID == detail.Uuid);
        row.SourceType.Should().Be(SalesInvoiceSources.ServiceManual);
        (row.SaleOrderUuid, row.DeliveryUuid).Should().Be((null, null));
        row.Status.Should().Be(SalesInvoiceStatuses.Draft);
        row.InvoiceNumber.Should().StartWith("SINV-20261001-");
        (row.Subtotal, row.TaxAmount, row.GrandTotal).Should().Be((1100m, 170m, 1270m));
        row.Lines.Should().OnlyContain(l => l.VariantUuid == Guid.Empty);
        row.Lines.Single(l => l.TaxCode == "GST17").TaxPercent.Should().Be(17m);
        row.DueDate.Should().Be(new DateTime(2026, 10, 31));
    }

    [Fact]
    public async Task Issuing_debits_the_customer_and_touches_no_stock_and_posts_only_lines_to_the_gl()
    {
        var (_, s, service) = await NewAsync();
        await using var _s = s;
        var detail = await service.CreateAsync(Request(), 5);

        var issued = await s.Invoices.IssueAsync(detail.Uuid, 5);

        issued.Status.Should().Be(SalesInvoiceStatuses.Issued);
        issued.PartnerBalance.Should().Be(1270m);
        (await s.Db.CustomerLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.PartnerId == Customer)).EntryType.Should().Be(CustomerLedgerEntryTypes.Invoice);
        (await s.Db.ProductLedgerEntries.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();

        var row = await s.Db.SalesInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == detail.Uuid);
        var tx = SalesPostings.Invoice(row);
        tx.Lines.Where(l => l.AccountRole == PostingRoles.Revenue).Sum(l => l.Credit).Should().Be(1100m);
        tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));
        var document = tx.Metadata!.Document.Should().BeOfType<SMS.Shared.Integration.QuickBooks.SalesInvoicePayload>().Subject;
        document.Lines.Should().OnlyContain(l => l.ItemExternalId == SMS.Shared.Integration.QuickBooks.ServiceInvoiceLines.ItemExternalId);
        document.PrivateNote.Should().Be("Service invoice");
    }

    [Fact]
    public async Task Every_other_invoice_still_carries_its_document()
    {
        var invoice = new SalesInvoice { UUID = Guid.NewGuid(), InvoiceNumber = "SINV-1", GrandTotal = 100m, CurrencyCode = "PKR", SourceType = SalesInvoiceSources.SaleOrder };
        invoice.Lines.Add(new SalesInvoiceLine { LineNo = 1, VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 100m });
        SalesPostings.Invoice(invoice).Metadata!.Document.Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        var (_, s, service) = await NewAsync();
        await using var _s = s;

        await service.Invoking(x => x.CreateAsync(new CreateServiceInvoiceRequest { CustomerUuid = Customer }, 1)).Should().ThrowAsync<BadRequestException>();
        var unknown = Request();
        unknown.CustomerUuid = Guid.NewGuid();
        await service.Invoking(x => x.CreateAsync(unknown, 1)).Should().ThrowAsync<NotFoundException>();
        await service.Invoking(x => x.CreateAsync(Request(tax: "NOPE"), 1)).Should().ThrowAsync<BadRequestException>();


        var negative = Request();
        negative.Lines[0].UnitPrice = -1m;
        await service.Invoking(x => x.CreateAsync(negative, 1)).Should().ThrowAsync<BadRequestException>();
    }

    private sealed class Chart : IExternalAccountMapReader
    {
        public Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default) => Task.FromResult(new ExternalAccountMap());
        public Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChartAccountRef>>(
        [
            new(Guid.NewGuid(), "4100", "Consulting", "Revenue", true), new(Guid.NewGuid(), "4200", "Other", "OtherIncome", true),
            new(Guid.NewGuid(), "4900", "Closed", "Revenue", false), new(Guid.NewGuid(), "6000", "Rent", "Expense", true)
        ]);
        public Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("4900")]
    [InlineData("6000")]
    public async Task An_income_account_must_be_an_active_income_leaf(string code)
    {
        var (w, s, _) = await NewAsync();
        await using var _s = s;
        var service = new ServiceInvoiceService(s.Db, s.Invoices, new ArDocumentCurrency(), w.Partners, w.Clock, new Chart());
        var request = Request();
        request.Lines[0].IncomeAccountCode = code;
        (await service.Invoking(x => x.CreateAsync(request, 1)).Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().StartWith("SERVICE_INCOME_ACCOUNT_INVALID");
    }

    [Fact]
    public async Task Lines_post_revenue_to_their_own_accounts_and_the_rest_to_the_role_and_it_balances()
    {
        var (w, s, _) = await NewAsync();
        await using var _s = s;
        var service = new ServiceInvoiceService(s.Db, s.Invoices, new ArDocumentCurrency(), w.Partners, w.Clock, new Chart());
        var request = Request();
        request.Lines[0].IncomeAccountCode = "4100";

        var detail = await service.CreateAsync(request, 1);
        detail.Lines.Single(l => l.LineNo == 1).IncomeAccountCode.Should().Be("4100");

        var row = await s.Db.SalesInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == detail.Uuid);
        var tx = SalesPostings.Invoice(row);
        tx.Lines.Single(l => l.AccountCode == "4100").Credit.Should().Be(1000m);
        tx.Lines.Single(l => l.AccountRole == PostingRoles.Revenue).Credit.Should().Be(100m);
        tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));

        // Every line its own account: no role line, rounding included in the last.
        foreach (var line in row.Lines) line.IncomeAccountCode = "4200";
        var all = SalesPostings.Invoice(row);
        all.Lines.Should().NotContain(l => l.AccountRole == PostingRoles.Revenue);
        all.Lines.Single(l => l.AccountCode == "4200").Credit.Should().Be(1100m);
    }

    [Fact]
    public async Task Another_organizations_tax_code_is_not_found()
    {
        var (w, s, service) = await NewAsync();
        await using var _s = s;
        s.Db.TaxCodes.Add(new TaxCode { OrganizationId = Guid.NewGuid(), Code = "OTHER", Name = "Other", RatePercent = 5m, IsActive = true });
        await s.Db.SaveChangesAsync();

        await service.Invoking(x => x.CreateAsync(Request(tax: "OTHER"), 1)).Should().ThrowAsync<BadRequestException>();
        w.Org.Should().NotBeEmpty();
    }

    [Fact]
    public void The_route_is_gated_by_finance_and_the_invoice_permission()
    {
        typeof(ServiceInvoicesController).GetCustomAttributes(typeof(SMS.Shared.Authorization.RequiresFeatureAttribute), true).Should().NotBeEmpty();
        typeof(ServiceInvoicesController).GetMethod(nameof(ServiceInvoicesController.Create))!
            .GetCustomAttributes(typeof(SMS.Shared.Authorization.RequirePermissionAttribute), true).Should().ContainSingle();
    }
}
