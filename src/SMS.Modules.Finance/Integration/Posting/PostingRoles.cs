namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-5) — the A42 accounting default keys a posting line names instead of an account code. Repeated here
/// because Finance does not reference SMS.Modules.Accounting (its <c>DefaultKeys</c> in GlCodes.cs is the source;
/// the values are the contract, API-CONTRACT §3).
/// </summary>
internal static class PostingRoles
{
    public const string Receivable       = "DEFAULT_RECEIVABLE";
    public const string Payable          = "DEFAULT_PAYABLE";
    public const string Cash             = "DEFAULT_CASH";
    public const string Bank             = "DEFAULT_BANK";
    public const string Revenue          = "DEFAULT_REVENUE";
    public const string TaxReceivable    = "DEFAULT_TAX_RECEIVABLE";
    public const string TaxPayable       = "DEFAULT_TAX_PAYABLE";
    public const string Inventory        = "DEFAULT_INVENTORY";
    public const string Purchase         = "DEFAULT_PURCHASE";
    public const string DiscountGiven    = "DEFAULT_DISCOUNT_GIVEN";
    public const string ExchangeGainLoss = "DEFAULT_EXCHANGE_GAIN_LOSS";
    public const string Rounding         = "DEFAULT_ROUNDING";

    /// <summary>Where money of a payment method lands (or leaves from): cash in hand for CASH, the bank for everything else.</summary>
    public static string MoneyFor(string? method) =>
        string.Equals(method?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase) ? Cash : Bank;
}

/// <summary>Keys of <see cref="SMS.Shared.Finance.Posting.FinancialDocumentMetadata.Properties"/> this module sets.</summary>
internal static class PostingProperties
{
    /// <summary>
    /// D-23 / D-71 — the external target sends the document and then flags it for the accountant with this text
    /// (a POS return refunded partly in cash and partly on account / in store credit goes as one credit memo).
    /// </summary>
    public const string FlagForAccountant = "FlagForAccountant";

    /// <summary>D-71 — on a credit memo applied to its original invoice: how much of it is applied (invariant "0.00").</summary>
    public const string AppliedAmount = "AppliedAmount";
}
