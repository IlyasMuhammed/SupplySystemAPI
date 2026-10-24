namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "Accounting defaults" (BE2).

public sealed class AccountingDefaultModel
{
    public string  DefaultKey          { get; set; } = string.Empty;
    public string  DefaultLabel        { get; set; } = string.Empty;
    public string  Category            { get; set; } = string.Empty;
    public bool    IsRequired          { get; set; }
    public string? ExpectedAccountType { get; set; }
    public string? HelpText            { get; set; }
    public bool    IsLocked            { get; set; }
    public AccountRefModel? Account    { get; set; }
    /// <summary>D-24 — e.g. the assigned account's type differs from the expected one (never blocks).</summary>
    public string? Warning             { get; set; }
    /// <summary>The assignment is no longer valid (account inactive or became a group) — counts as unassigned.</summary>
    public string? Problem             { get; set; }
}

public sealed class AccountRefModel
{
    public Guid    Uuid        { get; set; }
    public string  AccountCode { get; set; } = string.Empty;
    public string  AccountName { get; set; } = string.Empty;
    public string? AccountType { get; set; }
    /// <summary>A47 — the account's root type (journal lines, trial balance, reconciliation).</summary>
    public string? RootType    { get; set; }
}

public sealed class AccountingDefaultCategoryModel
{
    public string Category { get; set; } = string.Empty;
    public List<AccountingDefaultModel> Items { get; set; } = [];
}

public sealed class AccountingDefaultsModel
{
    public List<AccountingDefaultCategoryModel> Categories { get; set; } = [];
    public int RequiredCount         { get; set; }
    public int AssignedRequiredCount { get; set; }
}

public sealed class AssignDefaultRequest
{
    public Guid? AccountUuid { get; set; }
}

public sealed class SaveDefaultsRequest
{
    public List<SaveDefaultItem> Items { get; set; } = [];
}

public sealed class SaveDefaultItem
{
    public string? DefaultKey  { get; set; }
    public Guid?   AccountUuid { get; set; }
}

public sealed class DefaultMessageModel
{
    public string DefaultKey { get; set; } = string.Empty;
    public string Message    { get; set; } = string.Empty;
}

public sealed class DefaultsValidationModel
{
    public bool IsValid { get; set; }
    public List<string> MissingRequired { get; set; } = [];
    public List<DefaultMessageModel> Problems { get; set; } = [];
    public List<DefaultMessageModel> Warnings { get; set; } = [];
}
