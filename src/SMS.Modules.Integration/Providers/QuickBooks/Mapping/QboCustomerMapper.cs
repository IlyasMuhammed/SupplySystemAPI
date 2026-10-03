using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>RemoteCustomer ↔ SDK <see cref="Customer"/>. Tax id goes to <c>PrimaryTaxIdentifier</c>.</summary>
internal static class QboCustomerMapper
{
    public static Customer ToSdk(RemoteCustomer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Customer
        {
            DisplayName          = source.DisplayName,
            CompanyName          = Value(source.CompanyName),
            PrimaryEmailAddr     = Email(source.Email),
            PrimaryPhone         = Phone(source.Phone),
            Fax                  = Phone(source.Fax),
            WebAddr              = Web(source.Website),
            BillAddr             = Address(source.BillAddress),
            CurrencyRef          = Ref(source.CurrencyCode),
            SalesTermRef         = Ref(source.TermId),
            Notes                = Value(source.Notes),
            PrimaryTaxIdentifier = Value(source.TaxId),
            Active               = source.Active,
            ActiveSpecified      = true
        };
    }

    public static RemoteRecord ToRecord(Customer customer) =>
        new(RequireId(customer, "customer"), customer.SyncToken ?? string.Empty, customer.DisplayName, null,
            Active: ReadActive(customer.Active, customer.ActiveSpecified));

    /// <remarks>QuickBooks masks tax ids in responses (e.g. <c>XXXXXX1234</c>), so TaxId is rarely a usable match key.</remarks>
    public static RemoteListEntry ToListEntry(Customer customer) =>
        new(RequireId(customer, "customer"),
            customer.DisplayName ?? string.Empty,
            Value(customer.CompanyName),
            Value(customer.PrimaryEmailAddr?.Address),
            Value(customer.PrimaryTaxIdentifier),
            AccountNumber: null,
            Sku: null,
            Value(customer.CurrencyRef?.Value),
            ReadActive(customer.Active, customer.ActiveSpecified));
}
