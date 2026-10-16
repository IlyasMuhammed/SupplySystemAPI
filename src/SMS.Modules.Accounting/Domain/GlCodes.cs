namespace SMS.Modules.Accounting.Domain;

// A42 vocabularies (docs/general-ledger/API-CONTRACT.md "Codes"). Stored as strings; every list is also a CHECK constraint
// (Data/Maps/AccountingMaps.cs). Comparisons are ordinal: the API accepts and returns these exact spellings.

/// <summary>Level 1 of the chart: the five root types, with their fixed level-1 codes (D-8).</summary>
public static class RootTypes
{
    public const string Asset = "Asset", Liability = "Liability", Equity = "Equity", Revenue = "Revenue", Expense = "Expense";

    public static readonly IReadOnlyList<string> All = [Asset, Liability, Equity, Revenue, Expense];

    /// <summary>D-8 — level-1 code of each root type; every descendant's code starts with this digit.</summary>
    public static readonly IReadOnlyDictionary<string, string> RootCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Asset] = "1", [Liability] = "2", [Equity] = "3", [Revenue] = "4", [Expense] = "5",
    };

    /// <summary>Debit-normal roots (balance = debit − credit); the others are credit-normal.</summary>
    public static bool IsDebitNormal(string rootType) => rootType is Asset or Expense;

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>FSD Appendix A — the level-4 sub-classification and the root types each is valid for (D-23).</summary>
public static class AccountTypes
{
    public const string Cash = "Cash", Bank = "Bank", Receivable = "Receivable", OtherCurrentAsset = "OtherCurrentAsset",
        FixedAsset = "FixedAsset", AccumulatedDepreciation = "AccumulatedDepreciation", Payable = "Payable",
        OtherCurrentLiability = "OtherCurrentLiability", LongTermLiability = "LongTermLiability", Equity = "Equity",
        Revenue = "Revenue", OtherIncome = "OtherIncome", CostOfGoodsSold = "CostOfGoodsSold", Expense = "Expense",
        OtherExpense = "OtherExpense", Tax = "Tax", Depreciation = "Depreciation";

    public static readonly IReadOnlyDictionary<string, string[]> ValidRootTypes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Cash]                    = [RootTypes.Asset],
        [Bank]                    = [RootTypes.Asset],
        [Receivable]              = [RootTypes.Asset],
        [OtherCurrentAsset]       = [RootTypes.Asset],
        [FixedAsset]              = [RootTypes.Asset],
        [AccumulatedDepreciation] = [RootTypes.Asset],
        [Payable]                 = [RootTypes.Liability],
        [OtherCurrentLiability]   = [RootTypes.Liability],
        [LongTermLiability]       = [RootTypes.Liability],
        [Equity]                  = [RootTypes.Equity],
        [Revenue]                 = [RootTypes.Revenue],
        [OtherIncome]             = [RootTypes.Revenue],
        [CostOfGoodsSold]         = [RootTypes.Expense],
        [Expense]                 = [RootTypes.Expense],
        [OtherExpense]            = [RootTypes.Expense],
        [Tax]                     = [RootTypes.Asset, RootTypes.Liability],
        [Depreciation]            = [RootTypes.Expense],
    };

    public static readonly IReadOnlyList<string> All = ValidRootTypes.Keys.ToList();

    public static bool IsValid(string? value) => value is not null && ValidRootTypes.ContainsKey(value);

    /// <summary>D-23 — the type exists and is allowed under that root type.</summary>
    public static bool IsValidFor(string accountType, string rootType) =>
        ValidRootTypes.TryGetValue(accountType, out var roots) && roots.Contains(rootType);
}

/// <summary>D-16 — the setup state machine, in order. The stored status is the furthest consecutive satisfied step.</summary>
public static class SetupStatuses
{
    public const string NotStarted = "NotStarted", AccountsCreated = "AccountsCreated", FiscalYearSet = "FiscalYearSet",
        DefaultsSet = "DefaultsSet", BalancesEntered = "BalancesEntered", Complete = "Complete";

    public static readonly IReadOnlyList<string> All = [NotStarted, AccountsCreated, FiscalYearSet, DefaultsSet, BalancesEntered, Complete];

    /// <summary>Position in the state machine (NotStarted = 0 … Complete = 5); -1 for an unknown value.</summary>
    public static int Rank(string? status) => status is null ? -1 : All.ToList().IndexOf(status);

    /// <summary>BR-TPL-004 / D-16 — the chart may be replaced (apply/upload) only in these states.</summary>
    public static bool AllowsChartReplacement(string status) => status is NotStarted or AccountsCreated;
}

/// <summary>D-18 — Open accepts postings; Locked is a reversible soft close; Closed is final.</summary>
public static class PeriodStatuses
{
    public const string Open = "Open", Locked = "Locked", Closed = "Closed";
    public static readonly IReadOnlyList<string> All = [Open, Locked, Closed];
}

