using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 C1 — journal types (api/finance/journal-types, API-CONTRACT §1). Reads JE-READ, writes GL_SETUP_MANAGE.</summary>
public interface IJournalTypeService
{
    /// <summary>The organization's journal types (seeds the 7 system types when missing); <paramref name="activeOnly"/> hides deactivated ones.</summary>
    Task<IReadOnlyList<JournalTypeModel>> ListAsync(AccountingCaller caller, bool activeOnly = false, CancellationToken ct = default);
    /// <summary>One journal type of the caller's organization (404 otherwise).</summary>
    Task<JournalTypeModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    /// <summary>A custom journal type; code and sequence prefix unique per organization (D-CORE-5).</summary>
    Task<JournalTypeModel> CreateAsync(AccountingCaller caller, CreateJournalTypeRequest request, CancellationToken ct = default);
    /// <summary>Updates a type; a system type keeps its code and kind, a prefix is fixed once the type has numbered an entry.</summary>
    Task<JournalTypeModel> UpdateAsync(AccountingCaller caller, Guid uuid, UpdateJournalTypeRequest request, CancellationToken ct = default);
    /// <summary>Activates or deactivates a type (system journals of a deactivated type fall back to MISC, D-12).</summary>
    Task<JournalTypeModel> SetActiveAsync(AccountingCaller caller, Guid uuid, bool active, CancellationToken ct = default);
    /// <summary>Deletes a custom type that has never been used.</summary>
    Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class JournalTypeService : IJournalTypeService
{
    internal const string CodeMessage = "The code must be 1–10 letters or digits.";
    internal const string PrefixMessage = "The sequence prefix must be 1–10 letters or digits.";
    internal const string TypeMessage = "Type must be Sale, Purchase, Cash, Bank, Miscellaneous, Opening or Closing.";
    internal const string SystemFixedMessage = "The code and type of a system journal type cannot be changed.";
    internal const string AutoPostFixedMessage = "The Opening and Closing journal types always post automatically.";
    internal const string PrefixUsedMessage = "The sequence prefix cannot be changed once entries are numbered with it.";
    internal const string TypeUsedMessage = "The type cannot be changed once the journal type has entries.";
    internal const string SystemDeleteMessage = "System journal types cannot be deleted, only deactivated.";
    internal const string UsedDeleteMessage = "A journal type with entries cannot be deleted; deactivate it instead.";
    internal const string NotFoundMessage = "Journal type not found.";

    /// <summary>REV-6 — system types that cannot be deactivated (SALE, PURCH, CASH, BANK can: their journals fall back to MISC).</summary>
    internal static readonly IReadOnlyList<string> KeptActiveCodes = [SystemJournalTypes.Misc, SystemJournalTypes.Opening, SystemJournalTypes.Closing];

    internal static string KeepActiveMessage(string code) =>
        $"The {code} journal type cannot be deactivated: system entries (fallback, opening and year-end entries) are written to it.";

    private static readonly Regex CodePattern = new("^[A-Z0-9]{1,10}$", RegexOptions.Compiled);

    private readonly AccountingDbContext _db;
    private readonly JournalTypeSeeder _seeder;
    private readonly IAccountingEventPublisher _events;

    public JournalTypeService(AccountingDbContext db, JournalTypeSeeder seeder, IAccountingEventPublisher events)
    {
        _db = db;
        _seeder = seeder;
        _events = events;
    }

    public async Task<IReadOnlyList<JournalTypeModel>> ListAsync(AccountingCaller caller, bool activeOnly = false, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadJournals);
        var org = caller.OrganizationId;
        await _seeder.EnsureAsync(org, ct);
        var types = await Query(org).Where(t => !activeOnly || t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct);
        var used = await UsedIdsAsync(org, ct);
        return types.Select(t => ToModel(t, used.Contains(t.Id))).ToList();
    }

