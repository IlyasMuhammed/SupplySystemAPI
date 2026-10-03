using FluentAssertions;
using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QboItemMapperTests
{
    [Fact]
    public void Item_maps_every_field_and_flag()
    {
        var item = QboItemMapper.ToSdk(QboTestKit.FullItem());

        item.Name.Should().Be("Widget - Blue");
        item.Sku.Should().Be("WID-BLU");
        item.Type.Should().Be(ItemTypeEnum.NonInventory);
        item.TypeSpecified.Should().BeTrue();
        item.Description.Should().Be("A blue widget");
        item.UnitPrice.Should().Be(12.50m);
        item.UnitPriceSpecified.Should().BeTrue();
        item.PurchaseDesc.Should().Be("Blue widget, bought");
        item.PurchaseCost.Should().Be(7.25m);
        item.PurchaseCostSpecified.Should().BeTrue();
        item.IncomeAccountRef!.Value.Should().Be("79");
        item.ExpenseAccountRef!.Value.Should().Be("80");
        item.Active.Should().BeTrue();
        item.ActiveSpecified.Should().BeTrue();
    }

    [Fact]
    public void Service_items_are_Service()
    {
        var source = QboTestKit.FullItem();
        source.Type = RemoteItemType.Service;

        QboItemMapper.ToSdk(source).Type.Should().Be(ItemTypeEnum.Service);
    }

    [Fact]
    public void Inventory_is_never_produced()
    {
        foreach (var type in Enum.GetValues<RemoteItemType>())
            QboItemMapper.ToSdkType(type).Should().NotBe(ItemTypeEnum.Inventory);

        var act = () => QboItemMapper.ToSdkType((RemoteItemType)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Item_with_only_a_name_leaves_optional_fields_unset()
    {
        var item = QboItemMapper.ToSdk(new RemoteItem { Name = "Bare" });

        item.Sku.Should().BeNull();
        item.Description.Should().BeNull();
        item.PurchaseDesc.Should().BeNull();
        item.UnitPriceSpecified.Should().BeFalse();
        item.PurchaseCostSpecified.Should().BeFalse();
        item.IncomeAccountRef.Should().BeNull();
        item.ExpenseAccountRef.Should().BeNull();
        item.TrackQtyOnHandSpecified.Should().BeFalse("stock is never tracked in QuickBooks (D-3)");
        item.QtyOnHandSpecified.Should().BeFalse();
        item.TypeSpecified.Should().BeTrue("Type is always sent");
        item.ActiveSpecified.Should().BeTrue();
    }

    [Fact]
    public void A_zero_price_is_still_a_value()
    {
        var item = QboItemMapper.ToSdk(new RemoteItem { Name = "Free", UnitPrice = 0m, PurchaseCost = 0m });

        item.UnitPriceSpecified.Should().BeTrue();
        item.UnitPrice.Should().Be(0m);
        item.PurchaseCostSpecified.Should().BeTrue();
    }

    [Fact]
    public void Inactive_item()
    {
        var item = QboItemMapper.ToSdk(new RemoteItem { Name = "Old", Active = false });

        item.Active.Should().BeFalse();
        item.ActiveSpecified.Should().BeTrue();
    }

    [Fact]
    public void Item_record_and_list_entry()
    {
        var item = new Item { Id = "11", SyncToken = "3", Name = "Widget", Sku = "WID", Active = false, ActiveSpecified = true };

        QboItemMapper.ToRecord(item).Should().Be(new RemoteRecord("11", "3", "Widget", null, null, null, false));
        QboItemMapper.ToListEntry(item).Should().Be(new RemoteListEntry("11", "Widget", null, null, null, null, "WID", null, false));
    }
}
