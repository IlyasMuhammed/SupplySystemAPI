using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>Dispatches by kind to the per-entity mappers.</summary>
internal static class QboEntityMapper
{
    public static IntuitEntity ToSdk(RemoteEntity entity) => entity switch
    {
        RemoteCustomer customer     => QboCustomerMapper.ToSdk(customer),
        RemoteVendor vendor         => QboVendorMapper.ToSdk(vendor),
        RemoteItem item             => QboItemMapper.ToSdk(item),
        RemoteItemIncomeAccountFix fix => QboItemMapper.ToSdk(fix),
        RemoteInvoice invoice       => QboInvoiceMapper.ToSdk(invoice),
        RemoteBill bill             => QboBillMapper.ToSdk(bill),
        RemotePayment payment       => QboPaymentMapper.ToSdk(payment),
        RemoteBillPayment payment   => QboPaymentMapper.ToSdk(payment),
        RemoteSalesReceipt receipt  => QboSalesDocumentMapper.ToSdk(receipt),
        RemoteCreditMemo memo       => QboSalesDocumentMapper.ToSdk(memo),
        RemoteRefundReceipt refund  => QboSalesDocumentMapper.ToSdk(refund),
        RemoteVendorCredit credit   => QboVendorCreditMapper.ToSdk(credit),
        null => throw new ArgumentNullException(nameof(entity)),
        _ => throw new NotSupportedException($"No QuickBooks mapping for {entity.GetType().Name}.")
    };

    /// <summary>An empty SDK object of the kind, for FindById.</summary>
    public static IntuitEntity NewOfKind(SyncKind kind) => kind switch
    {
        SyncKind.Customer      => new Customer(),
        SyncKind.Vendor        => new Vendor(),
        SyncKind.Item          => new Item(),
        SyncKind.SalesInvoice  => new Invoice(),
        SyncKind.Bill          => new Bill(),
        SyncKind.Payment       => new Payment(),
        SyncKind.BillPayment   => new BillPayment(),
        SyncKind.SalesReceipt  => new SalesReceipt(),
        SyncKind.CreditMemo    => new CreditMemo(),
        SyncKind.RefundReceipt => new RefundReceipt(),
        SyncKind.VendorCredit  => new VendorCredit(),
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
        (SyncKind.Customer, Customer customer)          => QboCustomerMapper.ToRecord(customer),
        (SyncKind.Vendor, Vendor vendor)                => QboVendorMapper.ToRecord(vendor),
        (SyncKind.Item, Item item)                      => QboItemMapper.ToRecord(item),
        (SyncKind.SalesInvoice, Invoice invoice)        => QboInvoiceMapper.ToRecord(invoice),
        (SyncKind.Bill, Bill bill)                      => QboBillMapper.ToRecord(bill),
        (SyncKind.Payment, Payment payment)             => QboPaymentMapper.ToRecord(payment),
        (SyncKind.BillPayment, BillPayment payment)     => QboPaymentMapper.ToRecord(payment),
        (SyncKind.SalesReceipt, SalesReceipt receipt)   => QboSalesDocumentMapper.ToRecord(receipt, "sales receipt"),
        (SyncKind.CreditMemo, CreditMemo memo)          => QboSalesDocumentMapper.ToRecord(memo, "credit memo"),
        (SyncKind.RefundReceipt, RefundReceipt refund)  => QboSalesDocumentMapper.ToRecord(refund, "refund receipt"),
        (SyncKind.VendorCredit, VendorCredit credit)    => QboVendorCreditMapper.ToRecord(credit),
        _ => throw new InvalidOperationException(
            $"QuickBooks returned {(entity is null ? "nothing" : entity.GetType().Name)} where a {kind} was expected.")
    };
}
