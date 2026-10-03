using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Suppliers.Integration;

/// <summary>
/// A business partner as the QuickBooks gateway wants to hear about it: one row, sent as a customer, a
/// vendor or both. Neutral data only — every QuickBooks rule (name length, uniqueness, the vendor-name
/// suffix) is the gateway's, so nothing here shortens or renames anything.
/// </summary>
internal static class PartnerPayloadFactory
{
    /// <summary>
    /// Partner statuses that mean "do not use for new documents". SUSPENDED is deliberately not one:
    /// a suspension is temporary and under review, and bills already approved against the partner still
    /// have to reach QuickBooks, which refuses a transaction against an inactive name.
    /// </summary>
    internal static readonly IReadOnlySet<string> InactiveStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BLACKLISTED", "REJECTED", "INACTIVE" };

    /// <summary>Is this partner someone we sell to.</summary>
    internal static bool IsCustomerRole(BusinessPartner p) => p.IsCustomer;

    /// <summary>
    /// Is this partner someone we pay. Carriers and service providers are payees too: a carrier's freight
    /// bill is posted to Finance against its linked partner exactly like a supplier's invoice, so in
    /// QuickBooks it is a vendor.
    /// </summary>
    internal static bool IsVendorRole(BusinessPartner p) => p.IsVendor || p.IsCarrier || p.IsServiceProvider;

    /// <summary>Active in QuickBooks: switched on, not deleted, and not in a status that bars new documents.</summary>
    internal static bool IsActiveInQuickBooks(BusinessPartner p) =>
        p.IsActive && !p.IsDelete && !InactiveStatuses.Contains(p.Status ?? string.Empty);

    /// <param name="currencyCode">The ISO code of <see cref="BusinessPartner.PreferredCurrency"/>, or null when none is set or it has no code.</param>
    public static CustomerPayload BuildCustomer(BusinessPartner partner, string? currencyCode) =>
        Fill(new CustomerPayload(), partner, currencyCode);

    /// <param name="currencyCode">The ISO code of <see cref="BusinessPartner.PreferredCurrency"/>, or null when none is set or it has no code.</param>
    public static VendorPayload BuildVendor(BusinessPartner partner, string? currencyCode)
    {
        var payload = Fill(new VendorPayload(), partner, currencyCode);
        payload.AccountNumber = Clean(partner.SupplierCode);
        return payload;
    }

    private static T Fill<T>(T payload, BusinessPartner p, string? currencyCode) where T : PartyPayload
    {
        payload.ExternalId            = p.UUID.ToString();
        payload.DisplayName           = p.SupplierName?.Trim() ?? string.Empty;
        payload.CompanyName           = Clean(p.SupplierName);
        payload.Code                  = Clean(p.SupplierCode);
        // The partner's own contact details first; the inline primary contact only fills a gap.
        payload.Email                 = Clean(p.Email) ?? Clean(p.PrimaryContactEmail);
        payload.Phone                 = Clean(p.Phone) ?? Clean(p.PrimaryContactPhone);
        payload.Fax                   = Clean(p.Fax);
        payload.Website               = Clean(p.Website);
        payload.TaxId                 = Clean(p.TaxId);
        payload.BillingAddress        = Address(p);
        payload.CurrencyCode          = Clean(currencyCode)?.ToUpperInvariant();
        payload.PaymentTermExternalId = p.PreferredPaymentTerms?.ToString();
        payload.Notes                 = Clean(p.Notes);
        payload.IsActive              = IsActiveInQuickBooks(p);
        return payload;
    }

    /// <summary>Null when the partner has no address at all, rather than an address of empty lines.</summary>
    private static AddressPayload? Address(BusinessPartner p)
    {
        var address = new AddressPayload
        {
            Line1      = Clean(p.AddressLine1),
            Line2      = Clean(p.AddressLine2),
            City       = Clean(p.City),
            Region     = Clean(p.ProvinceState),
            PostalCode = Clean(p.PostalCode),
            Country    = Clean(p.Country)
        };

        return address is { Line1: null, Line2: null, City: null, Region: null, PostalCode: null, Country: null }
            ? null
            : address;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