    public async Task<JournalTypeModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadJournals);
        var t = await Query(caller.OrganizationId).FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException(NotFoundMessage);
        return ToModel(t, await IsUsedAsync(t.Id, ct));
    }

    public async Task<JournalTypeModel> CreateAsync(AccountingCaller caller, CreateJournalTypeRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _seeder.EnsureAsync(org, ct);

        var code = Code(request.Code, CodeMessage);
        var prefix = Code(string.IsNullOrWhiteSpace(request.SequencePrefix) ? request.Code : request.SequencePrefix, PrefixMessage);
        var kind = Kind(request.Type);
        var type = new JournalType
        {
            OrganizationId = org, Code = code, Name = Name(request.Name), Type = kind, SequencePrefix = prefix,
            RequiresPartner = request.RequiresPartner, AutoPost = request.AutoPost, IsSystem = false, IsActive = true,
            SortOrder = request.SortOrder ?? 100, CreatedBy = caller.UserIdOrNull, ModifiedBy = caller.UserIdOrNull,
        };
        type.DefaultDebitAccountId = await DefaultAccountAsync(org, request.DefaultDebitAccountUuid, "debit", ct);
        type.DefaultCreditAccountId = await DefaultAccountAsync(org, request.DefaultCreditAccountUuid, "credit", ct);
        await EnsureUniqueAsync(org, type, null, ct);

        _db.JournalTypes.Add(type);
        await _db.SaveChangesAsync(ct);
        await PublishAsync(caller, type, "created", ct);
        return await GetAsync(caller, type.Uuid, ct);
    }

    public async Task<JournalTypeModel> UpdateAsync(AccountingCaller caller, Guid uuid, UpdateJournalTypeRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        var type = await _db.JournalTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrganizationId == org && t.Uuid == uuid, ct)
            ?? throw new NotFoundException(NotFoundMessage);

        var code = string.IsNullOrWhiteSpace(request.Code) ? type.Code : Code(request.Code, CodeMessage);
        var kind = string.IsNullOrWhiteSpace(request.Type) ? type.Type : Kind(request.Type);
        var prefix = string.IsNullOrWhiteSpace(request.SequencePrefix) ? type.SequencePrefix : Code(request.SequencePrefix, PrefixMessage);
        var name = Name(request.Name);
        if (type.IsSystem && (code != type.Code || kind != type.Type)) throw new BadRequestException(SystemFixedMessage);
        if (type.IsSystem && type.Type is JournalTypeKinds.Opening or JournalTypeKinds.Closing && !request.AutoPost)
            throw new BadRequestException(AutoPostFixedMessage);

        var used = await IsUsedAsync(type.Id, ct);
        var numbered = used || await _db.JournalEntrySequences.IgnoreQueryFilters().AnyAsync(s => s.JournalTypeId == type.Id, ct);
        if (prefix != type.SequencePrefix && numbered) throw new ConflictException(PrefixUsedMessage);
        if (kind != type.Type && used) throw new ConflictException(TypeUsedMessage);

        var debit = await DefaultAccountAsync(org, request.DefaultDebitAccountUuid, "debit", ct);
        var credit = await DefaultAccountAsync(org, request.DefaultCreditAccountUuid, "credit", ct);
        type.Code = code;
        type.Type = kind;
        type.SequencePrefix = prefix;
        type.Name = name;
        type.DefaultDebitAccountId = debit;
        type.DefaultCreditAccountId = credit;
        type.RequiresPartner = request.RequiresPartner;
        type.AutoPost = request.AutoPost;
        if (request.SortOrder is { } sort) type.SortOrder = sort;
        type.ModifiedBy = caller.UserIdOrNull;
        await EnsureUniqueAsync(org, type, type.Id, ct);
        await _db.SaveChangesAsync(ct);
        await PublishAsync(caller, type, "changed", ct);
        return await GetAsync(caller, type.Uuid, ct);
    }

    public async Task<JournalTypeModel> SetActiveAsync(AccountingCaller caller, Guid uuid, bool active, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var type = await _db.JournalTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrganizationId == caller.OrganizationId && t.Uuid == uuid, ct)
            ?? throw new NotFoundException(NotFoundMessage);
        // REV-6 — MISC is the resolver's fallback; OPEN and CLOSE take the opening and year-end entries.
        if (!active && type.IsSystem && KeptActiveCodes.Contains(type.Code))
            throw new ConflictException(KeepActiveMessage(type.Code));
        if (type.IsActive != active)
        {
            type.IsActive = active;
            type.ModifiedBy = caller.UserIdOrNull;
            await _db.SaveChangesAsync(ct);
            await PublishAsync(caller, type, active ? "activated" : "deactivated", ct);
        }
        return await GetAsync(caller, uuid, ct);
    }

    public async Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var type = await _db.JournalTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrganizationId == caller.OrganizationId && t.Uuid == uuid, ct)
            ?? throw new NotFoundException(NotFoundMessage);
        if (type.IsSystem) throw new ConflictException(SystemDeleteMessage);
        if (await IsUsedAsync(type.Id, ct)) throw new ConflictException(UsedDeleteMessage);
        await _db.InTransactionAsync(async () =>
        {
            // Counter rows of deleted drafts only (no entry uses the type).
            _db.JournalEntrySequences.RemoveRange(await _db.JournalEntrySequences.IgnoreQueryFilters().Where(s => s.JournalTypeId == type.Id).ToListAsync(ct));
            _db.JournalTypes.Remove(type);
            await _db.SaveChangesAsync(ct);
        }, ct);
        await PublishAsync(caller, type, "deleted", ct);
    }

    // ── rules ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Code(string? value, string message)
    {
        var v = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern.IsMatch(v)) throw new BadRequestException(message);
        return v;
    }

    private static string Name(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) throw new BadRequestException("The name is required.");
        if (v.Length > JournalLimits.MaxTypeNameLength) throw new BadRequestException("The name can be at most 100 characters.");
        return v;
    }

    private static string Kind(string? value) =>
        JournalTypeKinds.All.FirstOrDefault(k => string.Equals(k, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new BadRequestException(TypeMessage);

    private async Task<int?> DefaultAccountAsync(Guid org, Guid? uuid, string side, CancellationToken ct)
    {
        if (uuid is null) return null;
        var id = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Uuid == uuid && a.IsActive && !a.IsGroup && a.Level == 4).Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
        return id ?? throw new BadRequestException($"The default {side} account must be an active Level 4 account.");
    }

    /// <summary>Code and prefix unique per organization; one Opening and one Closing type (task 250).</summary>
    private async Task EnsureUniqueAsync(Guid org, JournalType type, int? exceptId, CancellationToken ct)
    {
        var others = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org && (exceptId == null || t.Id != exceptId)).ToListAsync(ct);
        if (others.Any(t => t.Code == type.Code)) throw new ConflictException($"Journal type code {type.Code} already exists.");
        if (others.FirstOrDefault(t => t.SequencePrefix == type.SequencePrefix) is { } p)
            throw new ConflictException($"Sequence prefix {type.SequencePrefix} is already used by journal type {p.Code}.");
        if (type.Type is JournalTypeKinds.Opening or JournalTypeKinds.Closing && others.Any(t => t.Type == type.Type))
            throw new ConflictException($"An organization can have only one {type.Type} journal type.");
    }

    private IQueryable<JournalType> Query(Guid org) =>
        _db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Include(t => t.DefaultDebitAccount).Include(t => t.DefaultCreditAccount)
            .Where(t => t.OrganizationId == org);

    private Task<bool> IsUsedAsync(int typeId, CancellationToken ct) =>
        _db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.JournalTypeId == typeId, ct);

    private async Task<HashSet<int>> UsedIdsAsync(Guid org, CancellationToken ct) =>
        (await _db.JournalEntries.IgnoreQueryFilters().Where(j => j.OrganizationId == org).Select(j => j.JournalTypeId).Distinct().ToListAsync(ct)).ToHashSet();

    private Task PublishAsync(AccountingCaller caller, JournalType type, string action, CancellationToken ct) =>
        _events.PublishAsync(new JournalTypeChangedEvent(caller.OrganizationId, type.Uuid, type.Code, action, caller.UserIdOrNull, caller.UserName), ct);

    internal static JournalTypeModel ToModel(JournalType t, bool used) => new()
    {
        Uuid                 = t.Uuid,
        Code                 = t.Code,
        Name                 = t.Name,
        Type                 = t.Type,
        SequencePrefix       = t.SequencePrefix,
        DefaultDebitAccount  = Ref(t.DefaultDebitAccount),
        DefaultCreditAccount = Ref(t.DefaultCreditAccount),
        RequiresPartner      = t.RequiresPartner,
        AutoPost             = t.AutoPost,
        IsSystem             = t.IsSystem,
        IsActive             = t.IsActive,
        SortOrder            = t.SortOrder,
        IsUsed               = used,
        CanDelete            = !t.IsSystem && !used,
        CreatedAt            = t.CreatedAt,
        ModifiedAt           = t.ModifiedAt,
    };

    private static AccountRefModel? Ref(ChartOfAccount? a) =>
        a is null ? null : new AccountRefModel { Uuid = a.Uuid, AccountCode = a.AccountCode, AccountName = a.AccountName, RootType = a.RootType };
}
