namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "Templates" and "Chart of accounts".

public sealed class CoaTemplateModel
{
    public Guid    Uuid          { get; set; }
    public string  TemplateCode  { get; set; } = string.Empty;
    public string  TemplateName  { get; set; } = string.Empty;
    public string? CountryCode   { get; set; }
    public string? BusinessType  { get; set; }
    public string? CurrencyCode  { get; set; }
    public string? Description   { get; set; }
    public int     AccountCount  { get; set; }
    public string  Version       { get; set; } = string.Empty;
    public bool    IsSystem      { get; set; }
}

public sealed class CoaTemplateDetailModel
{
    public Guid    Uuid          { get; set; }
    public string  TemplateCode  { get; set; } = string.Empty;
    public string  TemplateName  { get; set; } = string.Empty;
    public string? CountryCode   { get; set; }
    public string? BusinessType  { get; set; }
    public string? CurrencyCode  { get; set; }
    public string? Description   { get; set; }
    public int     AccountCount  { get; set; }
    public string  Version       { get; set; } = string.Empty;
    public bool    IsSystem      { get; set; }
    public List<AccountNodeModel> Accounts { get; set; } = [];
}

public sealed class TemplateFilterOption
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class TemplateFiltersModel
{
    public List<TemplateFilterOption> Countries     { get; set; } = [];
    public List<string>               BusinessTypes { get; set; } = [];
}

public sealed class TemplateItemModel
{
    public string  AccountCode       { get; set; } = string.Empty;
    public string  AccountName       { get; set; } = string.Empty;
    public string? ParentCode        { get; set; }
    public int     Level             { get; set; }
    public string  RootType          { get; set; } = string.Empty;
    public bool    IsGroup           { get; set; }
    public string? AccountType       { get; set; }
    public string? Description       { get; set; }
    public bool    IsDefaultAccount  { get; set; }
    public string? DefaultAccountKey { get; set; }
}

public sealed class AccountNodeModel
{
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string  RootType    { get; set; } = string.Empty;
    public int     Level       { get; set; }
    public bool    IsGroup     { get; set; }
    public string? AccountType { get; set; }
    public string? Description { get; set; }
    public List<AccountNodeModel> Children { get; set; } = [];
}

/// <summary>A downloadable file (template / chart export, blank template).</summary>
public sealed record GlFile(byte[] Content, string FileName, string ContentType);

public class AccountModel
{
    public Guid    Uuid                 { get; set; }
    public string  AccountCode          { get; set; } = string.Empty;
    public string  AccountName          { get; set; } = string.Empty;
    public Guid?   ParentUuid           { get; set; }
    public string? ParentCode           { get; set; }
    public string  MaterializedPath     { get; set; } = string.Empty;
    public int     Level                { get; set; }
    public string  RootType             { get; set; } = string.Empty;
    public bool    IsGroup              { get; set; }
    public string? AccountType          { get; set; }
    public string? Description          { get; set; }
    public string? ExternalCode         { get; set; }
    public bool    IsSystemAccount      { get; set; }
    public bool    IsActive             { get; set; }
    public bool    HasTransactions      { get; set; }
    public decimal OpeningBalanceDebit  { get; set; }
    public decimal OpeningBalanceCredit { get; set; }
    public decimal CurrentBalance       { get; set; }
    public int     SortOrder            { get; set; }
    /// <summary>Computed for every account (tree, flat, detail, subtree) in one batch — the same rules as DELETE.</summary>
    public bool    CanDelete            { get; set; }
    public string? DeleteBlockedReason  { get; set; }
}

public sealed class AccountTreeNodeModel : AccountModel
{
    public List<AccountTreeNodeModel> Children { get; set; } = [];
}

public sealed class AccountParentRef
{
    public Guid   Uuid        { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
}

public sealed class AccountDetailModel : AccountModel
{
    public AccountParentRef? Parent              { get; set; }
    public int               ChildrenCount       { get; set; }
    public DateTime          CreatedAt           { get; set; }
    public DateTime?         ModifiedAt          { get; set; }
}

public sealed class ChartSummaryModel
{
    public int       TotalAccounts    { get; set; }
    public int       PostableAccounts { get; set; }
    public DateTime? LastModifiedAt   { get; set; }
}

public sealed class ChartTreeModel
{
    public List<AccountTreeNodeModel> Roots   { get; set; } = [];
    public ChartSummaryModel          Summary { get; set; } = new();
}

public sealed class AccountFlatQuery
{
    public int?    Level       { get; set; }
    public string? RootType    { get; set; }
    public bool?   IsGroup     { get; set; }
    public bool?   IsActive    { get; set; }
    public string? Search      { get; set; }
    public string? AccountType { get; set; }
}

public sealed class AccountCreateRequest
{
    public Guid    ParentUuid   { get; set; }
    public string  AccountCode  { get; set; } = string.Empty;
    public string  AccountName  { get; set; } = string.Empty;
    public string? AccountType  { get; set; }
    public string? Description  { get; set; }
    public string? ExternalCode { get; set; }
    public int?    SortOrder    { get; set; }
}

public sealed class AccountUpdateRequest
{
    public string  AccountName  { get; set; } = string.Empty;
    public string? AccountType  { get; set; }
    public string? Description  { get; set; }
    public string? ExternalCode { get; set; }
    public int?    SortOrder    { get; set; }
}

public sealed class ApplyTemplateRequest
{
    public Guid TemplateUuid { get; set; }
}

public sealed class ApplyResultModel
{
    public int    AccountsCreated  { get; set; }
    public int    DefaultsAssigned { get; set; }
    public string Status           { get; set; } = string.Empty;
}

public sealed class SuggestCodeModel
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>One upload problem. CSV: <see cref="Line"/> (1 = header). JSON: <see cref="Path"/> (e.g. accounts[0].children[2]) and <see cref="Code"/>.</summary>
public sealed class UploadError
{
    public int?    Line    { get; set; }
    public string? Code    { get; set; }
    public string? Path    { get; set; }
    /// <summary>R1..R13 (spec §5.5).</summary>
    public string  Rule    { get; set; } = string.Empty;
    public string  Message { get; set; } = string.Empty;
}