/// <summary>§10.2 — an opening balance entry is Draft until posted.</summary>
public static class OpeningEntryStatuses
{
    public const string Draft = "Draft", Posted = "Posted";
    public static readonly IReadOnlyList<string> All = [Draft, Posted];
}

/// <summary>D-10 — journal entry types. A42 only ever writes OPENING_BALANCE; A44 adds one per FinDocType (upper snake).</summary>
public static class JournalEntryTypes
{
    public const string OpeningBalance = "OPENING_BALANCE";

    /// <summary>A44 §3 — FinDocType name → upper snake: SalesInvoice → SALES_INVOICE, POSSale → POS_SALE.</summary>
    public static string For(string documentType) =>
        System.Text.RegularExpressions.Regex.Replace(documentType, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_").ToUpperInvariant();
}

/// <summary>D-10 / D-29 — journal entry status (A42 writes Posted only; Draft exists for A43 and the period-close check).</summary>
public static class JournalStatuses
{
    public const string Draft = "Draft", Posted = "Posted";
    public static readonly IReadOnlyList<string> All = [Draft, Posted];
}

/// <summary>§12.3 — external financial systems an account can be mapped to (D-21).</summary>
public static class ExternalSystemTypes
{
    public const string QuickBooksOnline = "QUICKBOOKS_ONLINE", QuickBooksDesktop = "QUICKBOOKS_DESKTOP", Xero = "XERO",
        ErpNext = "ERPNEXT", Tally = "TALLY", Sage = "SAGE", SapB1 = "SAP_B1", DynamicsBc = "DYNAMICS_BC",
        ZohoBooks = "ZOHO_BOOKS", Wave = "WAVE", Odoo = "ODOO", Custom = "CUSTOM";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [QuickBooksOnline]  = "QuickBooks Online",
        [QuickBooksDesktop] = "QuickBooks Desktop",
        [Xero]              = "Xero",
        [ErpNext]           = "ERPNext",
        [Tally]             = "Tally Prime",
        [Sage]              = "Sage",
        [SapB1]             = "SAP Business One",
        [DynamicsBc]        = "Microsoft Dynamics 365 Business Central",
        [ZohoBooks]         = "Zoho Books",
        [Wave]              = "Wave Accounting",
        [Odoo]              = "Odoo Accounting",
        [Custom]            = "Custom",
    };

    public static readonly IReadOnlyList<string> All = DisplayNames.Keys.ToList();
}

public static class SyncDirections
{
    public const string None = "None", Inbound = "Inbound", Outbound = "Outbound", Bidirectional = "Bidirectional";
    public static readonly IReadOnlyList<string> All = [None, Inbound, Outbound, Bidirectional];
}

public static class SyncStatuses
{
    public const string Success = "Success", Error = "Error", Pending = "Pending";
    public static readonly IReadOnlyList<string> All = [Success, Error, Pending];
}

/// <summary>§8.2 — the groups the accounting defaults are shown in.</summary>
public static class DefaultCategories
{
    public const string Receivables = "Receivables", Payables = "Payables", Banking = "Banking", Tax = "Tax", Revenue = "Revenue",
        CostOfSales = "CostOfSales", Inventory = "Inventory", Equity = "Equity";

    /// <summary>Display order of the categories.</summary>
    public static readonly IReadOnlyList<string> All = [Receivables, Payables, Banking, Revenue, CostOfSales, Inventory, Tax, Equity];
}

/// <summary>One of the 16 accounting defaults of §8.3 (seeded per organization, D-15).</summary>
public sealed record DefaultDefinition(string Key, string Label, string Category, bool IsRequired, string ExpectedAccountType, string HelpText, int SortOrder);

/// <summary>§8.3 — the 16 accounting default keys.</summary>
public static class DefaultKeys
{
    public const string Receivable = "DEFAULT_RECEIVABLE", Payable = "DEFAULT_PAYABLE", Cash = "DEFAULT_CASH", Bank = "DEFAULT_BANK",
        Revenue = "DEFAULT_REVENUE", Cogs = "DEFAULT_COGS", TaxReceivable = "DEFAULT_TAX_RECEIVABLE", TaxPayable = "DEFAULT_TAX_PAYABLE",
        Inventory = "DEFAULT_INVENTORY", Purchase = "DEFAULT_PURCHASE", DiscountGiven = "DEFAULT_DISCOUNT_GIVEN",
        DiscountReceived = "DEFAULT_DISCOUNT_RECEIVED", RetainedEarnings = "DEFAULT_RETAINED_EARNINGS",
        OpeningBalance = "DEFAULT_OPENING_BALANCE", ExchangeGainLoss = "DEFAULT_EXCHANGE_GAIN_LOSS", Rounding = "DEFAULT_ROUNDING";

