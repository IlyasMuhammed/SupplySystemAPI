using System.Globalization;
using System.Reflection;
using System.Xml.Serialization;
using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// Shared rules for building SDK objects.
/// <list type="bullet">
/// <item>An optional value that is null or blank is left unset — the SDK serializer then omits it, so a
/// sparse update never clears a field the accountant filled in QuickBooks. (The flip side: a field
/// cannot be cleared from our side either.)</item>
/// <item>Every value-typed field is paired with its <c>…Specified</c> flag; the SDK drops the value
/// silently when the flag is false.</item>
/// <item>Dates are sent as calendar dates. The SDK serializer converts <see cref="DateTimeKind.Local"/>
/// values to UTC before formatting, which would move the date across midnight, so every date is
/// re-kinded as Unspecified first.</item>
/// </list>
/// </summary>
internal static class QboMap
{
    public static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    public static string? Value(string? value) => Has(value) ? value : null;

    public static ReferenceType? Ref(string? id) => Has(id) ? new ReferenceType { Value = id } : null;

    /// <summary>The calendar date, re-kinded so the serializer writes it unshifted as <c>yyyy-MM-dd</c>.</summary>
    public static DateTime Date(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    public static EmailAddress? Email(string? address) => Has(address) ? new EmailAddress { Address = address } : null;

    public static TelephoneNumber? Phone(string? number) => Has(number) ? new TelephoneNumber { FreeFormNumber = number } : null;

    public static WebSiteAddress? Web(string? uri) => Has(uri) ? new WebSiteAddress { URI = uri } : null;

    /// <summary>Null when no address line has a value; otherwise only the lines that do.</summary>
    public static PhysicalAddress? Address(RemoteAddress? address)
    {
        if (address is null) return null;
        var result = new PhysicalAddress
        {
            Line1                  = Value(address.Line1),
            Line2                  = Value(address.Line2),
            City                   = Value(address.City),
            CountrySubDivisionCode = Value(address.Region),
            PostalCode             = Value(address.PostalCode),
            Country                = Value(address.Country)
        };
        return result.Line1 is null && result.Line2 is null && result.City is null
            && result.CountrySubDivisionCode is null && result.PostalCode is null && result.Country is null
            ? null
            : result;
    }

    /// <summary>Turns a mapped entity into a sparse update of an existing record.</summary>
    public static T AsSparseUpdate<T>(T entity, string remoteId, string syncToken) where T : IntuitEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncToken);
        entity.Id              = remoteId;
        entity.SyncToken       = syncToken;
        entity.sparse          = true;
        entity.sparseSpecified = true;
        return entity;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A value-typed field QuickBooks sent, or null when it did not.</summary>
    public static decimal? Read(decimal value, bool specified) => specified ? value : null;

    /// <summary>Name-list entities are active unless QuickBooks said otherwise.</summary>
    public static bool ReadActive(bool active, bool specified) => !specified || active;

    /// <summary>Transactions have no Active flag; a voided or deleted one counts as inactive.</summary>
    public static bool ReadTxnActive(IntuitEntity entity) =>
        !(entity.statusSpecified && entity.status is EntityStatusEnum.Voided or EntityStatusEnum.Deleted);

    public static string RequireId(IntuitEntity entity, string what) =>
        Has(entity.Id) ? entity.Id : throw new InvalidOperationException($"QuickBooks returned a {what} without an Id.");

    /// <summary>The name QuickBooks uses on the wire (<c>Cost of Goods Sold</c>, not <c>CostofGoodsSold</c>).</summary>
    public static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var field = typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static);
        var xmlEnum = field?.GetCustomAttribute<XmlEnumAttribute>();
        return string.IsNullOrEmpty(xmlEnum?.Name) ? name : xmlEnum.Name;
    }

    public static int? ToInt(object? value)
    {
        switch (value)
        {
            case null:    return null;
            case int i:   return i;
            case long l:  return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
            case decimal d: return d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
            default:
                return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
    }
}
