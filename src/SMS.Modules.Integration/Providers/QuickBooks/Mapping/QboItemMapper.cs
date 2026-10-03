using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// RemoteItem ↔ SDK <see cref="Item"/>. Only NonInventory and Service are produced — SCM stays the system
/// of record for stock and cost (plan D-3), so an Inventory item is never created.
/// </summary>
internal static class QboItemMapper
{
    public static Item ToSdk(RemoteItem source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var item = new Item
        {
            Name              = source.Name,
            Sku               = Value(source.Sku),
            Type              = ToSdkType(source.Type),
            TypeSpecified     = true,
            Description       = Value(source.Description),
            PurchaseDesc      = Value(source.PurchaseDesc),
            IncomeAccountRef  = Ref(source.IncomeAccountId),
            ExpenseAccountRef = Ref(source.ExpenseAccountId),
            Active            = source.Active,
            ActiveSpecified   = true
        };

        if (source.UnitPrice is { } unitPrice)
        {
            item.UnitPrice          = unitPrice;
            item.UnitPriceSpecified = true;
        }
        if (source.PurchaseCost is { } purchaseCost)
        {
            item.PurchaseCost          = purchaseCost;
            item.PurchaseCostSpecified = true;
        }
        return item;
    }

    public static ItemTypeEnum ToSdkType(RemoteItemType type) => type switch
    {
        RemoteItemType.NonInventory => ItemTypeEnum.NonInventory,
        RemoteItemType.Service      => ItemTypeEnum.Service,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only NonInventory and Service items are supported (plan D-3).")
    };

    public static RemoteRecord ToRecord(Item item) =>
        new(RequireId(item, "item"), item.SyncToken ?? string.Empty, item.Name, null,
            Active: ReadActive(item.Active, item.ActiveSpecified));

    public static RemoteListEntry ToListEntry(Item item) =>
        new(RequireId(item, "item"),
            item.Name ?? string.Empty,
            CompanyName: null,
            Email: null,
            TaxId: null,
            AccountNumber: null,
            Value(item.Sku),
            CurrencyCode: null,
            ReadActive(item.Active, item.ActiveSpecified));
}
