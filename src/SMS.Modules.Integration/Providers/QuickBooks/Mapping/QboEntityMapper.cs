using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>Dispatches by kind to the per-entity mappers.</summary>
internal static class QboEntityMapper
{
    public static IntuitEntity ToSdk(RemoteEntity entity) => entity switch
    {
        RemoteCustomer customer => QboCustomerMapper.ToSdk(customer),
        RemoteVendor vendor     => QboVendorMapper.ToSdk(vendor),
        RemoteItem item         => QboItemMapper.ToSdk(item),
        RemoteInvoice invoice   => QboInvoiceMapper.ToSdk(invoice),
        RemoteBill bill         => QboBillMapper.ToSdk(bill),
        null => throw new ArgumentNullException(nameof(entity)),
        _ => throw new NotSupportedException($"No QuickBooks mapping for {entity.GetType().Name}.")
    };

    /// <summary>An empty SDK object of the kind, for FindById.</summary>
    public static IntuitEntity NewOfKind(SyncKind kind) => kind switch
    {
        SyncKind.Customer     => new Customer(),
        SyncKind.Vendor       => new Vendor(),
        SyncKind.Item         => new Item(),
        SyncKind.SalesInvoice => new Invoice(),
        SyncKind.Bill         => new Bill(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sync kind.")
    };

    /// <summary>The SDK entity name (also its REST resource and query name).</summary>
    public static string EntityName(SyncKind kind) => NewOfKind(kind).GetType().Name;

    /// <summary>The same object as the SDK's <see cref="IEntity"/> (implemented by every concrete entity, not by the base class).</summary>
    public static IEntity AsEntity(IntuitEntity entity) =>
        entity as IEntity ?? throw new InvalidOperationException($"{entity.GetType().Name} is not an SDK entity.");

    /// <summary>Throws when QuickBooks returned something other than the kind asked for.</summary>
    public static RemoteRecord ToRecord(SyncKind kind, object? entity) => (kind, entity) switch
    {
        (SyncKind.Customer, Customer customer)    => QboCustomerMapper.ToRecord(customer),
        (SyncKind.Vendor, Vendor vendor)          => QboVendorMapper.ToRecord(vendor),
        (SyncKind.Item, Item item)                => QboItemMapper.ToRecord(item),
        (SyncKind.SalesInvoice, Invoice invoice)  => QboInvoiceMapper.ToRecord(invoice),
        (SyncKind.Bill, Bill bill)                => QboBillMapper.ToRecord(bill),
        _ => throw new InvalidOperationException(
            $"QuickBooks returned {(entity is null ? "nothing" : entity.GetType().Name)} where a {kind} was expected.")
    };
}