    public static readonly IReadOnlyList<DefaultDefinition> Definitions =
    [
        new(Receivable,       "Accounts Receivable",         DefaultCategories.Receivables, true,  AccountTypes.Receivable,
            "Customer balances for unpaid invoices. Sales invoices are debited here until the customer pays.", 10),
        new(Payable,          "Accounts Payable",            DefaultCategories.Payables,    true,  AccountTypes.Payable,
            "Supplier balances for unpaid bills. Supplier invoices are credited here until they are paid.", 20),
        new(Cash,             "Cash Account",                DefaultCategories.Banking,     true,  AccountTypes.Cash,
            "Primary cash account: cash sales and cash payments are recorded here.", 30),
        new(Bank,             "Bank Account",                DefaultCategories.Banking,     true,  AccountTypes.Bank,
            "Primary bank account: bank receipts and payments are recorded here.", 40),
        new(Revenue,          "Sales Revenue",               DefaultCategories.Revenue,     true,  AccountTypes.Revenue,
            "Default revenue account for sales.", 50),
        new(DiscountGiven,    "Discount Given",              DefaultCategories.Revenue,     false, AccountTypes.Revenue,
            "Discounts given to customers.", 60),
        new(ExchangeGainLoss, "Exchange Gain/Loss",          DefaultCategories.Revenue,     false, AccountTypes.OtherIncome,
            "Realized foreign exchange differences.", 70),
        new(Rounding,         "Rounding Adjustment",         DefaultCategories.Revenue,     false, AccountTypes.OtherExpense,
            "Rounding differences in payments.", 80),
        new(Cogs,             "Cost of Goods Sold",          DefaultCategories.CostOfSales, true,  AccountTypes.CostOfGoodsSold,
            "Default cost of goods sold account: the cost of stock that was sold.", 90),
        new(Purchase,         "Purchase Account",            DefaultCategories.CostOfSales, false, AccountTypes.Expense,
            "Default account for purchase transactions.", 100),
        new(DiscountReceived, "Discount Received",           DefaultCategories.CostOfSales, false, AccountTypes.OtherIncome,
            "Discounts received from suppliers.", 110),
        new(Inventory,        "Inventory",                   DefaultCategories.Inventory,   false, AccountTypes.OtherCurrentAsset,
            "Default inventory holding account: the value of stock on hand.", 120),
        new(TaxReceivable,    "Tax Receivable (Input VAT)",  DefaultCategories.Tax,         false, AccountTypes.Tax,
            "Input tax / VAT paid on purchases.", 130),
        new(TaxPayable,       "Tax Payable (Output VAT)",    DefaultCategories.Tax,         false, AccountTypes.Tax,
            "Output tax / VAT collected on sales.", 140),
        new(RetainedEarnings, "Retained Earnings",           DefaultCategories.Equity,      true,  AccountTypes.Equity,
            "Year-end profit/loss closing account.", 150),
        new(OpeningBalance,   "Opening Balance Equity",      DefaultCategories.Equity,      true,  AccountTypes.Equity,
            "Assigned automatically to account 3900 Opening Balance Equity, which absorbs any difference in the opening balances. It cannot be changed.", 160),
    ];

    public static readonly IReadOnlyDictionary<string, DefaultDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> All = Definitions.Select(d => d.Key).ToList();

    /// <summary>D-24 — DEFAULT_OPENING_BALANCE is auto-assigned to 3900 and cannot be changed by a user.</summary>
    public static bool IsLocked(string key) => key == OpeningBalance;
}

/// <summary>D-9 — the system accounts every chart has.</summary>
public static class SystemAccounts
{
    public const string OpeningBalanceEquityCode = "3900";
    public const string OpeningBalanceEquityName = "Opening Balance Equity";
    /// <summary>Created when 3900 is missing (L2 / L3 parents, both Equity).</summary>
    public const string OpeningBalancesGroupCode = "39", OpeningBalancesGroupName = "Opening Balances";
    public const string OpeningBalanceEquityGroupCode = "390", OpeningBalanceEquityGroupName = "Opening Balance Equity";

    public static readonly IReadOnlyDictionary<string, string> RootNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RootTypes.Asset] = "Assets", [RootTypes.Liability] = "Liabilities", [RootTypes.Equity] = "Equity",
        [RootTypes.Revenue] = "Revenue", [RootTypes.Expense] = "Expenses",
    };
}

/// <summary>D-25 — size limits.</summary>
public static class GlLimits
{
    public const int MaxAccounts = 2000;
    public const long MaxUploadBytes = 5 * 1024 * 1024;
    public const int MaxCodeLength = 20;
}
