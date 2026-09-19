using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// Rate card import for a supplier with no rate cards yet. Before the fix every such row needed the
// organization's base currency, which no organization had (and no screen sets) — the preview called
// them valid "New Link" rows and confirm then rejected every one, so the import created nothing.
public class RateCardImportCurrencyTests
{
    private const int User = 7;

    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();

    private sealed record Harness(InventoryDbContext Db, VariantSupplierService Service, Guid Supplier);

    private static async Task<Harness> NewHarness(Guid? baseCurrency)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var product = new Product { Uuid = Guid.NewGuid(), Sku = "CEMENT", Name = "Cement", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "CEMENT-50KG", VariantName = "50 kg bag",
            PurchasePrice = 1200m, IsDefault = true, IsActive = true, CreatedDate = DateTime.UtcNow
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var currency = new Mock<IOrganizationCurrencyService>();
        currency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(baseCurrency);

        var prices = new Mock<IPurchaseOrderPriceLookupService>();
        prices.Setup(p => p.GetLastPricesAsync(It.IsAny<IReadOnlyList<(Guid VariantUuid, Guid SupplierId)>>()))
              .ReturnsAsync(new Dictionary<(Guid VariantUuid, Guid SupplierId), LastPoInfo>());

        var service = new VariantSupplierService(
            db, tenant, currency.Object, prices.Object,
            Mock.Of<ISupplierNameLookupService>(), Mock.Of<ISupplierScoreLookupService>(), Mock.Of<IUserQueryService>(),
            new ConfigurationBuilder().Build());

        return new Harness(db, service, Guid.NewGuid());
    }

    private static MemoryStream Workbook(params (string sku, decimal rate)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Rates");
        ws.Cell(1, 1).Value = "Variant SKU";
        ws.Cell(1, 2).Value = "Current Rate";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].sku;
            ws.Cell(i + 2, 2).Value = rows[i].rate;
        }

        var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task With_no_base_currency_and_none_chosen_the_preview_says_so_instead_of_promising_a_new_rate_card()
    {
        var h = await NewHarness(baseCurrency: null);

        var preview = await h.Service.PreviewImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier);

        preview.Single().Error.Should().Contain("Choose a currency");

        var result = await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier, User);
        result.CreatedCount.Should().Be(0);
        result.Errors.Single().Message.Should().Contain("Choose a currency");
    }

    [Fact]
    public async Task A_currency_chosen_in_the_dialog_lets_the_import_create_rate_cards_that_then_list()
    {
        // The reported symptom: nothing ever showed on the Rate Cards screen.
        var h = await NewHarness(baseCurrency: null);

        var preview = await h.Service.PreviewImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier, Pkr);
        preview.Single().Error.Should().BeNull();
        preview.Single().NewRecord.Should().BeTrue();

        var result = await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier, User, Pkr);
        result.CreatedCount.Should().Be(1);
        result.Errors.Should().BeEmpty();

        var list = await h.Service.GetListAsync(new RateCardListFilter { SupplierId = h.Supplier });
        list.TotalRecords.Should().Be(1);
        list.Data.Single().VendorUnitCost.Should().Be(1150m);
        list.Data.Single().CurrencyId.Should().Be(Pkr);
    }

    [Fact]
    public async Task Without_a_chosen_currency_the_organizations_base_currency_is_still_used()
    {
        var h = await NewHarness(baseCurrency: Usd);

        var result = await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 9.5m)), h.Supplier, User);

        result.CreatedCount.Should().Be(1);
        (await h.Db.VariantSuppliers.SingleAsync()).CurrencyId.Should().Be(Usd);
    }

    [Fact]
    public async Task A_chosen_currency_wins_over_the_base_currency()
    {
        var h = await NewHarness(baseCurrency: Usd);

        await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier, User, Pkr);

        (await h.Db.VariantSuppliers.SingleAsync()).CurrencyId.Should().Be(Pkr);
    }

    [Fact]
    public async Task Updating_an_existing_rate_card_needs_no_currency_and_keeps_its_own()
    {
        var h = await NewHarness(baseCurrency: null);
        await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 1150m)), h.Supplier, User, Pkr);

        var preview = await h.Service.PreviewImportAsync(Workbook(("CEMENT-50KG", 1190m)), h.Supplier);
        preview.Single().Error.Should().BeNull();
        preview.Single().RateChanged.Should().BeTrue();

        var result = await h.Service.ConfirmImportAsync(Workbook(("CEMENT-50KG", 1190m)), h.Supplier, User, Usd);

        result.UpdatedCount.Should().Be(1);
        var card = await h.Db.VariantSuppliers.SingleAsync();
        card.VendorUnitCost.Should().Be(1190m);
        card.CurrencyId.Should().Be(Pkr, "the dialog's currency is only for new rate cards");
    }
}
