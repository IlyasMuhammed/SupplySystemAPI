namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "Opening balances" (BE2).

public sealed class OpeningBalancesModel
{
    /// <summary>The A40 opening balance date (Finance), or the copy taken at posting once posted.</summary>
    public DateOnly? ConversionDate { get; set; }
    public bool      IsPosted       { get; set; }
    public DateTime? PostedAt       { get; set; }
    /// <summary>Posted with no balances (D-19 skip).</summary>
    public bool      IsSkipped      { get; set; }
    public List<OpeningGroupModel> Groups { get; set; } = [];
    public OpeningSummaryModel Summary { get; set; } = new();
    /// <summary>D-12 — null when Finance's sub-ledger totals are not available.</summary>
    public SubledgerReferenceModel? Subledger { get; set; }
}

public sealed class OpeningGroupModel
{
    public string RootType { get; set; } = string.Empty;
    public List<OpeningRowModel> Rows { get; set; } = [];
}

public sealed class OpeningRowModel
{
    public Guid    AccountUuid  { get; set; }
    public string  AccountCode  { get; set; } = string.Empty;
    public string  AccountName  { get; set; } = string.Empty;
    public string? AccountType  { get; set; }
    public decimal DebitAmount  { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Notes        { get; set; }
    /// <summary>3900 Opening Balance Equity — read-only, receives the difference.</summary>
    public bool    IsPlug       { get; set; }
    /// <summary>BR-OB-007 — revenue/expense balance warning.</summary>
    public string? Warning      { get; set; }
}

public sealed class OpeningSummaryModel
{
    public decimal   TotalDebits    { get; set; }
    public decimal   TotalCredits   { get; set; }
    /// <summary>Debits − credits (before the plug).</summary>
    public decimal   Difference     { get; set; }
    public bool      IsBalanced     { get; set; }
    public DateOnly? ConversionDate { get; set; }
    /// <summary>Where the plug goes on 3900: "Credit" when debits exceed credits, "Debit" otherwise; null when balanced.</summary>
    public string?   PlugSide       { get; set; }
    public int       EntryCount     { get; set; }
}

public sealed class SubledgerReferenceModel
{
    public decimal  CustomerOpeningTotal     { get; set; }
    public decimal  SupplierOpeningTotal     { get; set; }
    public decimal  OpeningStockValue        { get; set; }
    public decimal? ReceivableAccountBalance { get; set; }
    public decimal? PayableAccountBalance    { get; set; }
    public decimal? InventoryAccountBalance  { get; set; }
    public string?  CurrencyCode             { get; set; }
    public string?  ReceivableAccountCode    { get; set; }
    public string?  PayableAccountCode       { get; set; }
    public string?  InventoryAccountCode     { get; set; }
}

public sealed class SaveOpeningBalancesRequest
{
    public List<OpeningEntryInput> Entries { get; set; } = [];
}

public sealed class OpeningEntryInput
{
    public Guid    AccountUuid  { get; set; }
    public decimal DebitAmount  { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Notes        { get; set; }
}

public sealed class PostOpeningBalancesRequest
{
    public bool AcknowledgeDifference { get; set; }
    public bool Skip                  { get; set; }
}

public sealed class PostOpeningBalancesResult
{
    public string? JournalEntryNumber { get; set; }
    public OpeningSummaryModel Summary { get; set; } = new();
}

public sealed class OpeningBalanceError
{
    public Guid?   AccountUuid { get; set; }
    public string? AccountCode { get; set; }
    public string  Message     { get; set; } = string.Empty;
}
