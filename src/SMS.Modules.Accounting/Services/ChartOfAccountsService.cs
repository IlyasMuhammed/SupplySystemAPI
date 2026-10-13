using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>An upload refused by R1–R13: every problem at once (400 <c>{ message, errors }</c>).</summary>
public sealed class CoaUploadException : BadRequestException
{
    public IReadOnlyList<UploadError> Errors { get; }

    public CoaUploadException(IReadOnlyList<UploadError> errors)
        : base(errors.Count == 1 ? errors[0].Message : $"The file has {errors.Count} problems. Nothing was imported.") => Errors = errors;
}

/// <summary>
/// A42 API-CONTRACT "Chart of accounts" — apply a template / upload a file (replace the chart: BR-TPL-004/005, D-16),
/// account CRUD (BR-COA-*, D-8, D-9, D-23, D-25, D-30), tree/flat/detail/suggest/export and subtree/ancestry queries.
/// Every write commits first, then publishes its event and calls <see cref="ISetupStatusService.RecomputeAsync"/>.
/// </summary>
public interface IChartOfAccountsService
{
    Task<ApplyResultModel> ApplyTemplateAsync(AccountingCaller caller, Guid templateUuid, CancellationToken ct = default);
    /// <summary>Throws <see cref="CoaUploadException"/> with every R1–R13 problem.</summary>
    Task<ApplyResultModel> UploadAsync(AccountingCaller caller, string fileName, byte[] content, CancellationToken ct = default);
    /// <summary>FSD §9.3 "Manual": replaces the chart with the 5 roots + 39/390/3900 only (same replace rules as apply/upload).</summary>
    Task<ApplyResultModel> StartBlankAsync(AccountingCaller caller, CancellationToken ct = default);

