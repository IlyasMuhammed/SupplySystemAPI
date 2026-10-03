using FluentAssertions;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Integration;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>QuickBooks plan §4 — ProductVariant + Product → ItemPayload, field by field.</summary>
public class VariantPayloadFactoryTests
{
    private static ProductVariant Variant(Action<Product>? product = null, Action<ProductVariant>? variant = null)
    {
        var p = new Product
        {
            Id = 7, Name = "  Portland Cement  ", Sku = "CEM", Description = "  50 kg bag, OPC grade  ",
            ProductType = SMS.Shared.Common.ProductType.StockItem, IsSaleable = true, IsPurchasable = true, IsActive = true
        };
        product?.Invoke(p);
        var v = new ProductVariant
        {
            Uuid = Guid.Parse("12345678-1234-1234-1234-123456789abc"), ProductId = 7, Product = p,
            Sku = " CEM-50KG ", VariantName = "50 kg", PurchasePrice = 1150.50m, SellingPrice = 1399m, IsActive = true
        };
        variant?.Invoke(v);
        return v;
    }

    [Fact]
    public void An_item_carries_every_mapped_field()
    {
        var item = VariantPayloadFactory.Build(Variant(), variantsOnProduct: 1);

        item.ExternalId.Should().Be("12345678-1234-1234-1234-123456789abc");
        item.Name.Should().Be("Portland Cement");
        item.VariantName.Should().BeNull("the product has only this one variant");
        item.Sku.Should().Be("CEM-50KG");
        item.Description.Should().Be("50 kg bag, OPC grade", "the product's description");
        item.Kind.Should().Be(ItemPayloadKind.Goods);
        item.SalesPrice.Should().Be(1399m);
        item.PurchaseCost.Should().Be(1150.50m);
        item.IsSold.Should().BeTrue();
        item.IsPurchased.Should().BeTrue();
        item.IsActive.Should().BeTrue();
    }

    [Fact]
    public void With_more_than_one_variant_the_variant_name_goes_too()
    {
        VariantPayloadFactory.Build(Variant(), variantsOnProduct: 2).VariantName.Should().Be("50 kg");
        VariantPayloadFactory.Build(Variant(), variantsOnProduct: 5).VariantName.Should().Be("50 kg");
    }

    [Theory]
    [InlineData("Portland Cement")]
    [InlineData("  portland cement ")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_variant_name_that_says_nothing_beyond_the_product_is_not_sent(string variantName)
    {
        // The auto-created default variant is named after its product: "Cement - Cement" adds nothing.
        var item = VariantPayloadFactory.Build(Variant(variant: v => v.VariantName = variantName), variantsOnProduct: 3);

        item.VariantName.Should().BeNull();
    }

    [Theory]
    [InlineData("SERVICE", ItemPayloadKind.Service)]
    [InlineData("service", ItemPayloadKind.Service)]
    [InlineData("STOCK_ITEM", ItemPayloadKind.Goods)]
    [InlineData("RAW_MATERIAL", ItemPayloadKind.Goods)]
    [InlineData("COMPONENT", ItemPayloadKind.Goods)]
    [InlineData("SEMI_FINISHED", ItemPayloadKind.Goods)]
    [InlineData("FINISHED_GOOD", ItemPayloadKind.Goods)]
    [InlineData("CONSUMABLE", ItemPayloadKind.Goods)]
    [InlineData("ASSET", ItemPayloadKind.Goods)]
    public void Only_a_service_product_is_a_service_item(string productType, ItemPayloadKind expected)
    {
        VariantPayloadFactory.Build(Variant(p => p.ProductType = productType), 1).Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(false, true,  false)]
    [InlineData(true,  false, false)]
    [InlineData(false, false, false)]
    public void Active_needs_both_the_variant_and_its_product(bool variantActive, bool productActive, bool expected)
    {
        var item = VariantPayloadFactory.Build(
            Variant(p => p.IsActive = productActive, v => v.IsActive = variantActive), 1);

        item.IsActive.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Sold_and_purchased_come_from_the_product(bool saleable, bool purchasable)
    {
        var item = VariantPayloadFactory.Build(Variant(p => { p.IsSaleable = saleable; p.IsPurchasable = purchasable; }), 1);

        item.IsSold.Should().Be(saleable);
        item.IsPurchased.Should().Be(purchasable);
    }

    [Fact]
    public void No_selling_price_and_no_description_are_sent_as_none()
    {
        var item = VariantPayloadFactory.Build(Variant(p => p.Description = "  ", v => v.SellingPrice = null), 1);

        item.SalesPrice.Should().BeNull();
        item.Description.Should().BeNull();
        item.PurchaseCost.Should().Be(1150.50m, "a purchase price always exists on a variant");
    }

    [Fact]
    public void An_inactive_variant_counts_itself_so_a_retired_variant_keeps_its_name()
    {
        var active   = Variant();
        var inactive = Variant(variant: v => v.IsActive = false);

        VariantPayloadFactory.NamingCount(activeVariantsOfProduct: 1, active).Should().Be(1);
        VariantPayloadFactory.NamingCount(activeVariantsOfProduct: 1, inactive).Should().Be(2);
    }

    [Fact]
    public void A_variant_loaded_without_its_product_is_a_programming_error()
    {
        var v = Variant();
        v.Product = null!;

        FluentActions.Invoking(() => VariantPayloadFactory.Build(v, 1)).Should().Throw<InvalidOperationException>();
    }
}
