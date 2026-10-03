using System.Text.Json.Serialization;

namespace SMS.Shared.Integration.QuickBooks;

// The data a caller hands the QuickBooks gateway. Deliberately neutral rather than QuickBooks-shaped:
// callers send their own data as it is, and the gateway applies every QuickBooks rule (name length and
// uniqueness, the ':' rule, vendor-name suffixes, discount lines, tax-code mapping). Otherwise every
// calling project would have to learn QuickBooks' quirks separately and get them subtly different.
//
// ExternalId is the caller's own id for the record (SCM sends the row's UUID). Together with the
// caller's source system it is the upsert key, so sending the same payload twice is harmless.

public sealed class AddressPayload
{
    public string? Line1      { get; set; }
    public string? Line2      { get; set; }
    public string? City       { get; set; }
    public string? Region     { get; set; }
    public string? PostalCode { get; set; }
    public string? Country    { get; set; }
}

/// <summary>What customers and vendors have in common.</summary>
public abstract class PartyPayload
{
    public string          ExternalId            { get; set; } = string.Empty;
    public string          DisplayName           { get; set; } = string.Empty;
    public string?         CompanyName           { get; set; }
    /// <summary>The caller's short code (SCM: SupplierCode). Used to disambiguate clashing names and to match.</summary>
    public string?         Code                  { get; set; }
    public string?         Email                 { get; set; }
    public string?         Phone                 { get; set; }
    public string?         Fax                   { get; set; }
    public string?         Website               { get; set; }
    public string?         TaxId                 { get; set; }
    public AddressPayload? BillingAddress        { get; set; }
    /// <summary>ISO code. Fixed in QuickBooks once the record has transactions.</summary>
    public string?         CurrencyCode          { get; set; }
    /// <summary>The caller's payment-term id; the gateway maps it to a QuickBooks term.</summary>
    public string?         PaymentTermExternalId { get; set; }
    public string?         Notes                 { get; set; }
    public bool            IsActive              { get; set; } = true;
}

public sealed class CustomerPayload : PartyPayload { }

public sealed class VendorPayload : PartyPayload
{
    /// <summary>Shown to the accountant as the vendor's account number (SCM: SupplierCode).</summary>
    public string? AccountNumber { get; set; }
}

public sealed class ItemPayload
{
    public string         ExternalId   { get; set; } = string.Empty;
    public string         Name         { get; set; } = string.Empty;
    /// <summary>Set only when the product has more than one variant; the gateway then names the item "Name - VariantName".</summary>
    public string?        VariantName  { get; set; }
    public string         Sku          { get; set; } = string.Empty;
    public string?        Description  { get; set; }
    public ItemPayloadKind Kind        { get; set; } = ItemPayloadKind.Goods;
    public decimal?       SalesPrice   { get; set; }
    public decimal?       PurchaseCost { get; set; }
    public bool           IsSold       { get; set; } = true;
    public bool           IsPurchased  { get; set; } = true;
    public bool           IsActive     { get; set; } = true;
}

