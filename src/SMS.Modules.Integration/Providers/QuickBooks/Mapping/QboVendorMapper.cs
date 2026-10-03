using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// RemoteVendor ↔ SDK <see cref="Vendor"/>. Tax id goes to <c>TaxIdentifier</c> (vendors have no
/// PrimaryTaxIdentifier), terms to <c>TermRef</c> (not SalesTermRef), account number to <c>AcctNum</c>.
/// </summary>
internal static class QboVendorMapper
{
    public static Vendor ToSdk(RemoteVendor source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Vendor
        {
            DisplayName      = source.DisplayName,
            CompanyName      = Value(source.CompanyName),
            PrimaryEmailAddr = Email(source.Email),
            PrimaryPhone     = Phone(source.Phone),
            Fax              = Phone(source.Fax),
            WebAddr          = Web(source.Website),
            BillAddr         = Address(source.BillAddress),
            CurrencyRef      = Ref(source.CurrencyCode),
            TermRef          = Ref(source.TermId),
            Notes            = Value(source.Notes),
            TaxIdentifier    = Value(source.TaxId),
            AcctNum          = Value(source.AccountNumber),
            Active           = source.Active,
            ActiveSpecified  = true
        };
    }

    public static RemoteRecord ToRecord(Vendor vendor) =>
        new(RequireId(vendor, "vendor"), vendor.SyncToken ?? string.Empty, vendor.DisplayName, null,
            Active: ReadActive(vendor.Active, vendor.ActiveSpecified));

    public static RemoteListEntry ToListEntry(Vendor vendor) =>
        new(RequireId(vendor, "vendor"),
            vendor.DisplayName ?? string.Empty,
            Value(vendor.CompanyName),
            Value(vendor.PrimaryEmailAddr?.Address),
            Value(vendor.TaxIdentifier),
            Value(vendor.AcctNum),
            Sku: null,
            Value(vendor.CurrencyRef?.Value),
            ReadActive(vendor.Active, vendor.ActiveSpecified));
}
