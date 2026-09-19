namespace SMS.Modules.Suppliers.Domain;

// P1-03 (Addendum 29 §1.2). Mirrors SMS.Modules.Logistics.Domain's [Code]-attribute pattern
// (LogisticsEnums.cs / LogisticsCode.cs) rather than inventing a new one — that pattern is
// internal to its own module and not visible here, so it is reproduced locally rather than
// shared, matching how each module already owns its closed vocabularies independently.
//
// Every member carries an explicit [Code] rather than deriving the persisted string from the
// member name, for the same reason as Logistics: renaming a member is a refactor a developer
// expects to be safe, and if the persisted value were derived from the name, that refactor would
// silently orphan every existing row.
//
// BusinessPartner.PartnerType persists as a plain string (matching the rest of the system, and
// already the P1-02 column type) — this enum exists so P1-04's auto-compute-from-flags logic, and
// any future service code, can reason in types instead of magic strings.

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
internal sealed class CodeAttribute : Attribute
{
    internal string Value { get; }
    internal CodeAttribute(string value) => Value = value;
}

internal enum PartnerType
{
    [Code("VENDOR")]           Vendor,
    [Code("CUSTOMER")]         Customer,
    [Code("CARRIER")]          Carrier,
    [Code("SERVICE_PROVIDER")] ServiceProvider,
    [Code("BOTH")]             Both,
    [Code("VENDOR_CARRIER")]   VendorCarrier,
    [Code("VENDOR_SERVICE")]   VendorService,
    [Code("FULL_PARTNER")]     FullPartner
}