public sealed class SalesInvoicePayload
{
    public string                         ExternalId           { get; set; } = string.Empty;
    public string                         DocNumber            { get; set; } = string.Empty;
    public string                         CustomerExternalId   { get; set; } = string.Empty;
    public DateTime                       TxnDate              { get; set; }
    public DateTime?                      DueDate              { get; set; }
    public string                         CurrencyCode         { get; set; } = string.Empty;
    public SalesInvoicePayloadStatus      Status               { get; set; } = SalesInvoicePayloadStatus.Issued;
    public List<SalesInvoiceLinePayload>  Lines                { get; set; } = new();
    /// <summary>A discount on the whole invoice, on top of any per-line discount.</summary>
    public decimal                        HeaderDiscountAmount { get; set; }
    /// <summary>The caller's own tax total; QuickBooks' calculation is checked against it.</summary>
    public decimal                        ExpectedTaxAmount    { get; set; }
    /// <summary>The caller's own grand total; the gateway refuses a payload whose lines do not add up to it.</summary>
    public decimal                        ExpectedTotal        { get; set; }
    public string?                        CustomerMemo         { get; set; }
    public string?                        PrivateNote          { get; set; }
    /// <summary>
    /// The invoice's own exchange rate, when the caller fixed one (SCM: the snapshot taken when it was issued,
    /// SAP alignment S-5): 1 <see cref="CurrencyCode"/> = ExchangeRate <see cref="ExchangeRateCurrencyCode"/>.
    /// When it converts into QuickBooks' home currency it is the rate QuickBooks is sent, so SMS's base amounts
    /// and QuickBooks' agree even if SMS's rate table changes later; otherwise the gateway uses SMS's exchange
    /// rates for <see cref="TxnDate"/>. Omitted from the stored JSON when null (fingerprints of payloads without
    /// one are unchanged); only sent for a foreign-currency document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal?                       ExchangeRate             { get; set; }
    /// <summary>The currency <see cref="ExchangeRate"/> converts into (SCM: its base currency). Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?                        ExchangeRateCurrencyCode { get; set; }
}

public sealed class SalesInvoiceLinePayload
{
    public int     LineNo          { get; set; }
    public string  ItemExternalId  { get; set; } = string.Empty;
    public string? Description     { get; set; }
    public decimal Quantity        { get; set; }
    /// <summary>Gross, before the line discount.</summary>
    public decimal UnitPrice       { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent      { get; set; }
    /// <summary>
    /// The caller's tax code (SCM: Finance TaxCode.Code), when the line has one — then mapped to a QuickBooks
    /// tax code by that code only (a line without a code is mapped by its rate). Omitted from the stored JSON
    /// when null, so payloads from before tax codes keep their fingerprints (and are not re-sent).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaxCode         { get; set; }
}

public sealed class BillPayload
{
    public string                ExternalId        { get; set; } = string.Empty;
    /// <summary>The vendor's own invoice number where known.</summary>
    public string                DocNumber         { get; set; } = string.Empty;
    public string                VendorExternalId  { get; set; } = string.Empty;
    public DateTime              TxnDate           { get; set; }
    public DateTime?             DueDate           { get; set; }
    public string                CurrencyCode      { get; set; } = string.Empty;
    public List<BillLinePayload> Lines             { get; set; } = new();
    public decimal               ExpectedTaxAmount { get; set; }
    public decimal               ExpectedTotal     { get; set; }
    public string?               PrivateNote       { get; set; }
    /// <summary>
    /// The bill's own exchange rate, when the caller fixed one (SCM: the snapshot taken at approval, S-5):
    /// 1 <see cref="CurrencyCode"/> = ExchangeRate <see cref="ExchangeRateCurrencyCode"/>. Used as on
    /// <see cref="SalesInvoicePayload.ExchangeRate"/>. Omitted from the stored JSON when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal?              ExchangeRate             { get; set; }
    /// <summary>The currency <see cref="ExchangeRate"/> converts into. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?               ExchangeRateCurrencyCode { get; set; }
}

public sealed class BillLinePayload
{
    public int              LineNo         { get; set; }
    /// <summary>Set for goods whose item is known; the line is then sent against that item.</summary>
    public string?          ItemExternalId { get; set; }
    public BillLineCategory Category       { get; set; } = BillLineCategory.Goods;
    public string?          Description    { get; set; }
    public decimal?         Quantity       { get; set; }
    public decimal?         UnitPrice      { get; set; }
    public decimal          Amount         { get; set; }
    /// <summary>Null when the caller's bill carries no per-line rate (plan D-10).</summary>
    public decimal?         TaxPercent     { get; set; }
    /// <summary>The caller's purchase tax code, when it has one. Omitted from the stored JSON when null (fingerprints unchanged).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?          TaxCode        { get; set; }
}