    Task<AccountDetailModel> CreateAsync(AccountingCaller caller, AccountCreateRequest request, CancellationToken ct = default);
    Task<AccountDetailModel> UpdateAsync(AccountingCaller caller, Guid uuid, AccountUpdateRequest request, CancellationToken ct = default);
    /// <summary>Soft delete (IsActive = false).</summary>
    Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);

    Task<ChartTreeModel> GetTreeAsync(AccountingCaller caller, bool includeInactive = false, CancellationToken ct = default);
    Task<List<AccountModel>> GetFlatAsync(AccountingCaller caller, AccountFlatQuery query, CancellationToken ct = default);
    Task<AccountDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SuggestCodeModel> SuggestCodeAsync(AccountingCaller caller, Guid parentUuid, CancellationToken ct = default);
    Task<GlFile> ExportAsync(AccountingCaller caller, string? format, CancellationToken ct = default);
    /// <summary>The account's active descendants (materialized path LIKE '/1/11/%'), depth-first.</summary>
    Task<List<AccountModel>> GetSubtreeAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    /// <summary>The account's ancestors, root first (recursive CTE on SQL Server, parent walk otherwise).</summary>
    Task<List<AccountModel>> GetAncestorsAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed partial class ChartOfAccountsService : IChartOfAccountsService
{
    internal const string ReplaceRefusal = "The chart can only be replaced before the fiscal year is set and before opening balances are posted.";
    internal const string R13Message = "Chart of Accounts cannot be replaced after journal entries have been posted. Add or modify individual accounts instead.";
    internal const string NotFoundMessage = "Account not found.";
    internal const string NoItemsMessage = "Accounts for this template are coming. Choose another template or upload a file.";

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex CodePattern();

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly ISetupStatusService _setup;
    private readonly IAccountingEventPublisher _events;

    public ChartOfAccountsService(AccountingDbContext db, IAccountingInitializer init, ISetupStatusService setup, IAccountingEventPublisher events)
    {
        _db = db;
        _init = init;
        _setup = setup;
        _events = events;
    }

    private IQueryable<ChartOfAccount> Accounts(Guid org) => _db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org);

    private async Task<ChartOfAccount> FindAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await Accounts(org).FirstOrDefaultAsync(a => a.Uuid == uuid, ct) ?? throw new NotFoundException(NotFoundMessage);

    // ── Replace the chart: apply a template / upload a file ─────────────────

    public async Task<ApplyResultModel> ApplyTemplateAsync(AccountingCaller caller, Guid templateUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        var template = await _db.CoaTemplates.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Uuid == templateUuid && t.IsActive && (t.IsGlobal || t.OrganizationId == org), ct)
            ?? throw new NotFoundException(TemplateService.NotFoundMessage);

        await EnsureReplaceableAsync(caller, ct, uploadErrors: null);
        var items = await _db.CoaTemplateItems.AsNoTracking().Where(i => i.TemplateId == template.Id).ToListAsync(ct);
        if (items.Count == 0) throw new ConflictException(NoItemsMessage);

        var (created, defaults) = await ReplaceChartAsync(caller, TemplateService.TemplateRows(items), template.Id, ct);
        await _events.PublishAsync(new CoaTemplateAppliedEvent(org, template.Uuid, template.TemplateCode, template.TemplateName, created,
            caller.UserIdOrNull, caller.UserName), ct);
        var status = await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return new ApplyResultModel { AccountsCreated = created, DefaultsAssigned = defaults, Status = status };
    }

    public async Task<ApplyResultModel> UploadAsync(AccountingCaller caller, string fileName, byte[] content, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        if (content.LongLength > GlLimits.MaxUploadBytes) throw new BadRequestException("The file must be 5 MB or smaller.");

        // R13 first: nothing else matters once journals are posted.
        var r13 = new List<UploadError>();
        await EnsureReplaceableAsync(caller, ct, r13);
        if (r13.Count > 0) throw new CoaUploadException(r13);

        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        CoaParseResult parsed;
        var text = DecodeText(content);
        if (extension == CoaFile.Csv) parsed = CoaFile.ParseCsv(text);
        else if (extension == CoaFile.Json) parsed = CoaFile.ParseJson(text, out _);
        else throw new CoaUploadException([new UploadError { Rule = "R1", Message = CoaFile.R1Message }]);

        var errors = parsed.Errors.Concat(parsed.Rows.Count > 0 || parsed.Errors.Count == 0 ? CoaValidator.Validate(parsed.Rows) : []).ToList();
        if (errors.Count > 0) throw new CoaUploadException(errors);

        var (created, defaults) = await ReplaceChartAsync(caller, parsed.Rows, null, ct);
        await _events.PublishAsync(new CoaUploadedEvent(org, Path.GetFileName(fileName ?? "upload"), created, parsed.Format,
            caller.UserIdOrNull, caller.UserName), ct);
        var status = await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return new ApplyResultModel { AccountsCreated = created, DefaultsAssigned = defaults, Status = status };
    }

    /// <summary>The CoaUploadedEvent format of <see cref="StartBlankAsync"/>.</summary>
    internal const string BlankFormat = "BLANK";

    public async Task<ApplyResultModel> StartBlankAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        await EnsureReplaceableAsync(caller, ct, uploadErrors: null);

        var rows = RootTypes.All.Select((root, i) => new CoaRow
        {
            Code = RootTypes.RootCode[root], Name = SystemAccounts.RootNames[root], Level = 1, RootType = root, IsGroup = true, SortOrder = (i + 1) * 10,
        }).ToList();
        var (created, defaults) = await ReplaceChartAsync(caller, rows, null, ct);
        await _events.PublishAsync(new CoaUploadedEvent(org, "blank", created, BlankFormat, caller.UserIdOrNull, caller.UserName), ct);
        var status = await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return new ApplyResultModel { AccountsCreated = created, DefaultsAssigned = defaults, Status = status };
    }

    private static string DecodeText(byte[] content) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(content).TrimStart('﻿');

    /// <summary>R13 (posted journals) and BR-TPL-004 / D-16 (before the fiscal year and before posted opening balances).</summary>
    private async Task EnsureReplaceableAsync(AccountingCaller caller, CancellationToken ct, List<UploadError>? uploadErrors)
    {
        var org = caller.OrganizationId;
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        if (await _setup.HasPostedJournalsAsync(org, ct))
        {
            if (uploadErrors is null) throw new ConflictException(R13Message);
            uploadErrors.Add(new UploadError { Rule = "R13", Message = R13Message });
            return;
        }
        var status = await _db.CoaSetupStatuses.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.OrganizationId == org, ct);
        var hasYear = await _db.FiscalYears.IgnoreQueryFilters().AnyAsync(y => y.OrganizationId == org, ct);
        if (!SetupStatuses.AllowsChartReplacement(status.Status) || hasYear || status.OpeningBalancesPostedAt is not null)
            throw new ConflictException(ReplaceRefusal);
    }

    /// <summary>
    /// BR-TPL-005 — one transaction: removes the organization's chart (draft opening balances, mappings and default
    /// assignments that point at it first), creates the new one with parents, materialized paths and D-9 system accounts,
    /// and assigns the defaults the rows mark (plus DEFAULT_OPENING_BALANCE → 3900).
    /// </summary>
    private async Task<(int Created, int Defaults)> ReplaceChartAsync(AccountingCaller caller, List<CoaRow> rows, int? templateId, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        rows = WithOpeningBalanceEquity(rows);
        var created = 0;
        var assigned = 0;

        await _db.InTransactionAsync(async () =>
        {
            await DeleteWhereAsync(_db.OpeningBalanceEntries.IgnoreQueryFilters().Where(e => e.OrganizationId == org), ct);
            await DeleteWhereAsync(_db.ExternalSystemMappings.IgnoreQueryFilters().Where(m => m.OrganizationId == org), ct);
            var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == org).ToListAsync(ct);
            foreach (var d in defaults) d.AccountId = null;
            await _db.SaveChangesAsync(ct);
            await DeleteWhereAsync(Accounts(org), ct);

            var byCode = new Dictionary<string, ChartOfAccount>(StringComparer.Ordinal);
            foreach (var r in rows.OrderBy(r => r.Level))
            {
                var parent = r.ParentCode is null ? null : byCode[r.ParentCode];
                var account = new ChartOfAccount
                {
                    OrganizationId = org,
                    AccountCode = r.Code,
                    AccountName = r.Name,
                    Parent = parent,
                    MaterializedPath = (parent?.MaterializedPath ?? "") + "/" + r.Code,
                    Level = (byte)r.Level!.Value,
                    RootType = r.RootType,
                    IsGroup = r.Level < 4,
                    AccountType = r.Level == 4 ? r.AccountType : null,
                    Description = r.Description,
                    IsSystemAccount = r.Level == 1 || r.Code == SystemAccounts.OpeningBalanceEquityCode,
                    SortOrder = r.SortOrder,
                    SourceTemplateId = templateId,
                    CreatedBy = caller.UserIdOrNull,
                    ModifiedBy = caller.UserIdOrNull,
                };
                byCode[r.Code] = account;
                _db.ChartOfAccounts.Add(account);
            }
            await _db.SaveChangesAsync(ct);
            created = byCode.Count;

            var keyed = rows.Where(r => r.IsDefaultAccount && r.DefaultAccountKey is not null && r.Level == 4)
                .GroupBy(r => r.DefaultAccountKey!).ToDictionary(g => g.Key, g => g.First().Code);
            keyed[DefaultKeys.OpeningBalance] = SystemAccounts.OpeningBalanceEquityCode;
            foreach (var d in defaults)
            {
                if (!keyed.TryGetValue(d.DefaultKey, out var code) || !byCode.TryGetValue(code, out var account)) continue;
                d.AccountId = account.Id;
                d.ModifiedBy = caller.UserIdOrNull;
                assigned++;
            }

            var status = await _db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == org, ct);
            status.TemplateId = templateId;
            status.TemplateAppliedAt = templateId is null ? null : DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }, ct);

        return (created, assigned);
    }

    /// <summary>D-9 — adds 3900 Opening Balance Equity (and its 39 / 390 groups when missing) to validated rows.</summary>
    internal static List<CoaRow> WithOpeningBalanceEquity(List<CoaRow> rows)
    {
        var codes = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Contains(SystemAccounts.OpeningBalanceEquityCode)) return rows;
        var result = new List<CoaRow>(rows);
        if (!codes.Contains(SystemAccounts.OpeningBalanceEquityGroupCode))
        {
            if (!codes.Contains(SystemAccounts.OpeningBalancesGroupCode))
                result.Add(new CoaRow { Code = SystemAccounts.OpeningBalancesGroupCode, Name = SystemAccounts.OpeningBalancesGroupName,
                    ParentCode = RootTypes.RootCode[RootTypes.Equity], Level = 2, RootType = RootTypes.Equity, IsGroup = true, SortOrder = 990 });
            result.Add(new CoaRow { Code = SystemAccounts.OpeningBalanceEquityGroupCode, Name = SystemAccounts.OpeningBalanceEquityGroupName,
                ParentCode = SystemAccounts.OpeningBalancesGroupCode, Level = 3, RootType = RootTypes.Equity, IsGroup = true, SortOrder = 10 });
        }
        result.Add(new CoaRow { Code = SystemAccounts.OpeningBalanceEquityCode, Name = SystemAccounts.OpeningBalanceEquityName,
            ParentCode = SystemAccounts.OpeningBalanceEquityGroupCode, Level = 4, RootType = RootTypes.Equity, IsGroup = false,
            AccountType = AccountTypes.Equity, Description = "Balancing account for opening balances; should be zero after setup.", SortOrder = 10 });
        return result;
    }

    /// <summary>A set-based delete on SQL Server (one statement — the self-referencing chart goes in one go); the
    /// in-memory test provider loads and removes.</summary>
    private async Task DeleteWhereAsync<T>(IQueryable<T> rows, CancellationToken ct) where T : class
    {
        if (_db.Database.IsRelational())
        {
            await rows.ExecuteDeleteAsync(ct);
            foreach (var entry in _db.ChangeTracker.Entries<T>().ToList()) entry.State = EntityState.Detached;
            return;
        }
        _db.RemoveRange(await rows.ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
    }

    // ── Account CRUD ─────────────────────────────────────────────────────────

    private static void CheckText(string? value, int max, string label)
    {
        if (value is not null && value.Length > max) throw new BadRequestException($"{label} must be {max} characters or fewer.");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>D-23 — groups have no type; a leaf's type must be valid for its root.</summary>
    private static void CheckAccountType(string? accountType, int level, string rootType)
    {
        if (accountType is null) return;
        if (level < 4) throw new BadRequestException("A group account cannot have an account type.");
        if (!AccountTypes.IsValidFor(accountType, rootType))
            throw new BadRequestException($"Account type {accountType} is not valid for {rootType} accounts.");
    }

    public async Task<AccountDetailModel> CreateAsync(AccountingCaller caller, AccountCreateRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageAccounts);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var parent = await Accounts(org).FirstOrDefaultAsync(a => a.Uuid == request.ParentUuid && a.IsActive, ct)
            ?? throw new NotFoundException("Parent account not found.");
        if (!parent.IsGroup || parent.Level >= 4) throw new BadRequestException("Accounts can only be added under a group account (levels 1–3).");

        var level = parent.Level + 1;
        var code = (request.AccountCode ?? string.Empty).Trim();
        var name = (request.AccountName ?? string.Empty).Trim();
        if (code.Length == 0) throw new BadRequestException("Account code is required.");
        if (!CodePattern().IsMatch(code)) throw new BadRequestException($"Account code '{code}' contains invalid characters. Use digits only.");
        CheckText(code, GlLimits.MaxCodeLength, "Account code");
        var digit = RootTypes.RootCode[parent.RootType];
        if (!code.StartsWith(digit, StringComparison.Ordinal))
            throw new BadRequestException($"Account code must start with {digit}, the code of {parent.RootType} accounts.");
        if (name.Length == 0) throw new BadRequestException("Account name is required.");
        CheckText(name, 200, "Account name");
        CheckText(request.Description, 500, "Description");
        CheckText(request.ExternalCode, 50, "External code");
        var accountType = Blank(request.AccountType);
        CheckAccountType(accountType, level, parent.RootType);

        if (await Accounts(org).AnyAsync(a => a.IsActive && a.AccountCode == code, ct)) throw new ConflictException($"Account code {code} already exists.");
        if (await Accounts(org).CountAsync(a => a.IsActive, ct) >= GlLimits.MaxAccounts)
            throw new ConflictException($"A tenant can have at most {GlLimits.MaxAccounts} accounts.");

        var sortOrder = request.SortOrder
            ?? (await Accounts(org).Where(a => a.ParentAccountId == parent.Id).Select(a => (int?)a.SortOrder).MaxAsync(ct) ?? 0) + 10;
        var account = new ChartOfAccount
        {
            OrganizationId = org,
            AccountCode = code,
            AccountName = name,
            ParentAccountId = parent.Id,
            MaterializedPath = parent.MaterializedPath + "/" + code,
            Level = (byte)level,
            RootType = parent.RootType,
            IsGroup = level < 4,
            AccountType = accountType,
            Description = Blank(request.Description),
            ExternalCode = Blank(request.ExternalCode),
            SortOrder = sortOrder,
            CreatedBy = caller.UserIdOrNull,
            ModifiedBy = caller.UserIdOrNull,
        };
        _db.ChartOfAccounts.Add(account);
        await _db.SaveChangesAsync(ct);

        await _events.PublishAsync(new ChartOfAccountCreatedEvent(org, account.Uuid, account.AccountCode, account.AccountName, account.Level,
            account.RootType, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return await GetAsync(caller, account.Uuid, ct);
    }

    public async Task<AccountDetailModel> UpdateAsync(AccountingCaller caller, Guid uuid, AccountUpdateRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageAccounts);
        var org = caller.OrganizationId;
        var account = await FindAsync(org, uuid, ct);
        if (!account.IsActive) throw new ConflictException("This account was deleted and cannot be changed.");

        var name = (request.AccountName ?? string.Empty).Trim();
        if (name.Length == 0) throw new BadRequestException("Account name is required.");
        CheckText(name, 200, "Account name");
        CheckText(request.Description, 500, "Description");
        CheckText(request.ExternalCode, 50, "External code");
        var accountType = Blank(request.AccountType);
        CheckAccountType(accountType, account.Level, account.RootType);

        var changed = new List<string>();
        if (account.AccountName != name)
        {
            if (account.AccountCode == SystemAccounts.OpeningBalanceEquityCode && account.IsSystemAccount)
                throw new ConflictException("Opening Balance Equity (3900) cannot be renamed.");
            if (account.Level == 1 && await IsCompleteAsync(org, ct))
                throw new ConflictException("Root accounts are locked once the setup is complete.");
            account.AccountName = name;
            changed.Add("accountName");
        }
        if (account.AccountType != accountType)
        {
            if (account.AccountCode == SystemAccounts.OpeningBalanceEquityCode && account.IsSystemAccount)
                throw new ConflictException("The account type of Opening Balance Equity (3900) cannot be changed.");
            account.AccountType = accountType;
            changed.Add("accountType");
        }
        var description = Blank(request.Description);
        if (account.Description != description) { account.Description = description; changed.Add("description"); }
        var external = Blank(request.ExternalCode);
        if (account.ExternalCode != external) { account.ExternalCode = external; changed.Add("externalCode"); }
        if (request.SortOrder is { } sort && account.SortOrder != sort) { account.SortOrder = sort; changed.Add("sortOrder"); }

        if (changed.Count > 0)
        {
            account.ModifiedBy = caller.UserIdOrNull;
            await _db.SaveChangesAsync(ct);
            await _events.PublishAsync(new ChartOfAccountUpdatedEvent(org, account.Uuid, account.AccountCode, changed, caller.UserIdOrNull, caller.UserName), ct);
        }
        return await GetAsync(caller, uuid, ct);
    }

    private Task<bool> IsCompleteAsync(Guid org, CancellationToken ct) =>
        _db.CoaSetupStatuses.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.Status == SetupStatuses.Complete, ct);

    /// <summary>Why an account cannot be deleted (BR-COA-005/006/007, D-24/D-30, D-19, BR-INT-003), or null.</summary>
    internal async Task<string?> DeleteBlockedReasonAsync(ChartOfAccount account, CancellationToken ct) =>
        (await DeleteBlockedReasonsAsync(account.OrganizationId, [account], ct))[account.Id];

    /// <summary>
    /// The delete refusal of many accounts at once — four queries whatever the count (no N+1): active children, default
    /// assignments, non-zero opening balances and external mappings of the given accounts. Order of the rules as DELETE.
    /// </summary>
    internal async Task<Dictionary<int, string?>> DeleteBlockedReasonsAsync(Guid org, IReadOnlyCollection<ChartOfAccount> accounts, CancellationToken ct)
    {
        var ids = accounts.Where(a => a.IsActive && !a.IsSystemAccount).Select(a => a.Id).ToList();
        HashSet<int> withChildren = [], withOpening = [];
        Dictionary<int, string> labels = [], systems = [];
        if (ids.Count > 0)
        {
            withChildren = (await Accounts(org).Where(a => a.IsActive && a.ParentAccountId != null && ids.Contains(a.ParentAccountId.Value))
                .Select(a => a.ParentAccountId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
            labels = (await _db.AccountingDefaults.IgnoreQueryFilters()
                    .Where(d => d.OrganizationId == org && d.AccountId != null && ids.Contains(d.AccountId.Value))
                    .Select(d => new { AccountId = d.AccountId!.Value, d.DefaultLabel, d.SortOrder }).ToListAsync(ct))
                .GroupBy(d => d.AccountId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.SortOrder).First().DefaultLabel);
            withOpening = (await _db.OpeningBalanceEntries.IgnoreQueryFilters()
                    .Where(e => e.OrganizationId == org && ids.Contains(e.AccountId) && (e.DebitAmount != 0 || e.CreditAmount != 0))
                    .Select(e => e.AccountId).Distinct().ToListAsync(ct)).ToHashSet();
            systems = (await _db.ExternalSystemMappings.IgnoreQueryFilters()
                    .Where(m => m.OrganizationId == org && ids.Contains(m.AccountId))
                    .Select(m => new { m.AccountId, m.SystemName, m.Id }).ToListAsync(ct))
                .GroupBy(m => m.AccountId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).First().SystemName);
        }

        string? Reason(ChartOfAccount a)
        {
            if (!a.IsActive) return "This account is already deleted.";
            if (a.IsSystemAccount) return "Cannot delete a system account.";
            if (withChildren.Contains(a.Id)) return "Cannot delete an account that has child accounts.";
            if (a.HasTransactions) return "Cannot delete an account with posted transactions.";
            if (labels.TryGetValue(a.Id, out var label)) return $"This account is assigned as {label}. Assign another account first.";
            if (withOpening.Contains(a.Id)) return "This account has an opening balance. Remove it first.";
            if (systems.TryGetValue(a.Id, out var system)) return $"This account is mapped to {system}. Remove the mapping first.";
            return null;
        }

        return accounts.ToDictionary(a => a.Id, Reason);
    }

    public async Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageAccounts);
        var org = caller.OrganizationId;
        var account = await FindAsync(org, uuid, ct);
        if (await DeleteBlockedReasonAsync(account, ct) is { } reason) throw new ConflictException(reason);

        // A zero draft opening balance row goes with the account.
        foreach (var e in await _db.OpeningBalanceEntries.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.AccountId == account.Id).ToListAsync(ct))
            _db.OpeningBalanceEntries.Remove(e);
        account.IsActive = false;
        account.ModifiedBy = caller.UserIdOrNull;
        await _db.SaveChangesAsync(ct);

        await _events.PublishAsync(new ChartOfAccountDeletedEvent(org, account.Uuid, account.AccountCode, account.AccountName, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
    }
}
